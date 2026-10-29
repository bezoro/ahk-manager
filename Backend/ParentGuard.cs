using System.Management;
using System.Runtime.InteropServices;
using Bezoro.AhkManager.Core.Infrastructure;

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
        if (actual != expectedParentPid || actual <= 0)
            throw new ProtocolException("ParentMismatch");
        string expected = ScriptPaths.Canonical(
            Path.GetFullPath(Path.Combine(backendDirectory, "..", "Bezoro.AhkManager.exe")),
            false
        );
        parent = new(actual);
        self = new(Environment.ProcessId);
        try
        {
            if (
                !parent.TryRead(out parentStamp)
                || !self.TryRead(out childStamp)
                || !Compatible(
                    parentStamp,
                    childStamp,
                    Mode(parent),
                    Mode(self),
                    expected,
                    out var mode
                )
            )
                throw new ProtocolException("ParentMismatch");
            executableLease = new(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
            Proof = new(
                actual,
                parentStamp.Created,
                expected,
                Environment.ProcessId,
                childStamp.Created,
                parentStamp.Sid,
                parentStamp.Session,
                mode
            );
            if (!Validate())
                throw new ProtocolException("ParentMismatch");
        }
        catch
        {
            executableLease?.Dispose();
            parent.Dispose();
            self.Dispose();
            throw;
        }
    }

    static int ActualParent()
    {
        using var query = new ManagementObjectSearcher(
            "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + Environment.ProcessId
        );
        query.Options.Timeout = TimeSpan.FromSeconds(5);
        using var rows = query.Get();
        foreach (ManagementObject row in rows)
            using (row)
                return checked((int)(uint)row["ParentProcessId"]);
        throw new ProtocolException("ParentMismatch");
    }

    internal static AuthorityMode? TokenMode(ProcessSecurity security) =>
        security switch
        {
            { Elevated: false, IntegrityRid: >= 0x2000 and < 0x3000 } => AuthorityMode.Normal,
            { Elevated: true, IntegrityRid: >= 0x3000 and < 0x4000 } => AuthorityMode.Administrator,
            _ => null,
        };

    static AuthorityMode? Mode(NativeProcessIdentity process) =>
        process.TryReadSecurity(out var security) ? TokenMode(security) : null;

    internal static bool Compatible(
        ProcessStamp parent,
        ProcessStamp child,
        AuthorityMode? parentMode,
        AuthorityMode? childMode,
        string executable,
        out AuthorityMode mode
    )
    {
        mode = parentMode ?? AuthorityMode.Normal;
        return parent.Pid > 0
            && child.Pid > 0
            && parent.Pid != child.Pid
            && parent.Created > 0
            && parent.Created <= child.Created
            && parent.Sid.Length > 0
            && parent.Sid != "S-1-5-18"
            && parent.Sid == child.Sid
            && parent.Session == child.Session
            && ScriptPaths.Equal(parent.Executable, executable)
            && parentMode.HasValue
            && parentMode == childMode;
    }

    static bool Same(ProcessStamp a, ProcessStamp b) =>
        a.Pid == b.Pid
        && a.Created == b.Created
        && ScriptPaths.Equal(a.Executable, b.Executable)
        && a.Sid == b.Sid
        && a.Session == b.Session;

    public bool Validate() =>
        parent.TryRead(out var now)
        && self.TryRead(out var child)
        && Same(now, parentStamp)
        && Same(child, childStamp)
        && Mode(parent) == Proof.AuthorityMode
        && Mode(self) == Proof.AuthorityMode;

    public void Dispose()
    {
        executableLease?.Dispose();
        parent.Dispose();
        self.Dispose();
    }
}

internal static class ParentNative
{
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(
        nint token,
        int kind,
        nint buffer,
        int length,
        out int returned
    );

    [DllImport("advapi32.dll")]
    public static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    public static extern nint GetSidSubAuthority(nint sid, uint index);
}
