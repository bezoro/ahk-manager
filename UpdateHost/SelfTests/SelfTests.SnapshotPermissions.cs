using System.Security.AccessControl;
using System.Security.Principal;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static async Task CheckSnapshotPermissions(string root, Action<bool, string> check)
    {
        string aclParent = Path.Combine(root, "permission-parent");
        Directory.CreateDirectory(aclParent);
        var parentAcl = new DirectoryInfo(aclParent).GetAccessControl();
        parentAcl.AddAccessRule(
            new(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        new DirectoryInfo(aclParent).SetAccessControl(parentAcl);
        string secured = Path.Combine(aclParent, "original"),
            aclBackup = Path.Combine(root, "permission-backup"),
            staging = Path.Combine(aclParent, "restoration-staging");
        SnapshotSecurity.CreatePrivateDirectory(secured);
        var rootAcl = new DirectoryInfo(secured).GetAccessControl();
        rootAcl.AddAccessRule(
            new(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        new DirectoryInfo(secured).SetAccessControl(rootAcl);
        Directory.CreateDirectory(Path.Combine(secured, "inherited"));
        SnapshotSecurity.CreatePrivateDirectory(Path.Combine(secured, "protected"));
        await File.WriteAllTextAsync(
            Path.Combine(secured, "inherited", "inherited.txt"),
            "inherited read access"
        );
        string privateFile = Path.Combine(secured, "inherited", "protected-file.txt");
        await File.WriteAllTextAsync(privateFile, "explicit private access");
        var fileAcl = new FileInfo(privateFile).GetAccessControl();
        fileAcl.SetAccessRuleProtection(true, false);
        using (var identity = WindowsIdentity.GetCurrent())
            fileAcl.AddAccessRule(
                new(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow)
            );
        new FileInfo(privateFile).SetAccessControl(fileAcl);
        var aclManifest = await InstallationSnapshot.Copy(secured, aclBackup);
        _ = await InstallationSnapshot.Copy(aclBackup, staging);
        InstallationSnapshot.VerifyPrivate(aclBackup, aclManifest);
        InstallationSnapshot.VerifyPrivate(staging, aclManifest);
        check(
            SnapshotSecurity.Read(staging, true) != aclManifest.RootDacl,
            "backup and staging remain private under a parent granting Users full control"
        );
        try
        {
            InstallationSnapshot.RestoreSecurity(staging, aclManifest);
            InstallationSnapshot.VerifySecurity(staging, aclManifest);
        }
        catch
        {
            try
            {
                Evidence.WriteNew(
                    Path.Combine(root, "acl-restoration-diagnostics.json"),
                    new
                    {
                        Expected = aclManifest,
                        ActualParent = SnapshotSecurity.Read(aclParent, true),
                        ActualRoot = SnapshotSecurity.Read(staging, true),
                        ActualDirectories = aclManifest
                            .Directories.Select(directory => new
                            {
                                directory.RelativePath,
                                Dacl = SnapshotSecurity.Read(
                                    Path.Combine(staging, directory.RelativePath),
                                    true
                                ),
                            })
                            .ToArray(),
                        ActualFiles = aclManifest
                            .Files.Select(file => new
                            {
                                file.RelativePath,
                                Dacl = SnapshotSecurity.Read(
                                    Path.Combine(staging, file.RelativePath),
                                    false
                                ),
                            })
                            .ToArray(),
                    }
                );
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("self-test:acl-diagnostics:" + ex);
            }

            throw;
        }

        check(
            true,
            "restoration preserves root directory file and protected child DACLs with original inheritance flags"
        );
        string published = Path.Combine(aclParent, "restored");
        Directory.Move(staging, published);
        InstallationSnapshot.VerifySecurity(published, aclManifest);
        check(true, "publishing the restored directory does not broaden its original DACLs");
        string refusedStaging = Path.Combine(aclParent, "refused-staging");
        _ = await InstallationSnapshot.Copy(aclBackup, refusedStaging);
        parentAcl.AddAccessRule(
            new(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        new DirectoryInfo(aclParent).SetAccessControl(parentAcl);
        bool changedParent = false;
        try
        {
            InstallationSnapshot.RestoreSecurity(refusedStaging, aclManifest);
        }
        catch (IOException)
        {
            changedParent = true;
        }

        InstallationSnapshot.VerifyPrivate(refusedStaging, aclManifest);
        check(
            changedParent,
            "changed parent permissions refuse recovery while staging still has its private DACL"
        );
    }

    static void CheckProfilePermissions(string root, Action<bool, string> check)
    {
        string unrestricted = Path.Combine(root, "unrestricted-empty-fixture");
        SnapshotSecurity.CreatePrivateDirectory(unrestricted);
        string privateDacl = SnapshotSecurity.Read(unrestricted, true);
        try
        {
            SnapshotSecurity.Write(unrestricted, true, "D:NO_ACCESS_CONTROL");
            bool untrusted = false,
                notPrivate = false;
            try
            {
                ProfileOwnership.Verify(unrestricted);
            }
            catch (UnauthorizedAccessException)
            {
                untrusted = true;
            }

            try
            {
                SnapshotSecurity.VerifyPrivate(unrestricted, true);
            }
            catch (UnauthorizedAccessException)
            {
                notPrivate = true;
            }

            check(
                untrusted && notPrivate,
                "a null DACL granting everyone access is never mistaken for an empty private DACL"
            );
        }
        finally
        {
            SnapshotSecurity.Write(unrestricted, true, privateDacl);
        }
    }
}
