using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.AgentCli;

internal sealed record AgentLayout(string Root, string Launcher, string Host, string Cli)
{
    internal static AgentLayout FromExecutable(string executable)
    {
        string cli = ScriptPaths.Canonical(executable, false);
        string? root = Path.GetDirectoryName(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cli)))
        );
        if (
            root is null
            || !ScriptPaths.Equal(
                cli,
                Path.Combine(root, "App", "backend", "agent", "ahk-manager.exe")
            )
        )
            throw new AgentFailure("InvalidInstallation");
        string host = ScriptPaths.Canonical(
            Path.Combine(root, "App", "Bezoro.AhkManager.exe"),
            false
        );
        var profile = RuntimeProfileLayout.ForHost(host);
        string launcher = ScriptPaths.Canonical(profile.StartupHostExecutable, false);
        _ = ScriptPaths.Canonical(Path.Combine(root, "portable-package.json"), false);
        return new AgentLayout(profile.PortableRoot, launcher, host, cli);
    }

    internal string PipeName(AgentPeer self)
    {
        string key = string.Create(
            CultureInfo.InvariantCulture,
            $"{Root.ToUpperInvariant()}\n{self.Stamp.Sid}\n{self.Stamp.Session}\n{self.Security.IntegrityRid}"
        );
        return "Bezoro.AhkManager.Agent.v1."
            + Convert.ToHexString(SHA256.HashData(AgentProtocol.Utf8.GetBytes(key)));
    }

    internal bool HasExistingHost()
    {
        var processes = Process.GetProcessesByName("Bezoro.AhkManager");
        try
        {
            foreach (var process in processes)
            {
                using var identity = new NativeProcessIdentity(process.Id);
                if (!identity.TryRead(out var stamp))
                {
                    if (!process.HasExited)
                        throw new AgentFailure("PeerUnverified");
                    continue;
                }
                if (
                    ScriptPaths.Equal(stamp.Executable, Host)
                    || ScriptPaths.Equal(stamp.Executable, Launcher)
                )
                    return true;
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}

internal sealed class AgentPeer : IDisposable
{
    readonly NativeProcessIdentity identity;
    internal ProcessStamp Stamp { get; }
    internal ProcessSecurity Security { get; }
    internal int Pid => Stamp.Pid;
    internal bool Administrator =>
        Security is { Elevated: true, IntegrityRid: >= 0x3000 and < 0x4000 }
        && Stamp.Sid != "S-1-5-18";

    internal AgentPeer(int pid)
    {
        identity = new NativeProcessIdentity(pid);
        if (identity.TryRead(out var stamp) && identity.TryReadSecurity(out var security))
        {
            Stamp = stamp;
            Security = security;
            return;
        }
        identity.Dispose();
        throw new AgentFailure("PeerUnverified");
    }

    internal bool Live =>
        identity.Matches(
            new ProcessIdentity(
                Stamp.Pid,
                Stamp.Created,
                Stamp.Executable,
                "",
                Stamp.Sid,
                Stamp.Session
            )
        )
        && identity.TryReadSecurity(out var current)
        && current == Security;

    internal void RequirePeer(AgentPeer local, string exactExecutable)
    {
        if (
            !Administrator
            || Security != local.Security
            || Stamp.Sid != local.Stamp.Sid
            || Stamp.Session != local.Stamp.Session
            || !ScriptPaths.Equal(Stamp.Executable, exactExecutable)
            || !Live
            || !local.Live
        )
            throw new AgentFailure("PeerUnverified");
    }

    internal static AgentPeer AuthenticateParent(AgentPeer child, AgentPeer local, string expected)
    {
        var parent = new AgentPeer(ParentPid(child.Pid));
        try
        {
            parent.RequirePeer(local, expected);
            if (
                parent.Pid == child.Pid
                || parent.Stamp.Created > child.Stamp.Created
                || !child.Live
            )
                throw new AgentFailure("PeerUnverified");
            return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    internal Process RetainObservableProcess()
    {
        var process = Process.GetProcessById(Pid);
        try
        {
            _ = process.SafeHandle;
            if (!Live || process.HasExited)
                throw new AgentFailure("PeerUnverified");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    static int ParentPid(int pid)
    {
        using var query = new ManagementObjectSearcher(
            "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId="
                + pid.ToString(CultureInfo.InvariantCulture)
        );
        query.Options.Timeout = TimeSpan.FromSeconds(4);
        using var rows = query.Get();
        foreach (ManagementObject row in rows)
            using (row)
                if (row["ParentProcessId"] is uint parent && parent is > 0 and <= int.MaxValue)
                    return (int)parent;
        throw new AgentFailure("PeerUnverified");
    }

    public void Dispose() => identity.Dispose();
}
