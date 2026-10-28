namespace Bezoro.AhkManager.UpdateHost;

internal sealed record InstallerRun(bool Exited, int? ExitCode, bool Dispatched = true);
internal sealed record BackupState(string Directory, string Version, string? FailedDirectory = null);
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
    internal static async Task<UpdateOutcome> Run(UpdatePlan plan, IApplyEffects effects)
    {
        UpdateOutcome Result(string outcome, string detail, int? code = null, BackupState? backup = null, string? version = null) => new(1, plan.OperationId, outcome, detail, version ?? plan.Version, code, null, DateTimeOffset.UtcNow, backup?.Directory, backup?.FailedDirectory);
        BackupState backup;
        try
        {
            if (!await effects.WaitForParentExit().ConfigureAwait(false)) return Result("Failed", "O Manager não encerrou no prazo. A instalação foi preservada.");
            effects.CheckCancellation();
            backup = await effects.Backup().ConfigureAwait(false);
            effects.CheckCancellation();
        }
        catch (OperationCanceledException) { return Result("Failed", "A atualização foi cancelada antes de alterar a instalação."); }
        catch (Exception ex) when (UpdateProtocol.Known(ex)) { return Result("Failed", "Não foi possível preparar uma cópia de recuperação. A instalação foi preservada."); }

        InstallerRun installation;
        try { effects.CheckCancellation(); installation = await effects.Install().ConfigureAwait(false); }
        catch (OperationCanceledException) { return Result("Failed", "A atualização foi cancelada antes de alterar a instalação.", backup: backup); }
        catch (Exception ex) when (UpdateProtocol.Known(ex)) { return Result("Unknown", "Não foi possível confirmar o início do instalador. Nenhuma recuperação foi repetida automaticamente.", backup: backup); }
        if (!installation.Exited) return Result("Unknown", "O instalador ainda não teve sua saída confirmada. Não execute outra atualização; verifique a operação antes de continuar.", backup: backup);
        if (!installation.Dispatched) return Result("Failed", "O instalador não pôde ser iniciado. A instalação original foi preservada.", backup: backup);
        if (installation.ExitCode == 0)
        {
            try { effects.VerifyInstalled(plan.Version); effects.Launch(); return Result("Installed", "Atualização instalada. O Manager foi reaberto.", 0, backup); }
            catch (Exception ex) when (UpdateProtocol.Known(ex)) { }
        }
        try
        {
            backup = await effects.Restore(backup).ConfigureAwait(false);
            effects.VerifyInstalled(backup.Version); effects.Launch();
            return Result("RolledBack", "A atualização não foi concluída. A instalação anterior foi restaurada e o Manager foi reaberto.", installation.ExitCode, backup, backup.Version);
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex)) { return Result("Unknown", "A recuperação não pôde ser confirmada. As cópias da instalação foram preservadas; não repita a atualização automaticamente.", installation.ExitCode, effects.RecoveryState ?? backup); }
    }
}
