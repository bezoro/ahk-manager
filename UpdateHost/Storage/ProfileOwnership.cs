using System.Security.AccessControl;
using System.Security.Principal;

namespace Bezoro.AhkManager.UpdateHost;

internal static class ProfileOwnership
{
    internal static void Verify(string directory)
    {
        UpdatePaths.NoRedirects(directory);
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        var acl = new DirectoryInfo(directory).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access
        );
        if (
            new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl
            is null
        )
            throw new UnauthorizedAccessException("An unrestricted DACL is not trusted.");
        string owner =
            acl.GetOwner(typeof(SecurityIdentifier))?.Value
            ?? throw new UnauthorizedAccessException("Update cache owner is absent.");
        var trusted = new HashSet<string>(
            [sid, "S-1-5-18", "S-1-5-32-544"],
            StringComparer.Ordinal
        );
        if (!trusted.Contains(owner))
            throw new UnauthorizedAccessException("Update cache owner is not trusted.");
        const FileSystemRights writes =
            FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;
        foreach (
            FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
        )
            if (
                rule.AccessControlType == AccessControlType.Allow
                && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && (rule.FileSystemRights & writes) != 0
                && !trusted.Contains(rule.IdentityReference.Value)
            )
                throw new UnauthorizedAccessException(
                    "Update cache permits another user to write."
                );
    }
}
