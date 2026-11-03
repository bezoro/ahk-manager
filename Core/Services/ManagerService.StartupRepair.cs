using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    public Task<StartupRepairPreview> PreviewStartupRepairAsync(
        CancellationToken cancellationToken = default
    ) =>
        Exclusive(
            async () =>
            {
                ReasonCode unavailable = StartupRepairUnavailable();
                if (
                    unavailable == ReasonCode.None
                    && !await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false)
                )
                    unavailable = ReasonCode.ConcurrentSettingsChange;
                if (unavailable != ReasonCode.None)
                    return new StartupRepairPreview(false, null, null, null, unavailable);
                var preview = startup.PreviewLegacyRunRepair(
                    document.Settings.LaunchManagerAtLogin,
                    document.Settings.LaunchManagerAsAdministrator
                );
                if (!preview.CanRepair)
                    return preview;
                string savedMode =
                    !document.Settings.LaunchManagerAtLogin
                        ? "Saved Windows startup is disabled, so the current Manager task will also be removed."
                    : document.Settings.LaunchManagerAsAdministrator
                        ? "The current administrator startup task will be preserved."
                    : "The current task will be replaced with standard-user startup, as saved in your preferences.";
                return preview with
                {
                    Detail = "Review the previous Run entry before removal. " + savedMode,
                };
            },
            cancellationToken
        );

    public Task<OperationResult> RepairStartupAsync(
        string fingerprint,
        bool confirmed,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                ReasonCode unavailable = StartupRepairUnavailable();
                if (unavailable != ReasonCode.None)
                    return new(Outcome.Failed, unavailable);
                if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false))
                    return new(Outcome.Failed, ReasonCode.ConcurrentSettingsChange);
                string? backupName = null;
                OperationResult? backupFailure = null;
                var backups = new StartupRepairBackupStore(DataRoot);
                var repaired = await startup
                    .RepairLegacyRunEntryAsync(
                        fingerprint,
                        confirmed,
                        document.Settings.LaunchManagerAtLogin,
                        document.Settings.LaunchManagerAsAdministrator,
                        async (backup, ct) =>
                        {
                            if (!await store.VerifyLoadedAsync(ct).ConfigureAwait(false))
                                return new OperationResult(
                                    Outcome.Failed,
                                    ReasonCode.ConcurrentSettingsChange
                                );
                            var saved = await backups.SaveAsync(backup, ct).ConfigureAwait(false);
                            if (saved.Succeeded)
                                backupName = saved.Detail;
                            else
                                backupFailure = saved;
                            if (
                                saved.Succeeded
                                && !await store.VerifyLoadedAsync(ct).ConfigureAwait(false)
                            )
                                return new OperationResult(
                                    Outcome.Failed,
                                    ReasonCode.ConcurrentSettingsChange
                                );
                            return saved;
                        },
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (repaired.Succeeded)
                {
                    try
                    {
                        if (
                            !await store
                                .VerifyLoadedAsync(CancellationToken.None)
                                .ConfigureAwait(false)
                        )
                            repaired = new(
                                Outcome.Partial,
                                ReasonCode.ConcurrentSettingsChange,
                                "The reviewed previous Run entry was removed, but saved preferences changed concurrently. The scheduled task was preserved."
                            );
                        else
                        {
                            // The review applies the durable preferences, never a renderer-owned draft.
                            var registered = await startup
                                .SetEnabledAsync(
                                    document.Settings.LaunchManagerAtLogin,
                                    true,
                                    document.Settings.LaunchManagerAsAdministrator,
                                    CancellationToken.None
                                )
                                .ConfigureAwait(false);
                            bool settingsCurrent = await store
                                .VerifyLoadedAsync(CancellationToken.None)
                                .ConfigureAwait(false);
                            repaired =
                                !settingsCurrent
                                    ? new(
                                        Outcome.Partial,
                                        ReasonCode.ConcurrentSettingsChange,
                                        "The reviewed previous Run entry was removed, but saved preferences changed during startup reconciliation. Review the current registration before retrying."
                                    )
                                : registered.Succeeded
                                    ? new(
                                        Outcome.Confirmed,
                                        Detail: "The reviewed previous Run entry was removed and the saved startup preferences were applied."
                                    )
                                : registered with
                                {
                                    Outcome = Outcome.Partial,
                                    Detail =
                                        "The reviewed previous Run entry was removed, but the saved startup preferences could not be confirmed. "
                                        + registered.Detail,
                                };
                        }
                    }
                    catch (Exception error) when (SafeJsonFiles.Known(error))
                    {
                        repaired = new(
                            Outcome.Partial,
                            ReasonCode.StorageFailure,
                            "The reviewed previous Run entry was removed, but the remaining startup state could not be confirmed. No rollback was attempted over concurrent settings or entries."
                        );
                    }
                }
                if (backupName is not null)
                    repaired = repaired with
                    {
                        Detail =
                            repaired.Detail
                            + " Backup: "
                            + StartupRepairBackupStore.DirectoryName
                            + "/"
                            + backupName,
                    };
                if (backupFailure?.Detail is { Length: > 0 } backupDetail)
                    repaired = repaired with
                    {
                        Detail = repaired.Detail + " Backup details: " + backupDetail,
                    };
                await Log(null, null, repaired, CancellationToken.None).ConfigureAwait(false);
                await Publish(false, CancellationToken.None).ConfigureAwait(false);
                return repaired;
            },
            cancellationToken
        );

    ReasonCode StartupRepairUnavailable() =>
        !loaded ? ReasonCode.CapabilityUnverified
        : snapshot.ReadOnly ? ReasonCode.ReadOnlySettings
        : !owner.Held || !owner.Trusted ? ReasonCode.EffectOwnerUnavailable
        : document.Settings.LaunchManagerAtLogin
        && document.Settings.LaunchManagerAsAdministrator
        && !processes.IsAdministrator
            ? ReasonCode.ManualElevationRequired
        : ReasonCode.None;
}
