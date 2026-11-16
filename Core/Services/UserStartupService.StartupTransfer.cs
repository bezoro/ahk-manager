using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class UserStartupService
{
    sealed record TransferTransaction(
        StartupTransferReview Review,
        Func<StartupRepairBackup, CancellationToken, Task<OperationResult>> Backup,
        Func<CancellationToken, Task<ReasonCode>> StateCurrent
    );

    internal Task<OperationResult> TransferReviewedEntryAsync(
        StartupTransferReview review,
        bool enabled,
        bool elevated,
        Func<StartupRepairBackup, CancellationToken, Task<OperationResult>> backup,
        Func<CancellationToken, Task<ReasonCode>> stateCurrent,
        CancellationToken ct
    ) =>
        SetEnabledCoreAsync(
            enabled,
            true,
            elevated,
            null,
            ct,
            new TransferTransaction(review, backup, stateCurrent)
        );

    bool TransferMatches(
        StartupTransferReview review,
        string? entry,
        StartupTaskRegistration? task,
        IUserStartupTaskAdapter? tasks
    ) =>
        tasks is not null
        && review.Entry == entry
        && review.Task == task
        && review.UserSid == tasks.UserSid
        && review.CurrentCommand == Command;

    static StartupTaskBackup? TaskBackup(StartupTaskRegistration? task) =>
        task is null
            ? null
            : new(
                task.Command,
                task.UserSid,
                task.Enabled,
                task.HighestPrivileges,
                task.InteractiveToken,
                task.UserLogonOnly,
                task.Source,
                task.Fingerprint,
                task.NonstandardWorkingDirectory,
                task.ParseIssue.ToString()
            );
}
