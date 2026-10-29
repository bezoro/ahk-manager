using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    bool RecyclePending(Guid id, string path) =>
        journal.Records.Any(r =>
            r.Operation == ScriptOperation.RecycleFile
            && OperationJournal.IsPending(r)
            && (r.ScriptId == id || ScriptPaths.Equal(r.ScriptPath, path))
        );

    ReasonCode FileAuthority(ScriptRecord script)
    {
        if (store.ReadOnly)
            return ReasonCode.ReadOnlySettings;
        if (!owner.Held || !owner.Trusted)
            return ReasonCode.EffectOwnerUnavailable;
        if (
            !journal.Ready
            || RecyclePending(script.Id, script.Path)
            || journal.Records.Any(r =>
                OperationJournal.IsPending(r)
                && (r.ScriptId == script.Id || ScriptPaths.Equal(r.ScriptPath, script.Path))
            )
        )
            return ReasonCode.PendingOperation;
        if (script.LegacyGuard)
            return ReasonCode.LegacyGuard;
        if (!observed.Complete)
            return ReasonCode.ObservationIncomplete;
        return ReasonCode.None;
    }

    public Task<OperationResult> RelinkAsync(
        SelectionContext selection,
        string oldPath,
        string replacementPath,
        bool startupConsent,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                var script = document.Scripts.SingleOrDefault(s => s.Id == selection.ScriptId);
                if (
                    script is null
                    || !ValidSelection(selection, true)
                    || !ScriptPaths.Equal(script.Path, oldPath)
                )
                    return new(Outcome.Failed, ReasonCode.StaleSelection);
                if (File.Exists(oldPath))
                {
                    await Publish(true, cancellationToken).ConfigureAwait(false);
                    return new(Outcome.Failed, ReasonCode.StaleSelection);
                }
                await ObserveCore(cancellationToken).ConfigureAwait(false);
                var denied = FileAuthority(script);
                if (denied != ReasonCode.None)
                    return new(Outcome.Failed, denied);
                string path;
                try
                {
                    path = ScriptPaths.Canonical(replacementPath);
                }
                catch (Exception ex)
                    when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception)
                {
                    return new(Outcome.Failed, ReasonCode.InvalidPath, ex.Message);
                }
                if (document.Scripts.Any(s => s.Id != script.Id && ScriptPaths.Equal(s.Path, path)))
                    return new(Outcome.Failed, ReasonCode.InvalidPath);
                if (
                    RecyclePending(script.Id, path)
                    || journal.Records.Any(r =>
                        OperationJournal.IsPending(r) && ScriptPaths.Equal(r.ScriptPath, path)
                    )
                )
                    return new(Outcome.Failed, ReasonCode.PendingOperation);
                if (
                    observed.Processes.Any(p =>
                        ScriptPaths.Equal(p.Identity.ScriptPath, oldPath)
                        || ScriptPaths.Equal(p.Identity.ScriptPath, path)
                    )
                )
                    return new(Outcome.Failed, ReasonCode.AlreadyRunning);
                if (script.RunAtLogin == true && !startupConsent)
                    return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
                if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false))
                    return new(Outcome.Failed, ReasonCode.ConcurrentSettingsChange);
                if (File.Exists(oldPath))
                {
                    await Publish(true, cancellationToken).ConfigureAwait(false);
                    return new(Outcome.Failed, ReasonCode.StaleSelection);
                }
                return await Commit(
                        document with
                        {
                            Scripts = document
                                .Scripts.Select(s => s.Id == script.Id ? s with { Path = path } : s)
                                .ToList(),
                        },
                        script.Id,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            },
            cancellationToken
        );

    public Task<RecyclePlan> PrepareRecycleAsync(
        SelectionContext selection,
        CancellationToken cancellationToken = default
    ) =>
        Exclusive(
            async () =>
            {
                var script = document.Scripts.SingleOrDefault(s => s.Id == selection.ScriptId);
                ReasonCode denied =
                    script is null || !ValidSelection(selection, true)
                        ? ReasonCode.StaleSelection
                        : ReasonCode.None;
                ScriptFileIdentity? file = null;
                ProcessIdentity? identity = null;
                if (denied == ReasonCode.None)
                {
                    await ObserveCore(cancellationToken).ConfigureAwait(false);
                    denied = FileAuthority(script!);
                    var matches = observed
                        .Processes.Where(p =>
                            ScriptPaths.Equal(p.Identity.ScriptPath, script!.Path)
                        )
                        .ToArray();
                    if (denied == ReasonCode.None && matches.Length > 1)
                        denied = ReasonCode.MultipleInstances;
                    if (denied == ReasonCode.None && matches.Length == 1)
                    {
                        identity = matches[0].Identity;
                        denied = !matches[0].WindowVerified
                            ? ReasonCode.WindowUnverified
                            : Policy(script!, ScriptOperation.Stop, identity);
                    }
                    if (denied == ReasonCode.None)
                    {
                        try
                        {
                            file = ScriptPaths.FileIdentity(script!.Path);
                        }
                        catch (Exception ex)
                            when (SafeJsonFiles.Known(ex)
                                || ex is System.ComponentModel.Win32Exception
                            )
                        {
                            denied = ReasonCode.InvalidPath;
                        }
                    }
                }
                var plan = new RecyclePlan(
                    Guid.NewGuid(),
                    selection.ScriptId,
                    script?.Path ?? "",
                    file,
                    identity,
                    selection,
                    denied == ReasonCode.None,
                    denied
                );
                foreach (
                    var key in recyclePlans
                        .Where(p => DateTimeOffset.UtcNow - p.Value.Time > TimeSpan.FromMinutes(2))
                        .Select(p => p.Key)
                        .ToArray()
                )
                    recyclePlans.Remove(key);
                if (recyclePlans.Count < 128 && plan.Allowed)
                    recyclePlans[plan.OperationId] = (plan, DateTimeOffset.UtcNow);
                return plan;
            },
            cancellationToken
        );

    public Task<RecycleAuthorization> AuthorizeRecycleAsync(
        Guid operationId,
        SelectionContext selection,
        bool confirmed,
        CancellationToken cancellationToken = default
    ) =>
        Exclusive(
            async () =>
            {
                bool stopped = false;
                RecycleAuthorization Deny(
                    ReasonCode code,
                    string? detail = null,
                    Outcome outcome = Outcome.Failed
                ) =>
                    new(
                        new(
                            stopped && outcome != Outcome.Unknown ? Outcome.Partial : outcome,
                            code,
                            stopped ? "Execução encerrada. " + detail : detail,
                            operationId
                        ),
                        null,
                        stopped
                    );
                if (
                    !recyclePlans.Remove(operationId, out var held)
                    || DateTimeOffset.UtcNow - held.Time > TimeSpan.FromMinutes(2)
                    || held.Plan.Selection != selection
                    || !ValidSelection(selection, true)
                )
                    return Deny(ReasonCode.StaleSelection);
                if (!confirmed)
                    return Deny(ReasonCode.Cancelled, outcome: Outcome.CancelledBeforeDispatch);
                var plan = held.Plan;
                var script = document.Scripts.SingleOrDefault(s => s.Id == plan.ScriptId);
                if (script is null || !ScriptPaths.Equal(script.Path, plan.Path))
                    return Deny(ReasonCode.StaleSelection);
                try
                {
                    if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false))
                        return Deny(ReasonCode.ConcurrentSettingsChange);
                    await ObserveCore(cancellationToken).ConfigureAwait(false);
                    var denied = FileAuthority(script);
                    if (denied != ReasonCode.None)
                        return Deny(denied);
                    if (ScriptPaths.FileIdentity(plan.Path) != plan.File)
                        return Deny(ReasonCode.IdentityChanged);
                    var matches = observed
                        .Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, plan.Path))
                        .ToArray();
                    if (matches.Length > 0)
                    {
                        if (
                            matches.Length != 1
                            || plan.ExpectedIdentity is null
                            || !NativeProcessIdentity.Same(
                                matches[0].Identity,
                                plan.ExpectedIdentity
                            )
                        )
                            return Deny(ReasonCode.IdentityChanged);
                        denied = !matches[0].WindowVerified
                            ? ReasonCode.WindowUnverified
                            : Policy(script, ScriptOperation.Stop, plan.ExpectedIdentity);
                        if (denied != ReasonCode.None)
                            return Deny(denied);
                        var stop = await ExecuteProcess(
                                script,
                                ScriptOperation.Stop,
                                plan.ExpectedIdentity,
                                Guid.NewGuid(),
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        stopped = stop.StopConfirmed;
                        if (!stop.Result.Succeeded)
                            return Deny(stop.Result.Code, stop.Result.Detail, stop.Result.Outcome);
                    }
                    await ObserveCore(cancellationToken).ConfigureAwait(false);
                    if (!observed.Complete)
                        return Deny(ReasonCode.ObservationIncomplete);
                    if (
                        observed.Processes.Any(p =>
                            ScriptPaths.Equal(p.Identity.ScriptPath, plan.Path)
                        )
                    )
                        return Deny(ReasonCode.AlreadyRunning);
                    denied = FileAuthority(script);
                    if (denied != ReasonCode.None)
                        return Deny(denied);
                    if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false))
                        return Deny(ReasonCode.ConcurrentSettingsChange);
                    if (ScriptPaths.FileIdentity(plan.Path) != plan.File)
                        return Deny(ReasonCode.IdentityChanged);
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = new OperationRecord(
                        operationId,
                        script.Id,
                        ScriptOperation.RecycleFile,
                        plan.Path,
                        "",
                        plan.File!.Hash,
                        "",
                        owner.Sid,
                        owner.LoginKey,
                        DateTimeOffset.UtcNow,
                        Outcome.Pending,
                        plan.ExpectedIdentity,
                        RecycleStopped: stopped
                    );
                    var saved = await journal
                        .PutAsync(record, CancellationToken.None)
                        .ConfigureAwait(false);
                    await Publish(false, CancellationToken.None).ConfigureAwait(false);
                    return saved.Succeeded
                        ? new RecycleAuthorization(
                            new(Outcome.Pending, CorrelationId: operationId),
                            plan.File,
                            stopped
                        )
                        : Deny(saved.Code, saved.Detail, Outcome.Unknown);
                }
                catch (OperationCanceledException)
                {
                    return Deny(ReasonCode.Cancelled, outcome: Outcome.CancelledBeforeDispatch);
                }
                catch (Exception ex)
                    when (SafeJsonFiles.Known(ex)
                        || ex is System.ComponentModel.Win32Exception or InvalidOperationException
                    )
                {
                    return Deny(ReasonCode.StorageFailure, ex.Message);
                }
            },
            cancellationToken
        );

    public Task<OperationResult> CompleteRecycleAsync(
        Guid operationId,
        Outcome receipt,
        string? detail,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                var record = journal.Records.SingleOrDefault(r =>
                    r.Id == operationId && r.Operation == ScriptOperation.RecycleFile
                );
                if (
                    record is null
                    || receipt is not (Outcome.Confirmed or Outcome.Failed or Outcome.Unknown)
                )
                    return new(Outcome.Failed, ReasonCode.InvalidMetadata);
                if (
                    record.Outcome == Outcome.Confirmed
                    || record.RecycleReceipt
                    || !OperationJournal.IsPending(record)
                )
                    return new(record.Outcome, record.Code, CorrelationId: operationId);
                if (receipt != Outcome.Confirmed)
                {
                    var outcome =
                        receipt == Outcome.Unknown ? Outcome.Unknown
                        : record.RecycleStopped ? Outcome.Partial
                        : Outcome.Failed;
                    var result = new OperationResult(
                        outcome,
                        receipt == Outcome.Unknown
                            ? ReasonCode.PendingOperation
                            : ReasonCode.DispatchFailed,
                        (record.RecycleStopped ? "Execução encerrada. " : "") + detail,
                        operationId
                    );
                    var save = await journal
                        .PutAsync(
                            record with
                            {
                                Outcome = outcome,
                                Code = result.Code,
                            },
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                    await Log(
                            record.ScriptId,
                            ScriptOperation.RecycleFile,
                            result,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                    await Publish(false, CancellationToken.None).ConfigureAwait(false);
                    return save.Succeeded
                        ? result
                        : new(Outcome.Unknown, ReasonCode.StorageFailure, detail, operationId);
                }
                // Receipt is durable before catalog mutation; absence alone never reaches this branch.
                record = record with
                {
                    RecycleReceipt = true,
                };
                var received = await journal
                    .PutAsync(record, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!received.Succeeded)
                    return new(
                        Outcome.Unknown,
                        ReasonCode.StorageFailure,
                        "Reciclagem confirmada; recibo não persistido.",
                        operationId
                    );
                var script = document.Scripts.SingleOrDefault(s => s.Id == record.ScriptId);
                OperationResult committed;
                if (
                    File.Exists(record.ScriptPath)
                    || script is null
                    || !ScriptPaths.Equal(script.Path, record.ScriptPath)
                    || !await store.VerifyLoadedAsync(CancellationToken.None).ConfigureAwait(false)
                )
                    committed = new(
                        Outcome.Partial,
                        ReasonCode.ConcurrentSettingsChange,
                        "Reciclagem confirmada; revise e remova o registro da lista.",
                        operationId
                    );
                else
                    committed = await Commit(
                            document with
                            {
                                Scripts = document
                                    .Scripts.Where(s => s.Id != record.ScriptId)
                                    .ToList(),
                            },
                            record.ScriptId,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                var final = committed.Succeeded
                    ? new OperationResult(Outcome.Confirmed, CorrelationId: operationId)
                    : new(
                        Outcome.Partial,
                        committed.Code,
                        "Reciclagem confirmada; registro preservado. Remova da lista após corrigir o armazenamento.",
                        operationId
                    );
                var persisted = await journal
                    .PutAsync(
                        record with
                        {
                            Outcome = final.Outcome,
                            Code = final.Code,
                        },
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
                await Publish(false, CancellationToken.None).ConfigureAwait(false);
                return persisted.Succeeded
                    ? final
                    : new(Outcome.Unknown, ReasonCode.StorageFailure, final.Detail, operationId);
            },
            cancellationToken
        );
}
