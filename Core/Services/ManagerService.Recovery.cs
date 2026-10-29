using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    public Task<OperationResult> RecoverPendingAsync(
        CancellationToken cancellationToken = default
    ) => ResultAction(() => RecoverCore(cancellationToken), cancellationToken);

    async Task ReconcileObservedCore(CancellationToken ct)
    {
        if (!owner.Held || !owner.Trusted || !journal.Ready)
            return;
        if (!await ResolveObservedPendingCore(ct).ConfigureAwait(false))
            await SettleExpiredStartupContinuationsCore().ConfigureAwait(false);
    }

    async Task<bool> ResolveObservedPendingCore(CancellationToken ct)
    {
        bool unresolved = false;
        foreach (var record in journal.Records.Where(OperationJournal.IsPending).ToArray())
        {
            if (record.Operation == ScriptOperation.RecycleFile)
            {
                unresolved = true;
                continue;
            }
            var targets = observed
                .Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, record.ScriptPath))
                .ToArray();
            bool same =
                record.Identity is not null
                && targets.Any(p => NativeProcessIdentity.Same(p.Identity, record.Identity));
            Outcome? resolution =
                (
                    record.Operation == ScriptOperation.Start
                    || record.Operation == ScriptOperation.Restart && record.RestartOldExited
                ) && same
                    ? Outcome.Confirmed
                : observed.Complete && !targets.Any() ? Outcome.Partial
                : null;
            if (resolution is null)
            {
                unresolved = true;
                continue;
            }
            var saved = await journal
                .PutAsync(
                    record with
                    {
                        Outcome = resolution.Value,
                        Code =
                            resolution == Outcome.Partial
                                ? ReasonCode.LaunchUncertain
                                : ReasonCode.None,
                    },
                    ct
                )
                .ConfigureAwait(false);
            if (!saved.Succeeded)
            {
                unresolved = true;
                break;
            }
        }
        return unresolved;
    }

    async Task<OperationResult> RecoverCore(CancellationToken ct)
    {
        if (!owner.Held || !owner.Trusted)
            return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (!journal.Ready)
            return new(Outcome.Failed, ReasonCode.PendingOperation);
        await ObserveCore(ct).ConfigureAwait(false);
        bool unresolved = await ResolveObservedPendingCore(ct).ConfigureAwait(false);
        var scripts = document.Scripts.ToList();
        bool changed = false;
        for (int i = 0; i < scripts.Count; i++)
            if (scripts[i].LegacyGuard)
            {
                if (await importer.GuardClearedAsync(scripts[i], ct).ConfigureAwait(false))
                {
                    scripts[i] = scripts[i] with { LegacyGuard = false };
                    changed = true;
                }
            }
        if (changed)
        {
            var result = await Commit(document with { Scripts = scripts }, null, ct)
                .ConfigureAwait(false);
            if (!result.Succeeded)
                return result;
        }
        if (startupRecoveryPending && !await RecoverStartupContinuationCore().ConfigureAwait(false))
            unresolved = true;
        await Publish(false, ct).ConfigureAwait(false);
        return new(
            unresolved ? Outcome.Unknown : Outcome.Confirmed,
            unresolved ? ReasonCode.PendingOperation : ReasonCode.None
        );
    }
}
