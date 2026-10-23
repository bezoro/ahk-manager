using System.Management;
using System.Runtime.InteropServices;
using Bezoro.AhkManager.WinUI.Infrastructure;

namespace Bezoro.AhkManager.Backend;

internal interface IParentGuard : IDisposable
{
    ParentProof Proof { get; }
    bool Validate();
}

internal sealed class ParentGuard : IParentGuard
{
    readonly NativeProcessIdentity parent;
    readonly NativeProcessIdentity self;
    readonly FileStream? executableLease;
    readonly ProcessStamp parentStamp;
    readonly ProcessStamp childStamp;
    public ParentProof Proof { get; }
    public ParentGuard(int expectedParentPid, string backendDirectory)
    {
        int actual = ActualParent();
        if (actual != expectedParentPid || actual <= 0) throw new ProtocolException("ParentMismatch");
        string expected = ScriptPaths.Canonical(Path.GetFullPath(Path.Combine(backendDirectory, "..", "Bezoro.AhkManager.exe")), false);
        parent = new(actual); self = new(Environment.ProcessId);
        try
        {
            if (!parent.TryRead(out parentStamp) || !self.TryRead(out childStamp) || parentStamp.Created > childStamp.Created || !ScriptPaths.Equal(parentStamp.Executable, expected) || parentStamp.Sid != childStamp.Sid || parentStamp.Session != childStamp.Session || !NormalToken(actual) || !NormalToken(Environment.ProcessId)) throw new ProtocolException("ParentMismatch");
            executableLease = new(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
            Proof = new(actual, parentStamp.Created, expected, Environment.ProcessId, childStamp.Created, parentStamp.Sid, parentStamp.Session);
            if (!Validate()) throw new ProtocolException("ParentMismatch");
        }
        catch { executableLease?.Dispose(); parent.Dispose(); self.Dispose(); throw; }
    }
    static int ActualParent()
    {
        using var query = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + Environment.ProcessId);
        query.Options.Timeout = TimeSpan.FromSeconds(5);
        using var rows = query.Get();
        foreach (ManagementObject row in rows) using (row) return checked((int)(uint)row["ParentProcessId"]);
        throw new ProtocolException("ParentMismatch");
    }
    static bool NormalToken(int pid)
    {
        using var process = ProcessNative.OpenProcess(ProcessNative.QuerySynchronize, false, pid);
        if (process.IsInvalid || !ProcessNative.OpenProcessToken(process, 8, out var token)) return false;
        using (token)
        {
            if (!ProcessNative.GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) || elevated != 0) return false;
            _ = ParentNative.GetTokenInformation(token.DangerousGetHandle(), 25, 0, 0, out int length);
            if (length is < 8 or > 4096) return false;
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!ParentNative.GetTokenInformation(token.DangerousGetHandle(), 25, buffer, length, out _)) return false;
                nint sid = Marshal.ReadIntPtr(buffer);
                nint count = ParentNative.GetSidSubAuthorityCount(sid);
                if (count == 0 || Marshal.ReadByte(count) == 0) return false;
                int level = Marshal.ReadInt32(ParentNative.GetSidSubAuthority(sid, (uint)(Marshal.ReadByte(count) - 1)));
                return level is >= 0x2000 and < 0x3000;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    static bool Same(ProcessStamp a, ProcessStamp b) => a.Pid == b.Pid && a.Created == b.Created && ScriptPaths.Equal(a.Executable, b.Executable) && a.Sid == b.Sid && a.Session == b.Session;
    public bool Validate() => parent.TryRead(out var now) && self.TryRead(out var child) && Same(now, parentStamp) && Same(child, childStamp) && NormalToken(parentStamp.Pid) && NormalToken(childStamp.Pid);
    public void Dispose() { executableLease?.Dispose(); parent.Dispose(); self.Dispose(); }
}

internal static class ParentNative
{
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(nint token, int kind, nint buffer, int length, out int returned);
    [DllImport("advapi32.dll")] public static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] public static extern nint GetSidSubAuthority(nint sid, uint index);
}
