using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    public Task<OperationResult> RunLoginAsync(CancellationToken cancellationToken = default) =>
        ResultAction(
            async () =>
            {
                async Task<OperationResult> Abort(
                    string stage,
                    OperationResult result,
                    ImmutableArray<Issue> issues = default
                )
                {
                    string detail =
                        $"Windows startup stopped while {stage}. {LoginReason(result.Code)}";
                    if (!issues.IsDefaultOrEmpty)
                        detail +=
                            "\n"
                            + string.Join(
                                "\n",
                                issues
                                    .Take(8)
                                    .Select(issue =>
                                        issue.Detail is { Length: > 0 } reason
                                            ? reason[..Math.Min(reason.Length, 240)]
                                            : LoginReason(issue.Code)
                                    )
                            );
                    var explained = result with { Detail = detail };
                    await Log(null, ScriptOperation.Start, explained, CancellationToken.None)
                        .ConfigureAwait(false);
                    await Publish(false, CancellationToken.None).ConfigureAwait(false);
                    return explained;
                }
                var registration = document.Settings.LaunchManagerAtLogin
                    ? startup.RegistrationStatus(
                        true,
                        document.Settings.LaunchManagerAsAdministrator
                    )
                    : ReasonCode.StartupInactive;
                if (registration != ReasonCode.None)
                    return await Abort(
                            "checking the startup configuration",
                            new(Outcome.Failed, registration)
                        )
                        .ConfigureAwait(false);
                if (!owner.Held || !owner.Trusted || owner.LoginKey.Length == 0)
                    return await Abort(
                            "obtaining permission to manage scripts",
                            new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable)
                        )
                        .ConfigureAwait(false);
                if (document.LastLoginKey == owner.LoginKey)
                    return new(Outcome.Confirmed, ReasonCode.StartupAlreadyProcessed);
                StartupInspection inspection;
                try
                {
                    inspection = await startup
                        .InspectAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await Abort(
                            "checking other startup configurations",
                            new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)
                        )
                        .ConfigureAwait(false);
                }
                if (!inspection.Complete)
                    return await Abort(
                            "checking other startup configurations",
                            new(Outcome.Failed, ReasonCode.ExternalStartupConflict),
                            inspection.Issues
                        )
                        .ConfigureAwait(false);
                OperationResult reserve;
                try
                {
                    reserve = await Commit(
                            document with
                            {
                                LastLoginKey = owner.LoginKey,
                            },
                            null,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await Abort(
                            "saving this session's startup record",
                            new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)
                        )
                        .ConfigureAwait(false);
                }
                if (!reserve.Succeeded)
                    return await Abort("saving this session's startup record", reserve)
                        .ConfigureAwait(false);
                int started = 0,
                    running = 0,
                    failed = 0;
                var details = new List<string>();
                var continuationCandidates = new List<ScriptRecord>();
                void Failure(ScriptRecord script, ReasonCode reason)
                {
                    failed++;
                    if (details.Count < 24)
                        details.Add($"{script.FriendlyName}: {LoginReason(reason)}");
                }
                foreach (var script in document.Scripts.Where(s => s.RunAtLogin == true).ToArray())
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Failure(script, ReasonCode.Cancelled);
                        await Log(
                                script.Id,
                                ScriptOperation.Start,
                                new(
                                    Outcome.CancelledBeforeDispatch,
                                    ReasonCode.Cancelled,
                                    $"{script.FriendlyName}: {LoginReason(ReasonCode.Cancelled)}"
                                ),
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                        continue;
                    }
                    await ObserveCore(CancellationToken.None).ConfigureAwait(false);
                    var reason =
                        cancellationToken.IsCancellationRequested ? ReasonCode.Cancelled
                        : script.RunAsAdministrator && !processes.IsAdministrator
                            ? ReasonCode.ManualElevationRequired
                        : inspection.ScriptPaths.Any(p => ScriptPaths.Equal(p, script.Path))
                            ? ReasonCode.ExternalStartupConflict
                        : Policy(script, ScriptOperation.Start, null);
                    if (reason == ReasonCode.AlreadyRunning)
                    {
                        running++;
                        continue;
                    }
                    if (reason != ReasonCode.None)
                    {
                        if (
                            reason == ReasonCode.ObservationIncomplete
                            && !script.RunAsAdministrator
                        )
                            continuationCandidates.Add(script);
                        Failure(script, reason);
                        await Log(
                                script.Id,
                                ScriptOperation.Start,
                                new(
                                    reason == ReasonCode.Cancelled
                                        ? Outcome.CancelledBeforeDispatch
                                        : Outcome.Failed,
                                    reason,
                                    $"{script.FriendlyName}: {LoginReason(reason)}"
                                ),
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                        continue;
                    }
                    var execution = await ExecuteProcess(
                            script,
                            ScriptOperation.Start,
                            null,
                            Guid.NewGuid(),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (execution.Result.Succeeded)
                        started++;
                    else
                        Failure(script, execution.Result.Code);
                }
                string detail =
                    $"Started: {started}. Already running: {running}. Not started or unconfirmed: {failed}.";
                if (details.Count > 0)
                    detail += "\n\n" + string.Join("\n", details);
                if (failed > details.Count)
                    detail += "\nSee Activity for the remaining results.";
                if (
                    continuationCandidates.Count > 0
                    && !processes.IsAdministrator
                    && !journal.Records.Any(OperationJournal.IsPending)
                )
                {
                    try
                    {
                        await PrepareStartupContinuationCore(continuationCandidates)
                            .ConfigureAwait(false);
                        detail +=
                            "\n\nYou can request Windows permission to inspect processes and continue only the standard-privilege scripts that have not been started.";
                    }
                    catch (Exception ex) when (SafeJsonFiles.Known(ex))
                    {
                        detail += "\nStartup could not be continued safely.";
                    }
                }
                var summary = new OperationResult(
                    failed > 0 ? Outcome.Partial : Outcome.Confirmed,
                    Detail: detail
                );
                await Log(null, ScriptOperation.Start, summary, CancellationToken.None)
                    .ConfigureAwait(false);
                await Publish(false, CancellationToken.None).ConfigureAwait(false);
                return summary;
            },
            cancellationToken
        );

    static string LoginReason(ReasonCode reason) =>
        reason switch
        {
            ReasonCode.ManualElevationRequired =>
                "Administrator autostart was skipped because Manager does not have administrator access. A manual launch can still request Windows permission.",
            ReasonCode.ObservationIncomplete =>
                "The process inventory is incomplete; starting another instance is unsafe.",
            ReasonCode.PendingOperation =>
                "Not started: the result of a previous request is still unconfirmed. Manager will not replay the launch automatically.",
            ReasonCode.LaunchUncertain =>
                "The launch outcome is unknown. A new launch is blocked to prevent duplicate instances.",
            ReasonCode.AccessDenied => "Windows denied access to the file or process.",
            ReasonCode.MissingFile => "The file was not found.",
            ReasonCode.UnsupportedInterpreter =>
                "The selected interpreter or the version required by the script is incompatible.",
            ReasonCode.ExternalStartupConflict =>
                "Another automatic startup configuration may launch a duplicate of this script.",
            ReasonCode.LegacyGuard =>
                "A catalog safeguard or launch block prevents this script from starting.",
            ReasonCode.Cancelled => "The launch was cancelled.",
            ReasonCode.DispatchFailed => "The launch request could not be dispatched.",
            ReasonCode.EffectOwnerUnavailable =>
                "The authority required to start scripts is unavailable.",
            ReasonCode.StartupInactive =>
                "Windows startup is disabled or unregistered. Review this option in Preferences.",
            ReasonCode.StartupEntryConflict =>
                "The Windows startup entry belongs to another configuration. Review it in Preferences.",
            ReasonCode.ConcurrentSettingsChange =>
                "Another instance changed the settings. Refresh the state before trying again.",
            ReasonCode.ReadOnlySettings or ReasonCode.StorageFailure =>
                "The data must be recovered before scripts can start.",
            _ => $"Startup is unavailable ({reason}). See Activity.",
        };
}
