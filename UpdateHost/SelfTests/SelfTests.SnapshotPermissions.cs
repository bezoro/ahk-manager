using System.Security.AccessControl;
using System.Security.Principal;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static async Task CheckSnapshotPermissions(string root, Action<bool, string> check)
    {
        await CheckFreshSnapshotProtection(root, check);
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

    static async Task CheckFreshSnapshotProtection(string root, Action<bool, string> check)
    {
        string parent = Path.Combine(root, "fresh-snapshot-protection");
        SnapshotSecurity.CreatePrivateDirectory(parent);
        string source = Path.Combine(parent, "source");
        SnapshotSecurity.CreatePrivateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        string relative = Path.Combine("nested", "payload.txt");
        await File.WriteAllTextAsync(Path.Combine(source, relative), "verified recovery bytes");
        string sourceDacl = SnapshotSecurity.Read(source, true),
            parentDacl = SnapshotSecurity.Read(parent, true);
        string backup = Path.Combine(parent, "backup");
        var snapshot = await InstallationSnapshot.Copy(
            source,
            backup,
            beforeFinalization: path =>
            {
                var acl = new DirectoryInfo(path).GetAccessControl();
                acl.SetAccessRuleProtection(false, true);
                new DirectoryInfo(path).SetAccessControl(acl);
                bool retained = false;
                try
                {
                    Directory.Move(path, path + "-replaced");
                }
                catch (IOException)
                {
                    retained = true;
                }
                check(retained, "fresh recovery root cannot be replaced during copy finalization");
            }
        );
        InstallationSnapshot.VerifyPrivate(backup, snapshot);
        InstallationSnapshot.Verify(backup, snapshot);
        check(
            true,
            "copy finalizes trusted inheritance drift with strict content and private DACL checks"
        );
        string staging = Path.Combine(parent, "rollback-staging");
        _ = await InstallationSnapshot.Copy(backup, staging);
        InstallationSnapshot.RestoreSecurity(staging, snapshot);
        InstallationSnapshot.Verify(staging, snapshot);
        InstallationSnapshot.VerifySecurity(staging, snapshot);
        check(true, "a finalized recovery copy retains normal rollback bytes and original ACLs");

        foreach (
            string fault in new[]
            {
                "content",
                "foreign-child",
                "foreign-access",
                "null-dacl",
                "trusted-explicit",
            }
        )
        {
            bool refused = false;
            try
            {
                await InstallationSnapshot.Copy(
                    source,
                    Path.Combine(parent, "refused-" + fault),
                    beforeFinalization: path =>
                    {
                        var directory = new DirectoryInfo(path);
                        var acl = directory.GetAccessControl();
                        acl.SetAccessRuleProtection(false, true);
                        directory.SetAccessControl(acl);
                        switch (fault)
                        {
                            case "content":
                                File.WriteAllText(
                                    Path.Combine(path, relative),
                                    "corrupted recovery bytes"
                                );
                                break;
                            case "foreign-access":
                                acl.AddAccessRule(
                                    new(
                                        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                                        FileSystemRights.ReadAndExecute,
                                        AccessControlType.Allow
                                    )
                                );
                                directory.SetAccessControl(acl);
                                break;
                            case "foreign-child":
                                var child = new FileInfo(Path.Combine(path, relative));
                                var childAcl = child.GetAccessControl();
                                childAcl.AddAccessRule(
                                    new(
                                        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                                        FileSystemRights.ReadAndExecute,
                                        AccessControlType.Allow
                                    )
                                );
                                child.SetAccessControl(childAcl);
                                check(
                                    child
                                        .GetAccessControl()
                                        .GetAccessRules(true, true, typeof(SecurityIdentifier))
                                        .Cast<FileSystemAccessRule>()
                                        .Any(rule =>
                                            rule.IdentityReference.Value == "S-1-1-0"
                                            && rule.AccessControlType == AccessControlType.Allow
                                        ),
                                    "foreign child access is effective before recovery finalization"
                                );
                                break;
                            case "null-dacl":
                                SnapshotSecurity.Write(path, true, "D:NO_ACCESS_CONTROL");
                                break;
                            case "trusted-explicit":
                                acl.AddAccessRule(
                                    new(
                                        new SecurityIdentifier(
                                            WellKnownSidType.LocalSystemSid,
                                            null
                                        ),
                                        FileSystemRights.ReadAndExecute,
                                        AccessControlType.Deny
                                    )
                                );
                                directory.SetAccessControl(acl);
                                break;
                        }
                    }
                );
            }
            catch (Exception error)
                when (error
                        is IOException
                            or UnauthorizedAccessException
                            or System.ComponentModel.Win32Exception
                )
            {
                refused = true;
            }
            check(refused, "fresh recovery finalization refuses " + fault);
            if (fault is "content" or "foreign-child")
                check(
                    !new DirectoryInfo(Path.Combine(parent, "refused-" + fault))
                        .GetAccessControl()
                        .AreAccessRulesProtected,
                    fault + " is refused before promoting the root DACL"
                );
        }
        check(
            SnapshotSecurity.Equivalent(sourceDacl, SnapshotSecurity.Read(source, true))
                && SnapshotSecurity.Equivalent(parentDacl, SnapshotSecurity.Read(parent, true))
                && await File.ReadAllTextAsync(Path.Combine(source, relative))
                    == "verified recovery bytes",
            "fresh recovery finalization preserves source and parent"
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
