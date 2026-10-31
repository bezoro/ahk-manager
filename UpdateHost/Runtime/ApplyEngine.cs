namespace Bezoro.AhkManager.UpdateHost;

internal sealed record InstallerRun(bool Exited, int? ExitCode, bool Dispatched = true);

internal sealed record BackupState(
    string Directory,
    string Version,
    string? FailedDirectory = null
);

internal interface IApplyEffects
{
    BackupState? RecoveryState => null;

    Task<bool> WaitForParentExit();
    void CheckCancellation();
    Task<BackupState> Backup();
    Task<InstallerRun> Install();
    void VerifyInstalled(string version);
    void Launch();
    Task<BackupState> Restore(BackupState backup);
}

internal static class ApplyEngine
{
    internal static async Task<UpdateOutcome> Run(
        UpdatePlan plan,
        IApplyEffects effects,
        Action<string>? log = null
    )
    {
        UpdateOutcome Result(
            string outcome,
            string detail,
            int? code = null,
            BackupState? backup = null,
            string? version = null
        ) =>
            new(
                1,
                plan.OperationId,
                outcome,
                detail,
                version ?? plan.Version,
                code,
                null,
                DateTimeOffset.UtcNow,
                backup?.Directory,
                backup?.FailedDirectory
            );
        BackupState backup;
        try
        {
            if (!await effects.WaitForParentExit().ConfigureAwait(false))
                return Result(
                    "Failed",
                    "Manager did not exit before the deadline. The installation was preserved."
                );
            effects.CheckCancellation();
            backup = await effects.Backup().ConfigureAwait(false);
            effects.CheckCancellation();
        }
        catch (OperationCanceledException)
        {
            return Result("Failed", "The update was cancelled before changing the installation.");
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex))
        {
            ApplyDiagnostics.Failure(log, "prepare-recovery-copy", ex);
            return Result(
                "Failed",
                "A recovery copy could not be prepared. The installation was preserved."
            );
        }

        InstallerRun installation;
        try
        {
            effects.CheckCancellation();
            installation = await effects.Install().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(
                "Failed",
                "The update was cancelled before changing the installation.",
                backup: backup
            );
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex))
        {
            ApplyDiagnostics.Failure(log, "install", ex);
            return Result(
                "Unknown",
                "The installer launch could not be confirmed. No recovery operation was replayed automatically.",
                backup: backup
            );
        }

        if (!installation.Exited)
            return Result(
                "Unknown",
                "The installer has not yet confirmed its exit. Do not run another update; inspect the operation before continuing.",
                backup: backup
            );
        if (!installation.Dispatched)
            return Result(
                "Failed",
                "The installer could not be started. The original installation was preserved.",
                backup: backup
            );
        if (installation.ExitCode == 0)
        {
            string stage = "verify-installed-version";
            try
            {
                effects.VerifyInstalled(plan.Version);
                stage = "launch-installed-manager";
                effects.Launch();
                return Result("Installed", "Update installed. Manager was reopened.", 0, backup);
            }
            catch (Exception ex) when (UpdateProtocol.Known(ex))
            {
                ApplyDiagnostics.Failure(log, stage, ex);
            }
        }

        try
        {
            backup = await effects.Restore(backup).ConfigureAwait(false);
            effects.VerifyInstalled(backup.Version);
            effects.Launch();
            return Result(
                "RolledBack",
                "The update did not complete. The previous installation was restored and Manager was reopened.",
                installation.ExitCode,
                backup,
                backup.Version
            );
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex))
        {
            ApplyDiagnostics.Failure(log, "restore-previous-installation", ex);
            return Result(
                "Unknown",
                "Recovery could not be confirmed. The installation copies were preserved; do not replay the update automatically.",
                installation.ExitCode,
                effects.RecoveryState ?? backup
            );
        }
    }
}
