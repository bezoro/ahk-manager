using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Core.Infrastructure;

// Restricted to the current user's same-session medium Explorer shell. No arbitrary token,
// privilege enabling, credentials or elevated fallback is accepted here.
public static class UnelevatedProcessLauncher
{
    // CreateProcessWithTokenW accepts at most 1024 UTF-16 code units including
    // the terminator, unlike CreateProcessW's larger command-line limit.
    const int MaximumTokenCommandLength = 1023;

    sealed class ShellProof : IDisposable
    {
        public readonly nint Window;
        public readonly NativeProcessIdentity Identity;
        public readonly ProcessStamp Stamp;
        public readonly SafeAccessTokenHandle Token;
        public readonly FileStream Lease;

        public ShellProof(
            nint window,
            NativeProcessIdentity identity,
            ProcessStamp stamp,
            SafeAccessTokenHandle token,
            FileStream lease
        )
        {
            Window = window;
            Identity = identity;
            Stamp = stamp;
            Token = token;
            Lease = lease;
        }

        public bool Valid() =>
            LaunchNative.GetShellWindow() == Window
            && ProcessNative.GetWindowThreadProcessId(Window, out uint pid) != 0
            && pid == Stamp.Pid
            && Identity.TryRead(out var now)
            && now == Stamp
            && Identity.TryReadSecurity(out var security)
            && !security.Elevated
            && security.IntegrityRid == 0x2000;

        public void Dispose()
        {
            Lease.Dispose();
            Token.Dispose();
            Identity.Dispose();
        }
    }

    internal static string ExpectedExplorerPath(string? windowsDirectory = null) =>
        ExpectedSystemImagePath(
            "explorer.exe",
            windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        );

    static string ExpectedSystemImagePath(string name, string directory)
    {
        string path = ScriptPaths.LocalPath(Path.Combine(directory, name));
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Redirected shell image refused.");
            if (
                current == path
                && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0
            )
                throw new IOException("Regular shell image required.");
        }
        // Windows services its system image through legitimate hard links. This
        // exception is restricted to fixed Explorer/Notepad system images;
        // arbitrary editors, AHK scripts and interpreters remain strict.
        StringBuilder text = new(32768);
        if (ProcessNative.GetLongPathName(path, text, (uint)text.Capacity) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return text.ToString();
    }

    static ShellProof GetShell()
    {
        nint window = LaunchNative.GetShellWindow();
        if (window == 0 || ProcessNative.GetWindowThreadProcessId(window, out uint pid) == 0)
            throw new InvalidOperationException("Normal shell unavailable.");
        var shell = new NativeProcessIdentity(checked((int)pid));
        SafeAccessTokenHandle? token = null;
        FileStream? lease = null;
        try
        {
            using var self = new NativeProcessIdentity(Environment.ProcessId);
            string explorer = ExpectedExplorerPath();
            if (
                !shell.TryRead(out var stamp)
                || !self.TryRead(out var own)
                || !shell.TryReadSecurity(out var security)
                || !CompatibleShell(own, stamp, security, explorer)
            )
                throw new InvalidOperationException("Normal shell proof unavailable.");
            using var process = ProcessNative.OpenProcess(
                ProcessNative.QuerySynchronize,
                false,
                stamp.Pid
            );
            if (process.IsInvalid || !ProcessNative.OpenProcessToken(process, 8 | 2, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            lease = new(explorer, FileMode.Open, FileAccess.Read, FileShare.Read);
            var proof = new ShellProof(window, shell, stamp, token, lease);
            if (!proof.Valid())
                throw new InvalidOperationException("Normal shell changed.");
            return proof;
        }
        catch
        {
            lease?.Dispose();
            token?.Dispose();
            shell.Dispose();
            throw;
        }
    }

    static bool CanImpersonate()
    {
        using var process = ProcessNative.OpenProcess(
            ProcessNative.QuerySynchronize,
            false,
            Environment.ProcessId
        );
        if (process.IsInvalid || !ProcessNative.OpenProcessToken(process, 8, out var token))
            return false;
        using (token)
        {
            if (!LaunchNative.LookupPrivilegeValue(null, "SeImpersonatePrivilege", out var luid))
                return false;
            var privileges = new LaunchNative.PrivilegeSet
            {
                Count = 1,
                Control = 1,
                Luid = luid,
                Attributes = 2,
            };
            return LaunchNative.PrivilegeCheck(token, ref privileges, out bool enabled) && enabled;
        }
    }

    public static ReasonCode? UnavailableReason()
    {
        try
        {
            using var shell = GetShell();
            return CanImpersonate() ? null : ReasonCode.AccessDenied;
        }
        catch (Win32Exception)
        {
            return ReasonCode.AccessDenied;
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException)
        {
            return ReasonCode.CapabilityUnverified;
        }
    }

    internal static bool CompatibleShell(
        ProcessStamp own,
        ProcessStamp shell,
        ProcessSecurity security,
        string explorer
    ) =>
        own.Sid.Length > 0
        && own.Sid != "S-1-5-18"
        && own.Sid == shell.Sid
        && own.Session == shell.Session
        && ScriptPaths.Equal(shell.Executable, explorer)
        && !security.Elevated
        && security.IntegrityRid == 0x2000;

    internal static string BuildCommand(string executable, string script)
    {
        string command =
            ScriptPaths.QuoteArgument(executable) + " " + ScriptPaths.QuoteArgument(script);
        if (command.Length > MaximumTokenCommandLength)
            throw new InvalidOperationException(
                "Normal launch command exceeds the Windows token-launch limit."
            );
        return command;
    }

    public static Process? Launch(string interpreter, string script)
    {
        string executable = ScriptPaths.Canonical(interpreter, false),
            path = ScriptPaths.Canonical(script);
        string command = BuildCommand(executable, path);
        return LaunchVerified(executable, command, Path.GetDirectoryName(path)!);
    }

    // Editors have their own executable and argument list. They use the same
    // retained medium-shell token and creation receipt as a normal AHK launch.
    public static Process? LaunchExecutable(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory
    )
    {
        string local = ScriptPaths.LocalPath(executable);
        string notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "notepad.exe"
        );
        executable = ScriptPaths.Equal(local, notepad)
            ? ExpectedSystemImagePath("notepad.exe", Path.GetDirectoryName(notepad)!)
            : ScriptPaths.Canonical(local, false);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Executable required.");
        string directory = ScriptPaths.LocalPath(workingDirectory);
        WindowsStoragePath.NoRedirects(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException();
        return LaunchVerified(executable, BuildExecutableCommand(executable, arguments), directory);
    }

    internal static string BuildExecutableCommand(
        string executable,
        IReadOnlyList<string> arguments
    )
    {
        if (
            arguments.Count > 256
            || arguments.Any(argument => argument is null || argument.Contains('\0'))
        )
            throw new ArgumentException("Invalid editor arguments.");
        string command = string.Join(
            " ",
            arguments.Prepend(executable).Select(ScriptPaths.QuoteArgument)
        );
        if (command.Length > MaximumTokenCommandLength)
            throw new ArgumentException(
                "The editor command exceeds the 1023-character limit for launching with standard privileges."
            );
        return command;
    }

    static Process? LaunchVerified(string executable, string command, string directory)
    {
        using var executableLease = new FileStream(
            executable,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        using var shell = GetShell();
        if (!CanImpersonate())
            throw new Win32Exception(1314);
        if (!LaunchNative.DuplicateTokenEx(shell.Token, 0x02000000, 0, 2, 1, out var primary))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (primary)
        {
            if (!LaunchNative.CreateEnvironmentBlock(out nint environment, primary, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!shell.Valid())
                    throw new InvalidOperationException("Normal shell changed before launch.");
                var startup = new LaunchNative.StartupInfo
                {
                    Size = Marshal.SizeOf<LaunchNative.StartupInfo>(),
                    Desktop = @"winsta0\default",
                };
                if (
                    !LaunchNative.CreateProcessWithTokenW(
                        primary,
                        0,
                        executable,
                        new StringBuilder(command),
                        0x400,
                        environment,
                        directory,
                        ref startup,
                        out var created
                    )
                )
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                using var processHandle = new SafeProcessHandle(created.Process, true);
                using var threadHandle = new SafeWaitHandle(created.Thread, true);
                {
                    if (processHandle.IsInvalid || threadHandle.IsInvalid || created.Pid == 0)
                        return null;
                    using var child = new NativeProcessIdentity(checked((int)created.Pid));
                    if (
                        !ProcessNative.GetProcessTimes(
                            processHandle,
                            out long createdAt,
                            out _,
                            out _,
                            out _
                        )
                        || !child.TryRead(out var stamp)
                        || stamp.Created != createdAt
                        || stamp.Sid != shell.Stamp.Sid
                        || stamp.Session != shell.Stamp.Session
                        || !ScriptPaths.Equal(stamp.Executable, executable)
                        || !child.TryReadSecurity(out var security)
                        || security.Elevated
                        || security.IntegrityRid != 0x2000
                    )
                        return null;
                    return RetainCreationReceipt(processHandle, stamp.Pid);
                }
            }
            finally
            {
                LaunchNative.DestroyEnvironmentBlock(environment);
            }
        }
    }

    internal static Process? RetainCreationReceipt(
        SafeProcessHandle creation,
        int pid,
        Func<int, Process>? acquire = null,
        Func<SafeProcessHandle, long?>? creationTime = null
    )
    {
        static long? ReadCreated(SafeProcessHandle handle) =>
            ProcessNative.GetProcessTimes(handle, out long created, out _, out _, out _)
                ? created
                : null;
        var read = creationTime ?? ReadCreated;
        Process? retained = null;
        try
        {
            long? expected = read(creation);
            if (expected is null)
                return null;
            retained = (acquire ?? Process.GetProcessById)(pid);
            // Force the lazy Process handle open before the native creation handle is
            // released, then compare both receipts. The caller keeps this pinned handle.
            if (retained.Id != pid || read(retained.SafeHandle) != expected)
                return null;
            var result = retained;
            retained = null;
            return result;
        }
        catch (Exception ex)
            when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            retained?.Dispose();
        }
    }
}
