using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Bezoro.AhkManager.WinUI.Models;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

public static class ScriptPaths
{
    public static bool Equal(string? left, string? right) => left is not null && right is not null && StringComparer.OrdinalIgnoreCase.Equals(left, right);
    public static string Lexical(string path)
    {
        var full = LocalPath(path);
        if (!full.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("AHK file required.");
        return full;
    }
    public static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 32760 || path.Contains('"') || path.Any(c => c < 32 || "<>|*?".Contains(c))) throw new ArgumentException("Invalid absolute path.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.AsSpan(2).Contains(':')) throw new ArgumentException("Local drive required.");
        return full;
    }
    public static string Canonical(string path, bool script = true)
    {
        var full = script ? Lexical(path) : LocalPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected path refused.");
        if (!File.Exists(full)) throw new FileNotFoundException("File absent.", full);
        StringBuilder buffer = new(32768);
        if (ProcessNative.GetLongPathName(full, buffer, (uint)buffer.Capacity) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        full = buffer.ToString();
        using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!ProcessNative.GetFileInformationByHandle(handle, out var stamp) || stamp.Links != 1) throw new IOException("Uncertain or multiply linked file identity.");
        return full;
    }
    public static string Fingerprint(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64 * 1024 * 1024) throw new IOException("File exceeds fingerprint limit.");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
    public static string[] SplitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768 || command.Contains('\0') || char.IsWhiteSpace(command[0])) return [];
        bool quoted = false; int slashes = 0;
        foreach (char c in command) { if (c == '"' && slashes % 2 == 0) quoted = !quoted; slashes = c == '\\' ? slashes + 1 : 0; }
        if (quoted) return [];
        nint pointer = ProcessNative.CommandLineToArgvW(command, out int count);
        if (pointer == 0 || count is < 1 or > 256) { if (pointer != 0) ProcessNative.LocalFree(pointer); return []; }
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally { ProcessNative.LocalFree(pointer); }
    }
    public static string? ScriptFromCommand(string command, string executable)
    {
        var args = SplitCommand(command);
        if (args.Length < 2 || !Equal(args[0], executable)) return null;
        int index = args[1].Equals("/script", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (index >= args.Length || args[index].StartsWith('/') || !Path.IsPathFullyQualified(args[index])) return null;
        try { return Canonical(args[index]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception) { return null; }
    }
    // Used only for shell APIs which require one command line; normal launch uses ArgumentList.
    public static string QuoteArgument(string value)
    {
        StringBuilder text = new("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') text.Append('\\', slashes * 2 + 1).Append(c);
            else text.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}

public readonly record struct ProcessSecurity(bool Elevated, uint IntegrityRid);

public readonly record struct ProcessStamp(int Pid, long Created, string Executable, string Sid, int Session);

// The process handle stays open through argv acquisition, HWND proof, dispatch and exit observation.
public sealed class NativeProcessIdentity : IDisposable
{
    readonly SafeProcessHandle handle;
    public int Pid { get; }
    public NativeProcessIdentity(int pid) { Pid = pid; handle = ProcessNative.OpenProcess(ProcessNative.QuerySynchronize, false, pid); }
    public bool Live => !handle.IsInvalid && ProcessNative.WaitForSingleObject(handle, 0) == 258;
    public bool Exited => !handle.IsInvalid && ProcessNative.WaitForSingleObject(handle, 0) == 0;
    internal bool TryReadImage(out string image)
    {
        image = "";
        if (!Live) return false;
        StringBuilder text = new(32768); int capacity = text.Capacity;
        if (!ProcessNative.QueryFullProcessImageName(handle, 0, text, ref capacity) || !Live) return false;
        image = text.ToString(); return image.Length > 0;
    }
    public bool TryRead(out ProcessStamp stamp)
    {
        stamp = default;
        if (!Live || !ProcessNative.GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
        StringBuilder text = new(32768); int capacity = text.Capacity;
        if (!ProcessNative.QueryFullProcessImageName(handle, 0, text, ref capacity) || !ProcessNative.OpenProcessToken(handle, 8, out var token)) return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (!ProcessNative.GetTokenInformation(token, 12, out int session, sizeof(int), out int returned) || returned != sizeof(int)) return false;
            stamp = new((int)ProcessNative.GetProcessId(handle), created, text.ToString(), identity.User?.Value ?? "", session);
            return stamp.Pid == Pid && stamp.Created > 0 && stamp.Sid.Length > 0;
        }
    }
    public bool TryReadSecurity(out ProcessSecurity security)
    {
        security = default;
        if (!Live || !ProcessNative.OpenProcessToken(handle, 8, out var token)) return false;
        using (token)
        {
            if (!ProcessNative.GetTokenInformation(token, 20, out int elevated, sizeof(int), out int returned) || returned != sizeof(int)) return false;
            _ = ProcessNative.GetTokenInformation(token, 25, 0, 0, out int length);
            if (length is < 8 or > 4096) return false;
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!ProcessNative.GetTokenInformation(token, 25, buffer, length, out _)) return false;
                nint sid = Marshal.ReadIntPtr(buffer), count = ProcessNative.GetSidSubAuthorityCount(sid);
                if (count == 0 || Marshal.ReadByte(count) == 0) return false;
                uint level = unchecked((uint)Marshal.ReadInt32(ProcessNative.GetSidSubAuthority(sid, (uint)(Marshal.ReadByte(count) - 1))));
                security = new(elevated != 0, level);
                return Live;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    public ReasonCode? ControlUnavailableReason()
    {
        using var self = new NativeProcessIdentity(Environment.ProcessId);
        if (!TryReadSecurity(out var target) || !self.TryReadSecurity(out var current)) return ReasonCode.AccessDenied;
        return target.IntegrityRid > current.IntegrityRid ? ReasonCode.ManualElevationRequired : null;
    }
    public static bool Same(ProcessIdentity? left, ProcessIdentity? right) => left is not null && right is not null && left.Pid == right.Pid && left.CreationFileTime == right.CreationFileTime && ScriptPaths.Equal(left.ExecutablePath, right.ExecutablePath) && ScriptPaths.Equal(left.ScriptPath, right.ScriptPath) && left.UserSid == right.UserSid && left.SessionId == right.SessionId && left.ScriptHash == right.ScriptHash && left.InterpreterHash == right.InterpreterHash;
    public bool Matches(ProcessIdentity expected) => TryRead(out var stamp) && stamp.Pid == expected.Pid && stamp.Created == expected.CreationFileTime && ScriptPaths.Equal(stamp.Executable, expected.ExecutablePath) && stamp.Sid == expected.UserSid && stamp.Session == expected.SessionId;
    public async Task<bool> WaitForExitAsync(TimeSpan deadline)
    {
        // Caller cancellation cannot turn an already delivered request into no effect.
        return await Task.Run(() => ProcessNative.WaitForSingleObject(handle, checked((uint)deadline.TotalMilliseconds)) == 0).ConfigureAwait(false);
    }
    public void Dispose() => handle.Dispose();
}
