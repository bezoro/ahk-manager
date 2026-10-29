using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class FileLease : IDisposable
{
    internal readonly FileStream Stream;
    internal readonly string Path;
    internal long Length => Stream.Length;
    FileLease(string path)
    {
        Path = UpdatePaths.Local(path); UpdatePaths.NoRedirects(Path);
        if ((File.GetAttributes(Path) & (FileAttributes.Directory | FileAttributes.Device)) != 0) throw new IOException("Regular update file required.");
        Stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try { if (!Native.GetFileInformationByHandle(Stream.SafeFileHandle, out var identity) || identity.Links != 1) throw new IOException("Multiply linked update file refused."); }
        catch { Stream.Dispose(); throw; }
    }
    internal static FileLease Open(string path, long maximumBytes)
    {
        var lease = new FileLease(path);
        if (lease.Length > maximumBytes) { lease.Dispose(); throw new InvalidDataException("Update file exceeds its size limit."); }
        return lease;
    }
    internal string Hash()
    {
        Stream.Position = 0; string hash = Convert.ToHexString(SHA256.HashData(Stream)).ToLowerInvariant(); Stream.Position = 0; return hash;
    }
    internal byte[] Read(int maximumBytes)
    {
        if (Length > maximumBytes) throw new InvalidDataException("Update document exceeds its size limit.");
        Stream.Position = 0; using var bytes = new MemoryStream(); Stream.CopyTo(bytes); Stream.Position = 0; return bytes.ToArray();
    }
    public void Dispose() => Stream.Dispose();
}

internal sealed class PackageLease : IDisposable
{
    internal const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    internal readonly FileLease Installer;
    readonly FileLease receipt;
    internal PackageLease(string path, string version, string sha256, string updatesRoot)
    {
        UpdatePaths.ValidateInstallerLocation(path, version, updatesRoot);
        Installer = FileLease.Open(path, MaximumInstallerBytes);
        try
        {
            if (Installer.Length == 0 || Installer.Hash() != sha256) throw new InvalidDataException("Installer checksum changed or installer is empty.");
            receipt = FileLease.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "receipt.json"), UpdateProtocol.MaximumPlanBytes);
            try
            {
                var proof = UpdateProtocol.Read<UpdateReceipt>(receipt.Read(UpdateProtocol.MaximumPlanBytes));
                if (proof.SchemaVersion != 1 || proof.UpdateProtocol != 1 || proof.Version != version || !UpdateProtocol.Hash(proof.Sha256, false) || !proof.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase) || proof.Bytes.HasValue && proof.Bytes.Value != Installer.Length) throw new InvalidDataException("Installer protocol receipt changed.");
            }
            catch { receipt.Dispose(); throw; }
        }
        catch { Installer.Dispose(); throw; }
    }
    internal void Verify(string hash) { if (Installer.Hash() != hash) throw new InvalidDataException("Pinned installer checksum changed."); }
    public void Dispose() { receipt.Dispose(); Installer.Dispose(); }
}

internal sealed class ParentLease : IDisposable
{
    readonly Process process;
    internal ParentLease(UpdatePlan plan)
    {
        using var self = Process.GetCurrentProcess();
        if (Native.NtQueryInformationProcess(self.SafeHandle, 0, out var basic, Marshal.SizeOf<Native.BasicInformation>(), out _) != 0 || basic.ParentPid.ToInt64() != plan.ParentPid) throw new InvalidDataException("Update host parent is not the Manager.");
        process = Process.GetProcessById(plan.ParentPid);
        try
        {
            if (!Native.GetProcessTimes(process.SafeHandle, out long created, out _, out _, out _) || DateTimeOffset.FromFileTime(created).ToUnixTimeMilliseconds() != plan.ParentStartedAtUtc.ToUnixTimeMilliseconds() || process.SessionId != self.SessionId) throw new InvalidDataException("Manager creation receipt changed.");
            var image = new StringBuilder(32768); int length = image.Capacity;
            if (!Native.QueryFullProcessImageName(process.SafeHandle, 0, image, ref length) || !UpdatePaths.Equal(image.ToString(), System.IO.Path.Combine(plan.InstallDirectory, "Bezoro.AhkManager.exe"))) throw new InvalidDataException("Manager image changed.");
            if (!Native.OpenProcessToken(process.SafeHandle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var parent = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
                if (parent.User is null || current.User is null || parent.User.Value != current.User.Value) throw new InvalidDataException("Manager user changed.");
        }
        catch { process.Dispose(); throw; }
    }
    internal bool Exited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    public void Dispose() => process.Dispose();
}

internal static class UpdateShutdownGate
{
    internal static void CheckCancellation(string operationDirectory, Guid operationId)
    {
        string path = Path.Combine(operationDirectory, "cancel.json");
        if (!File.Exists(path)) return;
        using var file = FileLease.Open(path, 1024);
        var cancel = UpdateProtocol.Read<UpdateCancel>(file.Read(1024));
        if (cancel.SchemaVersion != 1 || cancel.OperationId != operationId) throw new InvalidDataException("Cancellation identity changed.");
        throw new OperationCanceledException();
    }
    internal static async Task<bool> Wait(UpdatePlan plan, string operationDirectory, Func<bool> exited, Func<int> exitCode, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            CheckCancellation(operationDirectory, plan.OperationId);
            if (exited())
            {
                if (exitCode() != 0) throw new InvalidDataException("Manager did not exit normally.");
                using var file = FileLease.Open(Path.Combine(operationDirectory, "commit.json"), 4096);
                UpdateProtocol.ValidateCommit(plan, UpdateProtocol.Read<UpdateCommit>(file.Read(4096)));
                CheckCancellation(operationDirectory, plan.OperationId);
                return true;
            }
            if (clock.Elapsed >= timeout) return false;
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}

internal static class ProfileOwnership
{
    internal static void Verify(string directory)
    {
        UpdatePaths.NoRedirects(directory);
        using var identity = WindowsIdentity.GetCurrent(); string sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        var acl = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null) throw new UnauthorizedAccessException("An unrestricted DACL is not trusted.");
        string owner = acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? throw new UnauthorizedAccessException("Update cache owner is absent.");
        var trusted = new HashSet<string>([sid, "S-1-5-18", "S-1-5-32-544"], StringComparer.Ordinal);
        if (!trusted.Contains(owner)) throw new UnauthorizedAccessException("Update cache owner is not trusted.");
        const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 && (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value)) throw new UnauthorizedAccessException("Update cache permits another user to write.");
    }
}

internal sealed record RegistryValueSnapshot(string Name, RegistryValueKind Kind, string? Text = null, string[]? Strings = null, string? Binary = null, int? Dword = null, long? Qword = null);
internal sealed record RegistryNodeSnapshot(string RelativePath, RegistryValueSnapshot[] Values);
internal sealed record RegistryTreeSnapshot(string KeyPath, RegistryNodeSnapshot[] Nodes);
internal sealed record RegistrySnapshot(RegistryTreeSnapshot Installation, RegistryTreeSnapshot Uninstall, string Version);

internal static class InstallationRegistration
{
    static RegistryKey CurrentUser() => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    internal static string Verify(string installDirectory, string? expectedVersion = null)
    {
        using var user = CurrentUser(); using var installation = user.OpenSubKey(UpdateProtocol.RegistryPath); using var uninstall = user.OpenSubKey(UpdateProtocol.UninstallRegistryPath);
        if (installation?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string path || !UpdatePaths.Equal(path, installDirectory) || installation.GetValue("UpdateProtocol") is not int protocol || protocol != 1 || uninstall?.GetValue("DisplayVersion") is not string version || !UpdateProtocol.InstalledVersion(version) || expectedVersion is not null && !UpdateProtocol.SameVersion(version, expectedVersion)) throw new InvalidDataException("Registered installation does not support this update protocol.");
        if (uninstall.GetValue("UninstallString", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command || !command.StartsWith("\"" + installDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Uninstaller is outside the registered installation.");
        return version;
    }
    internal static RegistrySnapshot Capture(string installDirectory)
    {
        string version = Verify(installDirectory); using var user = CurrentUser();
        return new(CaptureTree(user, UpdateProtocol.RegistryPath), CaptureTree(user, UpdateProtocol.UninstallRegistryPath), version);
    }
    static RegistryTreeSnapshot CaptureTree(RegistryKey user, string path)
    {
        var nodes = new List<RegistryNodeSnapshot>(); var pending = new Stack<string>(); pending.Push(""); int values = 0;
        while (pending.Count > 0)
        {
            string relative = pending.Pop(); using var key = user.OpenSubKey(path + (relative.Length == 0 ? "" : "\\" + relative)) ?? throw new IOException("Installation registry changed.");
            var entries = new List<RegistryValueSnapshot>();
            foreach (string name in key.GetValueNames())
            {
                if (++values > 500) throw new IOException("Installation registry snapshot exceeds limit.");
                RegistryValueKind kind = key.GetValueKind(name); object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                entries.Add(kind switch
                {
                    RegistryValueKind.String or RegistryValueKind.ExpandString when value is string text && text.Length <= 32760 => new(name, kind, Text: text),
                    RegistryValueKind.MultiString when value is string[] strings && strings.Sum(s => s.Length) <= 32760 => new(name, kind, Strings: strings),
                    RegistryValueKind.DWord when value is int number => new(name, kind, Dword: number),
                    RegistryValueKind.QWord when value is long number => new(name, kind, Qword: number),
                    RegistryValueKind.Binary or RegistryValueKind.None when value is byte[] binary && binary.Length <= 65536 => new(name, kind, Binary: Convert.ToBase64String(binary)),
                    _ => throw new IOException("Unsupported installation registry value.")
                });
            }
            nodes.Add(new(relative, entries.ToArray()));
            if (nodes.Count > 50) throw new IOException("Installation registry snapshot exceeds limit.");
            foreach (string child in key.GetSubKeyNames()) pending.Push(relative.Length == 0 ? child : relative + "\\" + child);
        }
        return new(path, nodes.ToArray());
    }
    internal static void Restore(RegistrySnapshot snapshot, string installDirectory)
    {
        if (snapshot.Installation.KeyPath != UpdateProtocol.RegistryPath || snapshot.Uninstall.KeyPath != UpdateProtocol.UninstallRegistryPath) throw new InvalidDataException("Unexpected installation registry snapshot.");
        using var user = CurrentUser();
        using (var current = user.OpenSubKey(UpdateProtocol.RegistryPath))
            if (current?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string location && !UpdatePaths.Equal(location, installDirectory)) throw new InvalidDataException("Installation ownership changed before recovery.");
        using (var current = user.OpenSubKey(UpdateProtocol.UninstallRegistryPath))
            if (current?.GetValue("UninstallString", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command && !command.StartsWith("\"" + installDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Uninstall ownership changed before recovery.");
        foreach (var tree in new[] { snapshot.Installation, snapshot.Uninstall })
        {
            user.DeleteSubKeyTree(tree.KeyPath, false);
            foreach (var node in tree.Nodes)
            {
                using var key = user.CreateSubKey(tree.KeyPath + (node.RelativePath.Length == 0 ? "" : "\\" + node.RelativePath), true);
                foreach (var entry in node.Values)
                {
                    object value = entry.Kind switch
                    {
                        RegistryValueKind.String or RegistryValueKind.ExpandString => entry.Text!, RegistryValueKind.MultiString => entry.Strings!,
                        RegistryValueKind.DWord => entry.Dword!.Value, RegistryValueKind.QWord => entry.Qword!.Value,
                        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(entry.Binary!), _ => throw new InvalidDataException()
                    };
                    key.SetValue(entry.Name, value, entry.Kind);
                }
            }
        }
        Verify(installDirectory, snapshot.Version);
    }
}

internal static class Evidence
{
    internal static void WriteNew(string path, object value)
    {
        UpdatePaths.NoRedirects(path); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), UpdateProtocol.Json);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Update evidence exceeds limit.");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, false);
    }
}

internal sealed class OperationLog : IDisposable
{
    readonly StreamWriter output;
    int bytes;
    bool unavailable;
    internal OperationLog(string directory)
    {
        string path = Path.Combine(directory, "helper.log"); UpdatePaths.NoRedirects(path);
        output = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }
    internal void Write(string text)
    {
        if (unavailable) return;
        string line = DateTimeOffset.UtcNow.ToString("O") + " " + text[..Math.Min(text.Length, 2000)];
        if (bytes + Encoding.UTF8.GetByteCount(line) > 32768) return;
        try { output.WriteLine(line); bytes += Encoding.UTF8.GetByteCount(line) + 2; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { unavailable = true; }
    }
    public void Dispose()
    {
        try { output.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct FileStamp { public uint Attributes; public long Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicInformation { public nint Reserved, Peb, Reserved2, Reserved3, Pid, ParentPid; }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileStamp identity);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle handle, int flags, StringBuilder path, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token);
    [DllImport("ntdll.dll")] internal static extern int NtQueryInformationProcess(SafeProcessHandle handle, int kind, out BasicInformation basic, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")] internal static extern bool CreateHardLink(string link, string existing, nint reserved);
}
