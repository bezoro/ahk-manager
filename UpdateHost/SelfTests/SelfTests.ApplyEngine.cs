namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static async Task CheckApplyTransactions(
        string root,
        UpdatePlan plan,
        Action<bool, string> check
    )
    {
        foreach (
            string scenario in new[]
            {
                "success",
                "parent-alive",
                "cancel",
                "backup-failed",
                "promotion-unconfirmed",
                "promotion-refused",
                "promotion-failed",
                "verification-failed",
                "restore-failed",
                "launch-failed",
            }
        )
        {
            var fake = new FakeEffects(scenario, root);
            var diagnostics = new List<string>();
            var result = await ApplyEngine.Run(plan, fake, diagnostics.Add);
            string expected =
                scenario == "success" ? "Installed"
                : scenario is "promotion-unconfirmed" or "restore-failed" ? "Unknown"
                : scenario is "promotion-failed" or "verification-failed" or "launch-failed"
                    ? "RolledBack"
                : "Failed";
            check(
                result.Outcome == expected,
                "transaction result distinguishes confirmed installation recovery and uncertainty: "
                    + scenario
            );
            if (scenario is "parent-alive" or "cancel" or "backup-failed")
                check(
                    fake.Installs == 0 && fake.Restores == 0,
                    "unconfirmed exit cancellation or missing backup never starts installation: "
                        + scenario
                );
            if (scenario == "promotion-unconfirmed")
                check(
                    fake.Restores == 0 && fake.Launches == 0,
                    "a payload promotion without completion evidence is never raced by rollback or relaunch"
                );
            if (
                scenario
                is "promotion-failed"
                    or "verification-failed"
                    or "launch-failed"
                    or "restore-failed"
            )
                check(
                    fake.Restores == 1 && fake.Installs == 1,
                    "recovery is attempted once without replaying a payload promotion: " + scenario
                );
            string? failureStage = scenario switch
            {
                "backup-failed" => "prepare-recovery-copy",
                "verification-failed" => "verify-installed-version",
                "launch-failed" => "launch-installed-manager",
                "restore-failed" => "restore-previous-installation",
                _ => null,
            };
            if (failureStage is not null)
                check(
                    diagnostics.Any(line =>
                        line.Contains("stage=" + failureStage) && line.Contains("hresult=0x")
                    ),
                    "transaction diagnostics retain the original failure before recovery: "
                        + scenario
                );
        }
    }

    sealed class FakeEffects(string scenario, string root) : IApplyEffects
    {
        internal int Backups,
            Installs,
            Restores,
            Launches;
        internal Func<Task<bool>>? ShutdownGate;

        public Task<bool> WaitForParentExit() =>
            ShutdownGate?.Invoke() ?? Task.FromResult(scenario != "parent-alive");

        public void CheckCancellation()
        {
            if (scenario == "cancel")
                throw new OperationCanceledException();
        }

        public Task<BackupState> Backup()
        {
            Backups++;
            return scenario == "backup-failed"
                ? Task.FromException<BackupState>(new IOException())
                : Task.FromResult(new BackupState(Path.Combine(root, "backup"), "1.2.0"));
        }

        public Task<PackageApplyResult> Install()
        {
            Installs++;
            return Task.FromResult(
                scenario == "promotion-unconfirmed" ? new PackageApplyResult(false, null)
                : scenario == "promotion-refused" ? new(true, null, false)
                : new(true, scenario is "promotion-failed" or "restore-failed" ? 2 : 0)
            );
        }

        public void VerifyInstalled(string version)
        {
            if (scenario == "verification-failed" && version == "1.2.1")
                throw new InvalidDataException();
        }

        public void Launch()
        {
            Launches++;
            if (scenario == "launch-failed" && Launches == 1)
                throw new IOException();
        }

        public Task<BackupState> Restore(BackupState backup)
        {
            Restores++;
            return scenario == "restore-failed"
                ? Task.FromException<BackupState>(new IOException())
                : Task.FromResult(backup with { FailedDirectory = Path.Combine(root, "failed") });
        }
    }
}
