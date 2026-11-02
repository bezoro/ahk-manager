using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckPortableProtectionPermissions(string root, Action<bool, string> check)
    {
        string fixture = Path.Combine(root, "portable owner permissions");
        SnapshotSecurity.CreatePrivateDirectory(fixture);
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User ?? throw new UnauthorizedAccessException();
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null
        );
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        string directory = Path.Combine(fixture, "owned directory");
        string file = Path.Combine(fixture, "owned file.txt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(file, "portable permission fixture bytes");
        SetPortablePermissionFixture(directory, true, user, FileSystemRights.Modify);
        SetPortablePermissionFixture(file, false, user, FileSystemRights.Modify);
        string denied = Path.Combine(fixture, "explicitly denied directory");
        Directory.CreateDirectory(denied);
        SetPortablePermissionFixture(
            denied,
            true,
            user,
            FileSystemRights.ReadAndExecute,
            denyOwnerDacl: true
        );
        string deniedBefore = SnapshotSecurity.Read(denied, true);
        string deniedOwner = PortableFixtureOwner(denied, true);

        using (var restricted = PortablePermissionNative.Restricted(current.AccessToken))
            WindowsIdentity.RunImpersonated(
                restricted,
                () =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    check(
                        identity.User?.Value == user.Value
                            && !new WindowsPrincipal(identity).IsInRole(administrators),
                        "portable protection fixture uses the same account without enabled administrator membership"
                    );
                    bool directoryOwnerWrite = PortablePermissionDenied(() =>
                    {
                        var security = new DirectorySecurity();
                        security.SetOwner(user);
                        new DirectoryInfo(directory).SetAccessControl(security);
                    });
                    bool fileOwnerWrite = PortablePermissionDenied(() =>
                    {
                        var security = new FileSecurity();
                        security.SetOwner(user);
                        new FileInfo(file).SetAccessControl(security);
                    });
                    check(
                        directoryOwnerWrite && fileOwnerWrite,
                        "owned Modify-only paths refuse WRITE_OWNER under the actual restricted Windows token"
                    );
                    SnapshotSecurity.ProtectOwnedPath(directory, true);
                    SnapshotSecurity.ProtectOwnedPath(file, false);
                    check(
                        PortableFixtureOwner(directory, true) == user.Value
                            && PortableFixtureOwner(file, false) == user.Value
                            && File.ReadAllText(file) == "portable permission fixture bytes",
                        "portable protection restricts directory and file DACLs without changing their owner or data"
                    );
                    SnapshotSecurity.VerifyPrivate(directory, true, true);
                    SnapshotSecurity.VerifyPrivate(file, false, true);
                    check(
                        true,
                        "DACL-only protection retains the protected current-user SYSTEM and Administrators access policy"
                    );
                    check(
                        PortablePermissionDenied(() =>
                            SnapshotSecurity.ProtectOwnedPath(denied, true)
                        )
                            && SnapshotSecurity.Equivalent(
                                deniedBefore,
                                SnapshotSecurity.Read(denied, true)
                            )
                            && PortableFixtureOwner(denied, true) == deniedOwner,
                        "a caller denied WRITE_DAC cannot broaden portable permissions or change ownership"
                    );
                }
            );

        // The normal filesystem default may choose the user or Administrators
        // as owner. Protection must preserve either trusted result verbatim.
        string inherited = Path.Combine(root, "portable existing trusted owner");
        Directory.CreateDirectory(inherited);
        string originalOwner = PortableFixtureOwner(inherited, true);
        SnapshotSecurity.VerifyTrustedOwner(originalOwner, user.Value);
        SnapshotSecurity.ProtectOwnedPath(inherited, true);
        check(
            PortableFixtureOwner(inherited, true) == originalOwner,
            "portable protection preserves an existing trusted filesystem owner"
        );
        foreach (string owner in new[] { user.Value, system.Value, administrators.Value })
            SnapshotSecurity.VerifyTrustedOwner(owner, user.Value);
        string foreign = "S-1-5-21-2147483647-2147483647-2147483647-1234";
        check(
            PortablePermissionDenied(() => SnapshotSecurity.VerifyTrustedOwner(foreign, user.Value))
                && PortablePermissionDenied(() =>
                    SnapshotSecurity.VerifyTrustedOwner(system.Value, null)
                ),
            "portable ownership policy refuses another account and an unresolved current identity before ACL changes"
        );
        CheckInheritedPortableProtection(root, check);
    }

    static void CheckInheritedPortableProtection(string root, Action<bool, string> check)
    {
        var layout = CreatePortableFixture(
            Path.Combine(root, "portable inherited private protection"),
            "1.2.0"
        );
        string[] critical =
        [
            layout.Launcher,
            Path.Combine(layout.Root, PortableLayout.Descriptor),
            layout.Marker,
            Path.Combine(layout.Application, PortableLayout.Executable),
            Path.Combine(layout.Application, "backend", "AhkManager.Backend.exe"),
            Path.Combine(layout.Application, "backend", "update", "AhkManager.UpdateHost.exe"),
        ];
        InheritPortableProtection(layout.Application, true);
        InheritPortableProtection(layout.Data, true);
        foreach (string path in critical)
            InheritPortableProtection(path, false);
        check(
            !new DirectoryInfo(layout.Application).GetAccessControl().AreAccessRulesProtected
                && critical.All(path =>
                    !new FileInfo(path).GetAccessControl().AreAccessRulesProtected
                ),
            "portable startup fixture has inherited application and critical-file DACLs beneath its protected private root"
        );
        string note = Path.Combine(layout.Root, "notes.txt");
        File.WriteAllText(note, "preserve this unrelated user note");
        var paths = new[]
        {
            (Path: layout.Root, Directory: true),
            (Path: layout.Application, Directory: true),
            (Path: layout.Data, Directory: true),
        }
            .Concat(critical.Select(path => (Path: path, Directory: false)))
            .ToArray();
        var before = paths
            .Select(entry => new
            {
                entry.Path,
                entry.Directory,
                Dacl = SnapshotSecurity.Read(entry.Path, entry.Directory),
                Owner = PortableFixtureOwner(entry.Path, entry.Directory),
            })
            .ToArray();
        var contents = critical.ToDictionary(path => path, File.ReadAllBytes);
        PortableInitialization.Initialize(layout);
        PortableInitialization.InitializeData(layout);
        check(
            before.All(entry =>
                entry.Dacl == SnapshotSecurity.Read(entry.Path, entry.Directory)
                && entry.Owner == PortableFixtureOwner(entry.Path, entry.Directory)
            )
                && critical.All(path =>
                    contents[path].AsSpan().SequenceEqual(File.ReadAllBytes(path))
                )
                && File.ReadAllText(note) == "preserve this unrelated user note",
            "trusted inherited portable permissions are accepted without rewriting ACLs owners inventory bytes or unrelated files"
        );
        SnapshotSecurity.VerifyPrivate(layout.Root, true, true);
        string helper = critical[^1];
        var foreign = new FileInfo(helper).GetAccessControl();
        foreign.AddAccessRule(
            new(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                AccessControlType.Allow
            )
        );
        new FileInfo(helper).SetAccessControl(foreign);
        string foreignBefore = SnapshotSecurity.Read(helper, false);
        PortableRefused(
            () => PortableInitialization.Initialize(layout),
            check,
            "a foreign writer on an inherited portable executable prevents the private fast path and refuses an unproven container"
        );
        check(
            SnapshotSecurity.Read(helper, false) == foreignBefore
                && before.All(entry =>
                    entry.Path == helper
                    || entry.Dacl == SnapshotSecurity.Read(entry.Path, entry.Directory)
                )
                && critical.All(path =>
                    contents[path].AsSpan().SequenceEqual(File.ReadAllBytes(path))
                )
                && File.ReadAllText(note) == "preserve this unrelated user note",
            "refused portable protection leaves foreign-writer evidence unrelated user data and original permissions unchanged"
        );
    }

    static void InheritPortableProtection(string path, bool directory)
    {
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(false, false);
        foreach (
            FileSystemAccessRule rule in security.GetAccessRules(
                true,
                false,
                typeof(SecurityIdentifier)
            )
        )
            security.RemoveAccessRuleSpecific(rule);
        if (directory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    static string PortableFixtureOwner(string path, bool directory) =>
        (
            directory
                ? (FileSystemSecurity)
                    new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner)
        )
            .GetOwner(typeof(SecurityIdentifier))
            ?.Value
        ?? throw new UnauthorizedAccessException();

    static bool PortablePermissionDenied(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Win32Exception failure) when (failure.NativeErrorCode == 5)
        {
            return true;
        }
        catch (IOException failure) when (unchecked((uint)failure.HResult) == 0x80070005)
        {
            return true;
        }
    }

    static void SetPortablePermissionFixture(
        string path,
        bool directory,
        SecurityIdentifier user,
        FileSystemRights rights,
        bool denyOwnerDacl = false
    )
    {
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        if (denyOwnerDacl)
            security.AddAccessRule(
                new(
                    new SecurityIdentifier("S-1-3-4"),
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                    AccessControlType.Deny
                )
            );
        security.AddAccessRule(new(user, rights, AccessControlType.Allow));
        foreach (
            var sid in new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            }
        )
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        if (directory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    static class PortablePermissionNative
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SidAndAttributes
        {
            internal nint Sid;
            internal uint Attributes;
        }

        internal static SafeAccessTokenHandle Restricted(SafeAccessTokenHandle existing)
        {
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null
            );
            var bytes = new byte[administrators.BinaryLength];
            administrators.GetBinaryForm(bytes, 0);
            nint sid = Marshal.AllocHGlobal(bytes.Length);
            nint disabled = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
            try
            {
                Marshal.Copy(bytes, 0, sid, bytes.Length);
                Marshal.StructureToPtr(new SidAndAttributes { Sid = sid }, disabled, false);
                // DISABLE_MAX_PRIVILEGE removes restore/take-ownership privileges;
                // disabling Administrators prevents its FullControl ACE masking the bug.
                if (!CreateRestrictedToken(existing, 1, 1, disabled, 0, 0, 0, 0, out var token))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return token;
            }
            finally
            {
                Marshal.FreeHGlobal(disabled);
                Marshal.FreeHGlobal(sid);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateRestrictedToken(
            SafeAccessTokenHandle existing,
            uint flags,
            uint disableCount,
            nint disableSids,
            uint deletePrivilegeCount,
            nint privileges,
            uint restrictedCount,
            nint restrictedSids,
            out SafeAccessTokenHandle token
        );
    }
}
