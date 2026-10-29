using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.Backend;

internal static class StartupAdoptionPeer
{
    internal static bool Bootstrap(
        StartupAdoptionDescriptor descriptor,
        ParentProof parent,
        ProcessStamp own,
        ProcessStamp peer,
        ProcessSecurity security,
        string ownHash,
        string peerHash
    ) =>
        parent.AuthorityMode == AuthorityMode.Administrator
        && own.Pid == parent.ChildPid
        && own.Created == parent.ChildCreated
        && own.Sid == parent.UserSid
        && own.Session == parent.SessionId
        && peer.Pid == descriptor.BootstrapPid
        && peer.Created == descriptor.BootstrapCreated
        && peer.Sid == own.Sid
        && peer.Session == own.Session
        && ScriptPaths.Equal(peer.Executable, own.Executable)
        && ParentGuard.TokenMode(security) == AuthorityMode.Administrator
        && ownHash == peerHash;

    internal static bool Backend(
        ProcessStamp bootstrap,
        ProcessStamp expectedHost,
        ProcessStamp actualHost,
        ProcessStamp backend,
        ProcessSecurity hostSecurity,
        ProcessSecurity backendSecurity,
        int actualParent,
        string hostPath,
        string backendPath,
        string expectedHash,
        string actualHash
    ) =>
        actualHost == expectedHost
        && expectedHost.Sid == bootstrap.Sid
        && expectedHost.Session == bootstrap.Session
        && ScriptPaths.Equal(expectedHost.Executable, hostPath)
        && backend.Sid == bootstrap.Sid
        && backend.Session == bootstrap.Session
        && ScriptPaths.Equal(backend.Executable, backendPath)
        && actualParent == expectedHost.Pid
        && ParentGuard.TokenMode(hostSecurity) == AuthorityMode.Administrator
        && ParentGuard.TokenMode(backendSecurity) == AuthorityMode.Administrator
        && expectedHash == actualHash;
}
