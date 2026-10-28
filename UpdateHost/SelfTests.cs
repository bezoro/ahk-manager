using System.Text;
using System.Text.Json;

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
        Check(NativeApplyEffects.InstallerStartInfo(plan.InstallerPath, plan.InstallDirectory).ArgumentList.SequenceEqual(["/S", "--updated", "/D=" + plan.InstallDirectory]), "installer arguments preserve exact destination as the final argument without a shell");

        Directory.CreateDirectory(Path.GetDirectoryName(plan.InstallerPath)!);
        await File.WriteAllTextAsync(plan.InstallerPath, "inert package fixture; never executed");
        string packageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(plan.InstallerPath))).ToLowerInvariant();
        string receiptPath = Path.Combine(Path.GetDirectoryName(plan.InstallerPath)!, "receipt.json");
        var receipt = new UpdateReceipt(1, 1, plan.Version, packageHash.ToUpperInvariant(), new FileInfo(plan.InstallerPath).Length);
        await File.WriteAllBytesAsync(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, UpdateProtocol.Json));
        using (var verified = new PackageLease(plan.InstallerPath, plan.Version, packageHash, root)) Check(verified.Installer.Length > 0, "cached package accepts matching protocol receipt and uppercase NSIS checksum");
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
