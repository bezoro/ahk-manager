using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    bool ValidSelection(SelectionContext selection, bool safe)
    {
        if (selection.StateRevision != snapshot.Revision)
            return false;
        var projection = ScriptViews.Project(snapshot, selection.Query, selection.ScriptId);
        return safe
            ? projection.VisibleIds.Contains(selection.ScriptId)
            : projection.EffectiveSelectedId == selection.ScriptId;
    }

    ReasonCode Policy(ScriptRecord script, ScriptOperation operation, ProcessIdentity? expected)
    {
        if (operation is ScriptOperation.Edit or ScriptOperation.OpenFolder)
            return File.Exists(script.Path) ? ReasonCode.None : ReasonCode.MissingFile;
        if (operation == ScriptOperation.Recover)
            return ReasonCode.None;
        if (operation == ScriptOperation.RecycleFile)
            return ReasonCode.CapabilityUnverified;
        if (RecyclePending(script.Id, script.Path))
            return ReasonCode.PendingOperation;
        if (startupRecoveryPending && !startupResumeActive)
            return ReasonCode.PendingOperation;
        if (!owner.Held || !owner.Trusted)
            return ReasonCode.EffectOwnerUnavailable;
        if (store.ReadOnly)
            return ReasonCode.ReadOnlySettings;
        if (
            !journal.Ready
            || journal.Records.Any(r =>
                OperationJournal.IsPending(r)
                && (
                    r.Operation is ScriptOperation.Start or ScriptOperation.Restart
                    || r.ScriptId == script.Id
                        && (r.Operation == operation || r.Operation == ScriptOperation.Stop)
                )
            )
        )
            return ReasonCode.PendingOperation;
        if (script.LegacyGuard)
            return ReasonCode.LegacyGuard;
        if (!processes.Supports(operation))
            return ReasonCode.CapabilityUnverified;
        var matches = observed
            .Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, script.Path))
            .ToArray();
        if (operation is ScriptOperation.Start or ScriptOperation.Restart)
        {
            if (
                !script.RunAsAdministrator
                && processes.GetNormalLaunchUnavailableReason() is ReasonCode launchDenied
            )
                return launchDenied;
            if (script.LaunchBlocked)
                return ReasonCode.LegacyGuard;
            if (!File.Exists(script.Path))
                return ReasonCode.MissingFile;
            if (!observed.Complete)
                return ReasonCode.ObservationIncomplete;
            if (
                string.IsNullOrWhiteSpace(
                    script.InterpreterPath ?? document.Settings.InterpreterPath
                )
            )
                return ReasonCode.UnsupportedInterpreter;
            if (
                script.RunAsAdministrator
                && operation == ScriptOperation.Restart
                && !processes.IsAdministrator
            )
                return ReasonCode.ManualElevationRequired;
            if (
                observed.Processes.Any(p => IsSupervisorRelated(p.Identity.ScriptPath, script.Path))
            )
                return ReasonCode.LegacyGuard;
        }
        if (operation == ScriptOperation.Start)
            return matches.Length > 0 ? ReasonCode.AlreadyRunning : ReasonCode.None;
        if (matches.Length != 1)
            return matches.Length > 1 ? ReasonCode.MultipleInstances : ReasonCode.IdentityChanged;
        if (matches[0].ControlUnavailableReason is ReasonCode access)
            return access;
        if (operation == ScriptOperation.ToggleSuspend && !matches[0].Suspended.HasValue)
            return ReasonCode.StateNotObservable;
        if (expected is null || !NativeProcessIdentity.Same(expected, matches[0].Identity))
            return ReasonCode.IdentityChanged;
        return ReasonCode.None;
    }

    static bool IsSupervisorRelated(string active, string target)
    {
        static bool Child(string child, string supervisor) =>
            Path.GetFileName(supervisor).Equals("startup.ahk", StringComparison.OrdinalIgnoreCase)
            && ScriptPaths.Equal(
                Path.GetDirectoryName(child),
                Path.Combine(Path.GetDirectoryName(supervisor)!, "scripts", "startup")
            );
        return Child(active, target) || Child(target, active);
    }

    public Task<OperationPlan> PrepareAsync(
        SelectionContext selection,
        ScriptOperation operation,
        CancellationToken cancellationToken = default
    ) =>
        Exclusive(
            () =>
            {
                var script = document.Scripts.SingleOrDefault(s => s.Id == selection.ScriptId);
                bool safe =
                    operation
                    is ScriptOperation.Edit
                        or ScriptOperation.OpenFolder
                        or ScriptOperation.Recover;
                var identity = snapshot
                    .Scripts.FirstOrDefault(s => s.Id == selection.ScriptId)
                    ?.Process?.Identity;
                var reason =
                    script is null || !ValidSelection(selection, safe)
                        ? ReasonCode.StaleSelection
                        : Policy(script, operation, identity);
                var plan = new OperationPlan(
                    Guid.NewGuid(),
                    selection.ScriptId,
                    operation,
                    snapshot.Revision,
                    selection.Query.Version,
                    identity,
                    reason == ReasonCode.None,
                    operation is ScriptOperation.Stop or ScriptOperation.Restart,
                    reason,
                    selection
                );
                foreach (
                    var key in plans
                        .Where(p => DateTimeOffset.UtcNow - p.Value.Time > TimeSpan.FromMinutes(2))
                        .Select(p => p.Key)
                        .ToArray()
                )
                    plans.Remove(key);
                if (plans.Count < 128)
                    plans[plan.CorrelationId] = (plan, DateTimeOffset.UtcNow);
                return Task.FromResult(plan);
            },
            cancellationToken
        );

    public Task<OperationResult> PerformAsync(
        OperationPlan plan,
        SelectionContext currentSelection,
        bool confirmed,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                bool safe =
                    plan.Operation
                    is ScriptOperation.Edit
                        or ScriptOperation.OpenFolder
                        or ScriptOperation.Recover;
                if (
                    !plans.Remove(plan.CorrelationId, out var prepared)
                    || prepared.Plan != plan
                    || DateTimeOffset.UtcNow - prepared.Time > TimeSpan.FromMinutes(2)
                    || !plan.Allowed
                    || plan.Selection != currentSelection
                    || plan.StateRevision != snapshot.Revision
                    || !ValidSelection(currentSelection, safe)
                )
                    return new(Outcome.Failed, ReasonCode.StaleSelection);
                if (plan.RequiresConfirmation && !confirmed)
                    return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
                var script = document.Scripts.SingleOrDefault(s => s.Id == plan.ScriptId);
                if (script is null)
                    return new(Outcome.Failed, ReasonCode.StaleSelection);
                if (plan.Operation is ScriptOperation.Edit or ScriptOperation.OpenFolder)
                    return await editor
                        .OpenAsync(
                            script.Path,
                            document.Settings,
                            plan.Operation == ScriptOperation.OpenFolder,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (plan.Operation == ScriptOperation.Recover)
                    return await RecoverCore(cancellationToken).ConfigureAwait(false);
                if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false))
                    return new(Outcome.Failed, ReasonCode.ConcurrentSettingsChange);
                await ObserveCore(cancellationToken).ConfigureAwait(false);
                var reason = Policy(script, plan.Operation, plan.ExpectedIdentity);
                if (reason != ReasonCode.None)
                {
                    await Publish(false, cancellationToken).ConfigureAwait(false);
                    return new(Outcome.Failed, reason);
                }
                return (
                    await ExecuteProcess(
                            script,
                            plan.Operation,
                            plan.ExpectedIdentity,
                            plan.CorrelationId,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                ).Result;
            },
            cancellationToken
        );

    async Task<(OperationResult Result, bool StopConfirmed)> ExecuteProcess(
        ScriptRecord script,
        ScriptOperation operation,
        ProcessIdentity? expected,
        Guid correlation,
        CancellationToken ct
    )
    {
        string interpreter = script.InterpreterPath ?? document.Settings.InterpreterPath ?? "";
        string scriptHash = "",
            interpreterHash = "";
        async Task<(OperationResult Result, bool StopConfirmed)> Refused(OperationResult result)
        {
            result = result with { CorrelationId = correlation };
            await Log(
                    script.Id,
                    operation,
                    result with
                    {
                        Detail =
                            $"{script.FriendlyName}: {result.Detail ?? LoginReason(result.Code)}",
                    },
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            return (result, false);
        }
        var continuationRefusal = await InvalidatePreparedStartupForStart(script.Id, operation)
            .ConfigureAwait(false);
        if (continuationRefusal != ReasonCode.None)
            return await Refused(new(Outcome.Failed, continuationRefusal)).ConfigureAwait(false);
        if (operation is ScriptOperation.Start or ScriptOperation.Restart)
        {
            try
            {
                var verified = AutoHotkeyDiscovery.Validate(interpreter);
                interpreter = verified.Path;
                interpreterHash = verified.Hash;
                scriptHash = ScriptPaths.Fingerprint(ScriptPaths.Canonical(script.Path));
                await AutoHotkeyDiscovery
                    .ValidateScriptVersionAsync(script.Path, verified, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await Refused(new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
                when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception)
            {
                return await Refused(
                        new(Outcome.Failed, ReasonCode.UnsupportedInterpreter, ex.Message)
                    )
                    .ConfigureAwait(false);
            }
        }
        var record = new OperationRecord(
            correlation,
            script.Id,
            operation,
            script.Path,
            interpreter,
            scriptHash,
            interpreterHash,
            owner.Sid,
            owner.LoginKey,
            DateTimeOffset.UtcNow,
            Outcome.Pending,
            expected
        );
        OperationResult reserved;
        try
        {
            reserved = await journal.PutAsync(record, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await Refused(new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled))
                .ConfigureAwait(false);
        }
        if (!reserved.Succeeded)
            return await Refused(reserved).ConfigureAwait(false);
        await Log(
                script.Id,
                operation,
                new(Outcome.Pending, CorrelationId: correlation),
                CancellationToken.None
            )
            .ConfigureAwait(false);
        OperationResult result;
        bool oldExited = false;
        bool stopConfirmed = false;
        bool dispatchAttempted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!owner.Held || !owner.Trusted || !owner.Allows(script.Path, interpreter))
                result = new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
            else
            {
                if (
                    operation
                    is ScriptOperation.Stop
                        or ScriptOperation.Restart
                        or ScriptOperation.TogglePause
                        or ScriptOperation.ToggleSuspend
                )
                {
                    dispatchAttempted = true;
                    result = await processes
                        .ControlAsync(
                            expected!,
                            operation == ScriptOperation.Restart ? ScriptOperation.Stop : operation,
                            ct
                        )
                        .ConfigureAwait(false);
                    stopConfirmed = operation == ScriptOperation.Stop && result.Succeeded;
                    oldExited = operation == ScriptOperation.Restart && result.Succeeded;
                }
                else
                    result = new(Outcome.Accepted);
                if (operation == ScriptOperation.Start || oldExited)
                {
                    if (ct.IsCancellationRequested)
                        result = new(
                            oldExited ? Outcome.Partial : Outcome.CancelledBeforeDispatch,
                            oldExited ? ReasonCode.RestartPartial : ReasonCode.Cancelled
                        );
                    else
                    {
                        if (oldExited)
                        {
                            // Persist the fact old target exited before the replacement can become ambiguous.
                            record = record with
                            {
                                Identity = null,
                                Code = ReasonCode.RestartPartial,
                                RestartOldExited = true,
                            };
                            var marked = await journal
                                .PutAsync(record, CancellationToken.None)
                                .ConfigureAwait(false);
                            if (!marked.Succeeded)
                            {
                                await Publish(false, CancellationToken.None).ConfigureAwait(false);
                                return (
                                    new(
                                        Outcome.Partial,
                                        ReasonCode.RestartPartial,
                                        marked.Detail,
                                        correlation
                                    ),
                                    stopConfirmed
                                );
                            }
                            await ObserveCore(ct).ConfigureAwait(false);
                            var permitted = PolicyForReplacement(script);
                            if (permitted != ReasonCode.None)
                            {
                                result = new(
                                    Outcome.Partial,
                                    ReasonCode.RestartPartial,
                                    permitted.ToString()
                                );
                                goto Finalize;
                            }
                        }
                        dispatchAttempted = true;
                        var launched = await processes
                            .StartAsync(interpreter, script.Path, script.RunAsAdministrator, ct)
                            .ConfigureAwait(false);
                        result = launched.Result;
                        record = record with { Identity = launched.Process?.Identity };
                        if (
                            launched.Process is not null
                            && (
                                launched.Process.Identity.ScriptHash != scriptHash
                                || launched.Process.Identity.InterpreterHash != interpreterHash
                            )
                        )
                            result = new(Outcome.Unknown, ReasonCode.LaunchUncertain);
                        if (
                            oldExited
                            && !result.Succeeded
                            && result.Outcome is not (Outcome.Unknown or Outcome.Pending)
                        )
                            result = result with
                            {
                                Outcome = Outcome.Partial,
                                Code = ReasonCode.RestartPartial,
                            };
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = new(
                dispatchAttempted ? Outcome.Unknown
                    : oldExited ? Outcome.Partial
                    : Outcome.CancelledBeforeDispatch,
                oldExited ? ReasonCode.RestartPartial
                    : dispatchAttempted ? ReasonCode.LaunchUncertain
                    : ReasonCode.Cancelled
            );
        }
        catch (Exception ex)
            when (SafeJsonFiles.Known(ex)
                || ex is InvalidOperationException or System.ComponentModel.Win32Exception
            )
        {
            result = new(
                Outcome.Unknown,
                oldExited ? ReasonCode.RestartPartial : ReasonCode.LaunchUncertain,
                ex.Message
            );
        }
        Finalize:
        result = result with { CorrelationId = correlation };
        var persisted = await journal
            .PutAsync(
                record with
                {
                    Outcome = result.Outcome,
                    Code = result.Code,
                },
                CancellationToken.None
            )
            .ConfigureAwait(false);
        if (!persisted.Succeeded)
            result = new(Outcome.Unknown, ReasonCode.StorageFailure, persisted.Detail, correlation);
        await Log(script.Id, operation, result, CancellationToken.None).ConfigureAwait(false);
        await ObserveCore(CancellationToken.None).ConfigureAwait(false);
        await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return (result, stopConfirmed);
    }

    ReasonCode PolicyForReplacement(ScriptRecord script)
    {
        if (!owner.Held || !owner.Trusted)
            return ReasonCode.EffectOwnerUnavailable;
        if (!observed.Complete)
            return ReasonCode.ObservationIncomplete;
        if (observed.Processes.Any(p => ScriptPaths.Equal(p.Identity.ScriptPath, script.Path)))
            return ReasonCode.AlreadyRunning;
        if (observed.Processes.Any(p => IsSupervisorRelated(p.Identity.ScriptPath, script.Path)))
            return ReasonCode.LegacyGuard;
        return ReasonCode.None;
    }
}
