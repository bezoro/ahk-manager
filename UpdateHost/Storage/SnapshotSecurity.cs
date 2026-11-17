using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bezoro.AhkManager.UpdateHost;

// Copies remain private in the update cache. Original DACLs are applied only to
// the complete, verified replacement on the original installation volume.
internal static class SnapshotSecurity
{
    internal static string Read(string path, bool directory) =>
        (
            directory
                ? (FileSystemSecurity)
                    new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access)
        ).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    internal static bool Equivalent(string expected, string actual)
    {
        var first = new RawSecurityDescriptor(expected);
        var second = new RawSecurityDescriptor(actual);
        // Windows may normalize the auto-inheritance bookkeeping flags when
        // persisting a DACL. Protection, presence, ACE order and every ACE byte
        // still have to match, including masks, SIDs and inheritance flags.
        const ControlFlags policy =
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected;
        if ((first.ControlFlags & policy) != (second.ControlFlags & policy))
            return false;
        var left = first.DiscretionaryAcl;
        var right = second.DiscretionaryAcl;
        if (left is null || right is null)
            return left is null && right is null;
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            byte[] a = new byte[left[i].BinaryLength],
                b = new byte[right[i].BinaryLength];
            left[i].GetBinaryForm(a, 0);
            right[i].GetBinaryForm(b, 0);
            if (!a.AsSpan().SequenceEqual(b))
                return false;
        }

        return true;
    }

    static DirectorySecurity PrivateDirectory(bool includeOwner = true)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException();
        var security = new DirectorySecurity();
        if (includeOwner)
            security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        foreach (
            var sid in new[]
            {
                user,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            }.Distinct()
        )
            security.AddAccessRule(
                new(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
        return security;
    }

    internal static void CreatePrivateDirectory(string path)
    {
        UpdatePaths.NoRedirects(path);
        byte[] descriptor = PrivateDirectory().GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = pin.AddrOfPinnedObject(),
            };
            // SECURITY_ATTRIBUTES sets the protected DACL at creation, before
            // any bytes exist. ERROR_ALREADY_EXISTS is a refusal, not reuse.
            if (!CreateDirectory(path, ref attributes))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Private recovery directory could not be created."
                );
        }
        finally
        {
            pin.Free();
        }

        VerifyPrivate(path, true, true);
    }

    internal static void ProtectOwnedPath(string path, bool directory)
    {
        UpdatePaths.NoRedirects(path);
        using var identity = WindowsIdentity.GetCurrent();
        string owner =
            (
                directory
                    ? (FileSystemSecurity)
                        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Owner)
            )
                .GetOwner(typeof(SecurityIdentifier))
                ?.Value
            ?? throw new UnauthorizedAccessException();
        VerifyTrustedOwner(owner, identity.User?.Value);
        // Restricting a proven container requires WRITE_DAC, not WRITE_OWNER.
        // Preserve its already trusted owner, including SYSTEM or Administrators.
        var security = PrivateDirectory(includeOwner: false);
        if (directory)
            new DirectoryInfo(path).SetAccessControl(security);
        else
        {
            var file = new FileSecurity();
            file.SetAccessRuleProtection(true, false);
            foreach (
                FileSystemAccessRule rule in security.GetAccessRules(
                    true,
                    true,
                    typeof(SecurityIdentifier)
                )
            )
                file.AddAccessRule(
                    new FileSystemAccessRule(
                        rule.IdentityReference,
                        rule.FileSystemRights,
                        AccessControlType.Allow
                    )
                );
            new FileInfo(path).SetAccessControl(file);
        }
        VerifyPrivate(path, directory, true);
    }

    internal static void VerifyTrustedOwner(string owner, string? userSid)
    {
        if (userSid is null || (owner != userSid && owner != "S-1-5-18" && owner != "S-1-5-32-544"))
            throw new UnauthorizedAccessException(
                "The portable container belongs to another user."
            );
    }

    internal static void VerifyPrivate(string path, bool directory, bool requireProtected = false)
    {
        UpdatePaths.NoRedirects(path);
        var security = directory
            ? (FileSystemSecurity)
                new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access
                )
            : new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access
            );
        VerifyPrivate(security, requireProtected);
    }

    static void VerifyPrivate(FileSystemSecurity security, bool requireProtected)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var trusted = new HashSet<string>(
            [
                identity.User?.Value ?? throw new UnauthorizedAccessException(),
                "S-1-5-18",
                "S-1-5-32-544",
            ],
            StringComparer.Ordinal
        );
        if (
            new RawSecurityDescriptor(
                security.GetSecurityDescriptorBinaryForm(),
                0
            ).DiscretionaryAcl
            is null
        )
            throw new UnauthorizedAccessException(
                "An unrestricted DACL cannot protect a recovery copy."
            );
        if (
            security.GetOwner(typeof(SecurityIdentifier))?.Value is not { } owner
            || !trusted.Contains(owner)
        )
            throw new UnauthorizedAccessException("Recovery copy owner changed.");
        if (requireProtected && !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("Recovery directory lost its protected DACL.");
        foreach (
            FileSystemAccessRule rule in security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier)
            )
        )
            if (
                rule.AccessControlType == AccessControlType.Allow
                && !trusted.Contains(rule.IdentityReference.Value)
            )
                throw new UnauthorizedAccessException(
                    "Recovery copy inherited access for another principal."
                );
    }

    internal static byte[] CaptureFreshDirectory(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access
        );
        VerifyPrivate(security, true);
        return security.GetSecurityDescriptorBinaryForm();
    }

    internal static bool FinalizeFreshDirectory(string path, byte[] originalDescriptor)
    {
        UpdatePaths.NoRedirects(path);
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access
        );
        VerifyPrivate(security, false);
        var original = new RawSecurityDescriptor(originalDescriptor, 0);
        var current = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (
            original.Owner != current.Owner
            || original.DiscretionaryAcl is not { } expected
            || current.DiscretionaryAcl is not { } actual
        )
            throw new UnauthorizedAccessException("Fresh recovery descriptor changed.");
        int explicitIndex = 0;
        foreach (GenericAce ace in actual)
        {
            if ((ace.AceFlags & AceFlags.Inherited) != 0)
            {
                if (!expected.Cast<GenericAce>().Any(candidate => SameAce(candidate, ace)))
                    throw new UnauthorizedAccessException(
                        "Fresh recovery inherited an unexpected ACE."
                    );
            }
            else if (explicitIndex >= expected.Count || !SameAce(expected[explicitIndex++], ace))
                throw new UnauthorizedAccessException(
                    "Fresh recovery explicit permissions changed."
                );
        }
        if (explicitIndex != expected.Count)
            throw new UnauthorizedAccessException("Fresh recovery explicit permissions changed.");
        bool changed = !security.AreAccessRulesProtected;
        if (changed)
        {
            // Freeze the descriptor just validated, preserving its owner and effective access.
            security.SetAccessRuleProtection(true, true);
            RemoveDuplicateAces(security);
            VerifyPrivate(security, true);
            new DirectoryInfo(path).SetAccessControl(security);
        }
        var persisted = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access
        );
        VerifyPrivate(persisted, true);
        if (
            security.GetOwner(typeof(SecurityIdentifier))
                != persisted.GetOwner(typeof(SecurityIdentifier))
            || !Equivalent(
                security.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                persisted.GetSecurityDescriptorSddlForm(AccessControlSections.Access)
            )
        )
            throw new UnauthorizedAccessException(
                "Fresh recovery descriptor changed during finalization."
            );
        return changed;
    }

    static void RemoveDuplicateAces(DirectorySecurity security)
    {
        // Windows coalesces exact duplicate inherited/explicit entries when
        // freezing inheritance. Prepare that same descriptor before writing it.
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var original = descriptor.DiscretionaryAcl!;
        var normalized = new RawAcl(original.Revision, original.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GenericAce ace in original)
        {
            ace.AceFlags &= ~AceFlags.Inherited;
            byte[] bytes = new byte[ace.BinaryLength];
            ace.GetBinaryForm(bytes, 0);
            if (seen.Add(Convert.ToHexString(bytes)))
                normalized.InsertAce(normalized.Count, ace);
        }
        descriptor.DiscretionaryAcl = normalized;
        byte[] result = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(result, 0);
        security.SetSecurityDescriptorBinaryForm(result, AccessControlSections.Access);
    }

    static bool SameAce(GenericAce expected, GenericAce actual)
    {
        byte[] first = new byte[expected.BinaryLength],
            second = new byte[actual.BinaryLength];
        expected.GetBinaryForm(first, 0);
        actual.GetBinaryForm(second, 0);
        // Inherited copies may only duplicate an original trusted ACE.
        first[1] &= unchecked((byte)~(byte)AceFlags.Inherited);
        second[1] &= unchecked((byte)~(byte)AceFlags.Inherited);
        return first.AsSpan().SequenceEqual(second);
    }

    internal static void Write(string path, bool directory, string dacl, bool protect = false)
    {
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
        if (protect)
            security.SetAccessRuleProtection(true, true);
        if (directory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        internal int Length;
        internal nint Descriptor;
        internal int InheritHandle;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateDirectoryW"
    )]
    static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
}
