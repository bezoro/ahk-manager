using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.AgentCli;

internal static class StableUpdateLauncher
{
    internal static async Task<AgentResponse> RequestConsent(
        AgentLayout layout,
        AgentPeer local,
        StableUpdateIdentity request,
        string version,
        CancellationToken ct
    )
    {
        Process? child;
        using (new StableUpdateLaunchLease(layout, local.Stamp.Executable, version))
        {
            var start = new ProcessStartInfo
            {
                FileName = layout.Cli,
                Arguments = StableUpdateHandoff.ChildParameters(request),
                WorkingDirectory = layout.Root,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (ct.IsCancellationRequested)
                return StableUpdateHandoff.BeforeLaunch(
                    request.OperationId,
                    version,
                    canceled: true
                );
            try
            {
                // Windows owns the consent decision. Keep the verified namespace leased
                // during ShellExecute, even if the original console is canceled.
                child = Process.Start(start);
            }
            catch (Win32Exception ex)
            {
                return StableUpdateHandoff.FromShellError(
                    request.OperationId,
                    version,
                    ex.NativeErrorCode
                );
            }
            catch (Exception ex) when (AgentFailure.Expected(ex))
            {
                return StableUpdateHandoff.FromExit(
                    request.OperationId,
                    version,
                    StableUpdateHandoff.ChildUnknown
                );
            }
        }

        // The launch-only file and directory handles must be released before the
        // elevated child can hand the updater ownership of the application tree.
        using (child)
        {
            int exit = StableUpdateHandoff.ChildUnknown;
            try
            {
                if (child is not null && !child.SafeHandle.IsInvalid)
                {
                    await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(90), ct);
                    exit = child.ExitCode;
                }
            }
            catch (Exception ex) when (AgentFailure.Expected(ex))
            {
                // Possible dispatch. Never terminate, retry, or borrow another PID.
            }
            return StableUpdateHandoff.FromExit(request.OperationId, version, exit);
        }
    }
}

internal sealed class StableUpdateLaunchLease : IDisposable
{
    const string CliInventoryPath = "App/backend/agent/ahk-manager.exe";
    const int MaximumInventoryBytes = 4 * 1024 * 1024;
    const long MaximumExecutableBytes = 512L * 1024 * 1024;
    readonly List<IDisposable> handles = [];

    internal StableUpdateLaunchLease(AgentLayout layout, string executable, string version)
    {
        try
        {
            RetainAncestors(layout.Cli);
            if (!ScriptPaths.Equal(ScriptPaths.Canonical(executable, false), layout.Cli))
                throw new AgentFailure("InvalidInstallation");
            var inventory = HoldFile(
                Path.Combine(layout.Root, "portable-package.json"),
                MaximumInventoryBytes
            );
            var binary = HoldFile(layout.Cli, MaximumExecutableBytes);
            byte[] bytes = new byte[checked((int)inventory.Length)];
            inventory.ReadExactly(bytes);
            VerifyInventory(bytes, version, binary);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void RetainAncestors(string executable)
    {
        Stack<string> ancestors = new();
        for (
            string? directory = Path.GetDirectoryName(executable);
            directory is not null;
            directory = Path.GetDirectoryName(directory)
        )
            ancestors.Push(directory);
        foreach (string directory in ancestors)
        {
            // FILE_LIST_DIRECTORY participates in rename sharing. Inspect each
            // entry without following a reparse point, from the volume down.
            var handle = CreateFile(@"\\?\" + directory, 0x81, 3, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            handles.Add(handle);
            WindowsStoragePath.VerifyHandle(handle);
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
                throw new AgentFailure("InvalidInstallation");
        }
    }

    FileStream HoldFile(string path, long limit)
    {
        // The entry lease rejects a redirection or rename during the data open.
        // The canonical checks also refuse multiple hard links.
        using var entry = WindowsStoragePath.OpenVerifiedMetadata(path, retainName: true);
        if (!ScriptPaths.Equal(path, ScriptPaths.Canonical(path, false)))
            throw new AgentFailure("InvalidInstallation");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        handles.Add(stream);
        WindowsStoragePath.VerifyHandle(stream.SafeFileHandle);
        if (stream.Length <= 0 || stream.Length > limit)
            throw new AgentFailure("InvalidInstallation");
        return stream;
    }

    static void VerifyInventory(byte[] bytes, string version, FileStream binary)
    {
        var inventory = AgentProtocol.Parse(bytes, JsonValueKind.Object);
        if (
            inventory.EnumerateObject().Count() != 6
            || !inventory.TryGetProperty("schemaVersion", out var schema)
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1
            || !inventory.TryGetProperty("applicationId", out var application)
            || application.GetString() != RuntimeProfileLayout.ApplicationId
            || !inventory.TryGetProperty("updateProtocol", out var protocol)
            || !protocol.TryGetInt32(out var updateProtocol)
            || updateProtocol != 2
            || !inventory.TryGetProperty("version", out var release)
            || release.GetString() != version
            || !inventory.TryGetProperty("commit", out var commit)
            || commit.GetString() is not { Length: 40 } revision
            || revision.Any(c => !char.IsAsciiHexDigitLower(c))
            || !inventory.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() is < 1 or > 10000
        )
            throw new AgentFailure("InvalidInstallation");
        JsonElement? selected = null;
        foreach (var entry in files.EnumerateArray())
        {
            if (
                entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("path", out var path)
                || path.ValueKind != JsonValueKind.String
            )
                throw new AgentFailure("InvalidInstallation");
            string? name = path.GetString();
            if (!string.Equals(name, CliInventoryPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (selected.HasValue || name != CliInventoryPath)
                throw new AgentFailure("InvalidInstallation");
            selected = entry;
        }
        if (
            selected is not { } binaryEntry
            || binaryEntry.EnumerateObject().Count() != 3
            || !binaryEntry.TryGetProperty("bytes", out var size)
            || !size.TryGetInt64(out long length)
            || length != binary.Length
            || !binaryEntry.TryGetProperty("sha256", out var hash)
            || !string.Equals(
                hash.GetString(),
                Convert.ToHexStringLower(SHA256.HashData(binary)),
                StringComparison.Ordinal
            )
        )
            throw new AgentFailure("InvalidInstallation");
    }

    public void Dispose()
    {
        for (int i = handles.Count - 1; i >= 0; i--)
            handles[i].Dispose();
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    static extern SafeFileHandle CreateFile(
        string name,
        uint access,
        uint sharing,
        nint security,
        uint disposition,
        uint flags,
        nint template
    );
}
