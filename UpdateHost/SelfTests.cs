using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bezoro.AhkManager.UpdateHost;

internal static class SelfTests
{
    internal static async Task<int> Run(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows update validation requires Windows.");
        root = UpdatePaths.Local(root); UpdatePaths.NoRedirects(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Self-test root must be absent.");
        Directory.CreateDirectory(root); int checks = 0;
        void Check(bool pass, string name) { if (!pass) throw new InvalidOperationException("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
        var plan = new UpdatePlan(1, Guid.NewGuid(), 42, DateTimeOffset.UtcNow, Path.Combine(root, "installation"), Path.Combine(root, "installers", "1.2.1", "installer.exe"), new string('a', 64), "1.2.1");
        UpdateProtocol.Validate(plan); UpdatePaths.ValidatePlanLocation(Path.Combine(root, "operations", plan.OperationId.ToString("D"), "plan.json"), plan, root);
        Check(UpdateProtocol.Read<UpdatePlan>(JsonSerializer.SerializeToUtf8Bytes(plan, UpdateProtocol.Json)) == plan, "update plan strict JSON roundtrip preserves operation and parent identity");
        foreach (var invalid in new[] { plan with { SchemaVersion = 2 }, plan with { OperationId = Guid.Empty }, plan with { ParentPid = 0 }, plan with { Version = "1.2.0-beta" }, plan with { InstallerSha256 = new string('A', 64) }, plan with { InstallerPath = "https://example.invalid/installer.exe" }, plan with { InstallDirectory = @"\\host\share" } })
        {
            bool refused = false; try { UpdateProtocol.Validate(invalid); } catch (Exception ex) when (UpdateProtocol.Known(ex)) { refused = true; }
            Check(refused, "invalid protocol identity version checksum or remote path is refused");
        }
        foreach (string invalid in new[] { "{\"schemaVersion\":1,\"schemaVersion\":1}", "{}", JsonSerializer.Serialize(plan, UpdateProtocol.Json).Replace("\"parentPid\":42", "\"parentPid\":42,\"unknown\":true", StringComparison.Ordinal) })
        {
            bool refused = false; try { _ = UpdateProtocol.Read<UpdatePlan>(Encoding.UTF8.GetBytes(invalid)); } catch (Exception ex) when (UpdateProtocol.Known(ex)) { refused = true; }
            Check(refused, "duplicate missing and unknown update properties do not become apply authority");
        }
        Check(UpdateProtocol.SameVersion("1.2", "1.2.0.0") && !UpdateProtocol.SameVersion("1.2.0", "1.2.1") && UpdateProtocol.Hash(new string('A', 64), false), "numeric version and receipt hash rules are explicit");
        foreach (string path in new[] { Path.Combine(root, "installers", "1.2.0", "installer.exe"), Path.Combine(root, "downloads", "installer.exe"), Path.Combine(root, "installers", "1.2.1", "other.exe") })
        {
            bool refused = false; try { UpdatePaths.ValidateInstallerLocation(path, plan.Version, root); } catch (InvalidDataException) { refused = true; }
            Check(refused, "only the exact immutable version cache can supply an installer");
        }
        string spacedDestination = Path.Combine(root, "Manager with spaces");
        var nsis = NativeApplyEffects.InstallerStartInfo(plan.InstallerPath, spacedDestination);
        Check(!nsis.UseShellExecute && nsis.ArgumentList.Count == 0 && nsis.Arguments == "/S --updated /D=" + spacedDestination, "NSIS receives its final destination with spaces unquoted and without a shell");
        foreach (string invalid in new[] { spacedDestination + "\r/S", spacedDestination + "\n/S", spacedDestination + "\0/S", spacedDestination + "\" /S" })
        {
            bool refused = false; try { _ = NativeApplyEffects.InstallerStartInfo(plan.InstallerPath, invalid); } catch (InvalidDataException) { refused = true; }
            Check(refused, "NSIS destination refuses command line control characters and quotes");
        }
        const string equivalentAcl = "D:P(A;OICI;FA;;;SY)(A;OICI;FR;;;BU)";
        Check(SnapshotSecurity.Equivalent(equivalentAcl, equivalentAcl.Replace("D:P", "D:PAI", StringComparison.Ordinal)), "DACL comparison ignores only auto-inheritance bookkeeping normalization");
        foreach (string changedAcl in new[] { equivalentAcl.Replace("D:P", "D:", StringComparison.Ordinal), equivalentAcl.Replace("FR", "FA", StringComparison.Ordinal), equivalentAcl.Replace("BU", "AU", StringComparison.Ordinal), equivalentAcl.Replace("OICI;FR", "OICIIO;FR", StringComparison.Ordinal), equivalentAcl.Replace("OICI;FR", "OICIID;FR", StringComparison.Ordinal), "D:P(A;OICI;FR;;;BU)(A;OICI;FA;;;SY)" })
            Check(!SnapshotSecurity.Equivalent(equivalentAcl, changedAcl), "DACL comparison preserves protection access masks principals inheritance and ACE order");

        Directory.CreateDirectory(Path.GetDirectoryName(plan.InstallerPath)!);
        await File.WriteAllTextAsync(plan.InstallerPath, "inert package fixture; never executed");
        string packageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(plan.InstallerPath))).ToLowerInvariant();
        string receiptPath = Path.Combine(Path.GetDirectoryName(plan.InstallerPath)!, "receipt.json");
        var receipt = new UpdateReceipt(1, 1, plan.Version, packageHash.ToUpperInvariant(), new FileInfo(plan.InstallerPath).Length);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, UpdateProtocol.Json));
        using (var verified = new PackageLease(plan.InstallerPath, plan.Version, packageHash, root)) Check(verified.Installer.Length > 0, "cached package accepts matching protocol receipt and uppercase NSIS checksum");
        Check(PackageLease.MaximumInstallerBytes == 1024L * 1024 * 1024, "installer size contract is one GiB independently of the snapshot file limit");
        string overLimit = Path.Combine(root, "oversized-inert-package.exe");
        using (var sparse = new FileStream(overLimit, FileMode.CreateNew, FileAccess.Write, FileShare.None)) sparse.SetLength(PackageLease.MaximumInstallerBytes + 1);
        bool oversized = false; try { using var invalid = FileLease.Open(overLimit, PackageLease.MaximumInstallerBytes); } catch (InvalidDataException) { oversized = true; }
        Check(oversized, "an installer exceeding one GiB is refused before hashing or execution");
        using (var fixture = new FileStream(overLimit, FileMode.Open, FileAccess.Write, FileShare.None)) fixture.SetLength(0);
        foreach (var changedReceipt in new[] { receipt with { UpdateProtocol = 0 }, receipt with { Version = "1.2.0" }, receipt with { Bytes = 1 }, receipt with { Sha256 = new string('b', 64) } })
        {
            await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(changedReceipt, UpdateProtocol.Json));
            bool refused = false; try { using var invalid = new PackageLease(plan.InstallerPath, plan.Version, packageHash, root); } catch (InvalidDataException) { refused = true; }
            Check(refused, "changed protocol version bytes or checksum cannot authorize cached package use");
        }

        foreach (string scenario in new[] { "success", "parent-alive", "cancel", "backup-failed", "installer-active", "installer-refused", "installer-failed", "verification-failed", "restore-failed", "launch-failed" })
        {
            var fake = new FakeEffects(scenario, root); var result = await ApplyEngine.Run(plan, fake);
            string expected = scenario == "success" ? "Installed" : scenario is "installer-active" or "restore-failed" ? "Unknown" : scenario is "installer-failed" or "verification-failed" or "launch-failed" ? "RolledBack" : "Failed";
            Check(result.Outcome == expected, "transaction result distinguishes confirmed installation recovery and uncertainty: " + scenario);
            if (scenario is "parent-alive" or "cancel" or "backup-failed") Check(fake.Installs == 0 && fake.Restores == 0, "unconfirmed exit cancellation or missing backup never starts installation: " + scenario);
            if (scenario == "installer-active") Check(fake.Restores == 0 && fake.Launches == 0, "an installer without exit evidence is never raced by rollback or relaunch");
            if (scenario is "installer-failed" or "verification-failed" or "launch-failed" or "restore-failed") Check(fake.Restores == 1 && fake.Installs == 1, "recovery is attempted once without replaying an installer: " + scenario);
        }

        string original = Path.Combine(root, "original"), copy = Path.Combine(root, "copy"); Directory.CreateDirectory(original); Directory.CreateDirectory(Path.Combine(original, "empty"));
        await File.WriteAllTextAsync(Path.Combine(original, "Bezoro.AhkManager.exe"), "inert owned fixture, never executed");
        await File.WriteAllTextAsync(Path.Combine(original, "foreign-user-file.txt"), "preserved unrelated data");
        var manifest = await InstallationSnapshot.Copy(original, copy); InstallationSnapshot.Verify(copy, manifest);
        Check(manifest.Files.Length == 2 && Directory.Exists(Path.Combine(copy, "empty")) && await File.ReadAllTextAsync(Path.Combine(copy, "foreign-user-file.txt")) == "preserved unrelated data", "snapshot preserves every regular file and empty directory including unrelated data");
        await File.WriteAllTextAsync(Path.Combine(copy, "foreign-user-file.txt"), "changed after backup");
        bool changedCopy = false; try { InstallationSnapshot.Verify(copy, manifest); } catch (IOException) { changedCopy = true; }
        Check(changedCopy, "a modified backup cannot authorize restoration");
        string aclParent = Path.Combine(root, "permission-parent"); Directory.CreateDirectory(aclParent);
        var parentAcl = new DirectoryInfo(aclParent).GetAccessControl();
        parentAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(aclParent).SetAccessControl(parentAcl);
        string secured = Path.Combine(aclParent, "original"), aclBackup = Path.Combine(root, "permission-backup"), staging = Path.Combine(aclParent, "restoration-staging");
        SnapshotSecurity.CreatePrivateDirectory(secured);
        var rootAcl = new DirectoryInfo(secured).GetAccessControl();
        rootAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(secured).SetAccessControl(rootAcl);
        Directory.CreateDirectory(Path.Combine(secured, "inherited"));
        SnapshotSecurity.CreatePrivateDirectory(Path.Combine(secured, "protected"));
        await File.WriteAllTextAsync(Path.Combine(secured, "inherited", "inherited.txt"), "inherited read access");
        string privateFile = Path.Combine(secured, "inherited", "protected-file.txt"); await File.WriteAllTextAsync(privateFile, "explicit private access");
        var fileAcl = new FileInfo(privateFile).GetAccessControl(); fileAcl.SetAccessRuleProtection(true, false);
        using (var identity = WindowsIdentity.GetCurrent()) fileAcl.AddAccessRule(new(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(privateFile).SetAccessControl(fileAcl);
        var aclManifest = await InstallationSnapshot.Copy(secured, aclBackup);
        _ = await InstallationSnapshot.Copy(aclBackup, staging);
        InstallationSnapshot.VerifyPrivate(aclBackup, aclManifest); InstallationSnapshot.VerifyPrivate(staging, aclManifest);
        Check(SnapshotSecurity.Read(staging, true) != aclManifest.RootDacl, "backup and staging remain private under a parent granting Users full control");
        try { InstallationSnapshot.RestoreSecurity(staging, aclManifest); InstallationSnapshot.VerifySecurity(staging, aclManifest); }
        catch
        {
            try
            {
                Evidence.WriteNew(Path.Combine(root, "acl-restoration-diagnostics.json"), new
                {
                    Expected = aclManifest,
                    ActualParent = SnapshotSecurity.Read(aclParent, true), ActualRoot = SnapshotSecurity.Read(staging, true),
                    ActualDirectories = aclManifest.Directories.Select(directory => new { directory.RelativePath, Dacl = SnapshotSecurity.Read(Path.Combine(staging, directory.RelativePath), true) }).ToArray(),
                    ActualFiles = aclManifest.Files.Select(file => new { file.RelativePath, Dacl = SnapshotSecurity.Read(Path.Combine(staging, file.RelativePath), false) }).ToArray()
                });
            }
            catch (Exception ex) { Console.Error.WriteLine("self-test:acl-diagnostics:" + ex); }
            throw;
        }
        Check(true, "restoration preserves root directory file and protected child DACLs with original inheritance flags");
        string published = Path.Combine(aclParent, "restored"); Directory.Move(staging, published); InstallationSnapshot.VerifySecurity(published, aclManifest);
        Check(true, "publishing the restored directory does not broaden its original DACLs");
        string refusedStaging = Path.Combine(aclParent, "refused-staging"); _ = await InstallationSnapshot.Copy(aclBackup, refusedStaging);
        parentAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(aclParent).SetAccessControl(parentAcl);
        bool changedParent = false; try { InstallationSnapshot.RestoreSecurity(refusedStaging, aclManifest); } catch (IOException) { changedParent = true; }
        InstallationSnapshot.VerifyPrivate(refusedStaging, aclManifest);
        Check(changedParent, "changed parent permissions refuse recovery while staging still has its private DACL");
        string unrestricted = Path.Combine(root, "unrestricted-empty-fixture"); SnapshotSecurity.CreatePrivateDirectory(unrestricted);
        string privateDacl = SnapshotSecurity.Read(unrestricted, true);
        try
        {
            SnapshotSecurity.Write(unrestricted, true, "D:NO_ACCESS_CONTROL");
            bool untrusted = false, notPrivate = false;
            try { ProfileOwnership.Verify(unrestricted); } catch (UnauthorizedAccessException) { untrusted = true; }
            try { SnapshotSecurity.VerifyPrivate(unrestricted, true); } catch (UnauthorizedAccessException) { notPrivate = true; }
            Check(untrusted && notPrivate, "a null DACL granting everyone access is never mistaken for an empty private DACL");
        }
        finally { SnapshotSecurity.Write(unrestricted, true, privateDacl); }
        string link = Path.Combine(original, "linked.txt");
        if (!Native.CreateHardLink(link, Path.Combine(original, "foreign-user-file.txt"), 0)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        bool linked = false; try { await InstallationSnapshot.Copy(original, Path.Combine(root, "refused-link-copy")); } catch (IOException) { linked = true; }
        Check(linked, "multiply linked installation files are refused before any installer effect");
        Console.WriteLine("UPDATE HOST checks=" + checks); return 0;
    }

    sealed class FakeEffects(string scenario, string root) : IApplyEffects
    {
        internal int Installs, Restores, Launches;
        public Task<bool> WaitForParentExit() => Task.FromResult(scenario != "parent-alive");
        public void CheckCancellation() { if (scenario == "cancel") throw new OperationCanceledException(); }
        public Task<BackupState> Backup() => scenario == "backup-failed" ? Task.FromException<BackupState>(new IOException()) : Task.FromResult(new BackupState(Path.Combine(root, "backup"), "1.2.0"));
        public Task<InstallerRun> Install()
        {
            Installs++; return Task.FromResult(scenario == "installer-active" ? new InstallerRun(false, null) : scenario == "installer-refused" ? new(true, null, false) : new(true, scenario is "installer-failed" or "restore-failed" ? 2 : 0));
        }
        public void VerifyInstalled(string version) { if (scenario == "verification-failed" && version == "1.2.1") throw new InvalidDataException(); }
        public void Launch() { Launches++; if (scenario == "launch-failed" && Launches == 1) throw new IOException(); }
        public Task<BackupState> Restore(BackupState backup)
        {
            Restores++; return scenario == "restore-failed" ? Task.FromException<BackupState>(new IOException()) : Task.FromResult(backup with { FailedDirectory = Path.Combine(root, "failed") });
        }
    }
}
