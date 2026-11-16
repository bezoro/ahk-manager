using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    internal Task<OperationResult> TransferStartupAsync(
        StartupTransferReview review,
        CancellationToken ct
    ) =>
        ResultAction(
            async () =>
            {
                var unavailable = await CheckCurrent(ct).ConfigureAwait(false);
                if (unavailable != ReasonCode.None)
                    return new(Outcome.Failed, unavailable);

                string? backupName = null;
                var backups = new StartupRepairBackupStore(DataRoot);
                var result = await startup
                    .TransferReviewedEntryAsync(
                        review,
                        document.Settings.LaunchManagerAtLogin,
                        document.Settings.LaunchManagerAsAdministrator,
                        async (backup, token) =>
                        {
                            var unavailable = await CheckCurrent(token).ConfigureAwait(false);
                            if (unavailable != ReasonCode.None)
                                return new OperationResult(Outcome.Failed, unavailable);
                            var saved = await backups
                                .SaveAsync(backup, token)
                                .ConfigureAwait(false);
                            if (saved.Succeeded)
                                backupName = saved.Detail;
                            return saved;
                        },
                        CheckCurrent,
                        ct
                    )
                    .ConfigureAwait(false);
                if (result.Succeeded)
                {
                    try
                    {
                        unavailable = await CheckCurrent(CancellationToken.None)
                            .ConfigureAwait(false);
                        if (unavailable != ReasonCode.None)
                            result = new(
                                Outcome.Partial,
                                unavailable,
                                "Startup was transferred, but the saved preferences or effect owner changed concurrently. Review the current registration before retrying."
                            );
                    }
                    catch (Exception error) when (SafeJsonFiles.Known(error))
                    {
                        result = new(
                            Outcome.Partial,
                            ReasonCode.StorageFailure,
                            "Startup was transferred, but the saved preferences could not be verified afterwards. Review the current registration before retrying."
                        );
                    }
                }
                if (backupName is not null)
                    result = result with
                    {
                        Detail =
                            result.Detail
                            + " Backup: "
                            + StartupRepairBackupStore.DirectoryName
                            + "/"
                            + backupName,
                    };
                await Log(null, null, result, CancellationToken.None).ConfigureAwait(false);
                await Publish(false, CancellationToken.None).ConfigureAwait(false);
                return result;

                async Task<ReasonCode> CheckCurrent(CancellationToken token)
                {
                    var unavailable = StartupRepairUnavailable();
                    if (unavailable != ReasonCode.None)
                        return unavailable;
                    if (
                        snapshot.Revision != review.Revision
                        || !document.Settings.LaunchManagerAtLogin
                        || owner.Sid != review.UserSid
                    )
                        return ReasonCode.StaleSelection;
                    return await store.VerifyLoadedAsync(token).ConfigureAwait(false)
                        ? ReasonCode.None
                        : ReasonCode.ConcurrentSettingsChange;
                }
            },
            ct
        );
}
