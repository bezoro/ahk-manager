using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.Backend;

/// <summary>Creates private leaf directories without modifying existing ancestors or ACLs.</summary>
internal static class TrustedProfileDirectory
{
    internal static void Ensure(string path)
    {
        path = RuntimeProfileLayout.Local(path);
        RuntimeProfileLayout.NoRedirects(path);
        if (!Directory.Exists(path))
        {
            try
            {
                CreateNew(path);
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 183)
            {
                // Another creator does not establish ownership. Verify the observed directory.
            }
        }
        Verify(path);
    }

    internal static void CreateNew(string path)
    {
        path = RuntimeProfileLayout.Local(path);
        RuntimeProfileLayout.NoRedirects(path);
        string parent =
            Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("A private data directory needs a parent.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The selected data location's parent is absent.");
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        var descriptor = new RawSecurityDescriptor(
            "D:P(A;OICI;FA;;;" + sid + ")(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"
        );
        byte[] bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        nint memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = memory,
            };
            if (!CreateDirectory(path, ref attributes))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
        Verify(path);
    }

    internal static void Verify(string path)
    {
        path = RuntimeProfileLayout.Local(path);
        RuntimeProfileLayout.NoRedirects(path);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("The portable data directory is unavailable.");
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        var acl = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access
        );
        if (
            new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl
            is null
        )
            throw new UnauthorizedAccessException(
                "The portable data directory has no private ACL."
            );
        var trusted = new HashSet<string>(
            [sid, "S-1-5-18", "S-1-5-32-544"],
            StringComparer.Ordinal
        );
        string owner = acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? "";
        if (!trusted.Contains(owner))
            throw new UnauthorizedAccessException(
                "The portable data directory owner is not trusted."
            );
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
                    "The portable data directory permits another user to write."
                );
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        internal int Length;
        internal nint Descriptor;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool Inherit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
}
