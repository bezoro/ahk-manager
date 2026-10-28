using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bezoro.AhkManager.UpdateHost;

// Copies remain private in the update cache. Original DACLs are applied only to
// the complete, verified replacement on the original installation volume.
internal static class SnapshotSecurity
{
    internal static string Read(string path, bool directory) => (directory
        ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access)).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    static DirectorySecurity PrivateDirectory()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException();
        var security = new DirectorySecurity(); security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static void CreatePrivateDirectory(string path)
    {
        UpdatePaths.NoRedirects(path);
        byte[] descriptor = PrivateDirectory().GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            // SECURITY_ATTRIBUTES sets the protected DACL at creation, before
            // any bytes exist. ERROR_ALREADY_EXISTS is a refusal, not reuse.
            if (!CreateDirectory(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Private recovery directory could not be created.");
        }
        finally { pin.Free(); }
        VerifyPrivate(path, true, true);
    }

    internal static void VerifyPrivate(string path, bool directory, bool requireProtected = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = new HashSet<string>([identity.User?.Value ?? throw new UnauthorizedAccessException(), "S-1-5-18", "S-1-5-32-544"], StringComparer.Ordinal);
        var security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access) : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null) throw new UnauthorizedAccessException("An unrestricted DACL cannot protect a recovery copy.");
        if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not { } owner || !trusted.Contains(owner)) throw new UnauthorizedAccessException("Recovery copy owner changed.");
        if (requireProtected && !security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Recovery directory lost its protected DACL.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value)) throw new UnauthorizedAccessException("Recovery copy inherited access for another principal.");
    }

    internal static void Write(string path, bool directory, string dacl, bool protect = false)
    {
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
        if (protect) security.SetAccessRuleProtection(true, true);
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { internal int Length; internal nint Descriptor; internal int InheritHandle; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
}
