using System.Security.Principal;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class PortableAdministratorRequiredException()
    : UnauthorizedAccessException("Open the root portable launcher with administrator access.") { }

internal static class PortableAdministratorAccess
{
    // Both roles come from native tokens after the real parent's SID and session
    // have been checked. A helper elevated independently cannot authorize a normal host.
    internal static void Require(bool? helperAdministrator, bool? parentAdministrator)
    {
        if (helperAdministrator != true || parentAdministrator != true)
            throw new PortableAdministratorRequiredException();
    }

    internal static bool? Read(WindowsIdentity identity)
    {
        try
        {
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception failure) when (UpdateProtocol.Known(failure))
        {
            return null;
        }
    }
}
