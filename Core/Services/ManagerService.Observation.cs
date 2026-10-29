using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    async void ProcessInvalidated(object? sender, EventArgs e)
    {
        if (disposed || Interlocked.Exchange(ref refreshQueued, 1) != 0)
            return;
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
            when (SafeJsonFiles.Known(ex)
                || ex is ObjectDisposedException or InvalidOperationException
            ) { }
        finally
        {
            Volatile.Write(ref refreshQueued, 0);
        }
    }

    async Task ObserveCore(CancellationToken ct)
    {
        var known = document
            .Scripts.Select(s => s.InterpreterPath)
            .Append(document.Settings.InterpreterPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var previous = observed;
        observed = await processes.ObserveAsync(known, ct).ConfigureAwait(false);
        observedAt = DateTimeOffset.UtcNow;
        if (observed.Complete)
            foreach (
                var ended in previous.Processes.Where(old =>
                    !observed.Processes.Any(now =>
                        now.Identity.Pid == old.Identity.Pid
                        && now.Identity.CreationFileTime == old.Identity.CreationFileTime
                    )
                )
            )
            {
                var script = document.Scripts.FirstOrDefault(s =>
                    ScriptPaths.Equal(s.Path, ended.Identity.ScriptPath)
                );
                if (script is not null)
                    await Log(
                            script.Id,
                            ScriptOperation.Stop,
                            new(Outcome.Confirmed),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
            }
    }

    async Task Publish(bool reanalyze, CancellationToken ct)
    {
        var scripts = ImmutableArray.CreateBuilder<ScriptSnapshot>();
        foreach (var entry in document.Scripts)
        {
            var issues = ImmutableArray.CreateBuilder<Issue>();
            bool exists = File.Exists(entry.Path);
            if (reanalyze || !analyses.ContainsKey(entry.Id))
                analyses[entry.Id] = exists
                    ? await analyzer.AnalyzeAsync(entry.Path, ct).ConfigureAwait(false)
                    : new([], AnalysisCoverage.Unanalysable, [new(ReasonCode.MissingFile)]);
            var analysis = analyses[entry.Id];
            issues.AddRange(analysis.Issues.Select(i => i with { ScriptId = entry.Id }));
            var matching = observed
                .Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, entry.Path))
                .ToArray();
            bool pending = journal.Records.Any(r =>
                OperationJournal.IsPending(r)
                && (
                    r.ScriptId == entry.Id
                        && r.Operation
                            is ScriptOperation.Start
                                or ScriptOperation.Restart
                                or ScriptOperation.Stop
                    || r.Operation == ScriptOperation.RecycleFile
                        && (r.ScriptId == entry.Id || ScriptPaths.Equal(r.ScriptPath, entry.Path))
                )
            );
            ScriptStatus status =
                matching.Length > 1 ? ScriptStatus.Multiple
                : pending ? ScriptStatus.Unknown
                : matching.Length == 1 ? ScriptStatus.Running
                : !exists ? ScriptStatus.Missing
                : !observed.Complete ? ScriptStatus.Unknown
                : entry.LegacyGuard || entry.LaunchBlocked ? ScriptStatus.Blocked
                : ScriptStatus.Stopped;
            if (matching.Length > 1)
                issues.Add(new(ReasonCode.MultipleInstances, null, entry.Id));
            if (!observed.Complete && matching.Length == 0)
                issues.Add(new(ReasonCode.ObservationIncomplete, null, entry.Id));
            if (pending)
                issues.Add(new(ReasonCode.PendingOperation, null, entry.Id));
            if (!exists)
                issues.Add(new(ReasonCode.MissingFile, null, entry.Id));
            if (entry.LegacyGuard)
                issues.Add(new(ReasonCode.LegacyGuard, null, entry.Id));
            if (entry.RunAtLogin == true && !document.Settings.LaunchManagerAtLogin)
                issues.Add(new(ReasonCode.StartupInactive, null, entry.Id));
            var keys = analysis.Hotkeys.AddRange(
                entry.ManualHotkeys.Select(chord => new HotkeySnapshot(
                    chord,
                    HotkeyAnalyzer.Normalize(chord),
                    HotkeyOrigin.Manual
                ))
            );
            scripts.Add(
                new(
                    entry.Id,
                    entry.Path,
                    entry.FriendlyName,
                    entry.Description,
                    entry.CollectionId,
                    entry.RunAtLogin,
                    entry.RunAsAdministrator,
                    entry.LaunchBlocked,
                    status,
                    matching.Length == 1 ? matching[0] : null,
                    keys,
                    analysis.Coverage,
                    issues.ToImmutable(),
                    analysis.HeaderDescription
                )
            );
        }
        var global = storageIssues.AddRange(observed.Issues);
        if (!owner.Held || !owner.Trusted)
            global = global.Add(new(ReasonCode.EffectOwnerUnavailable));
        if (!journal.Ready)
            global = global.Add(
                new(
                    ReasonCode.PendingOperation,
                    "The launch history could not be read. Launch actions are unavailable."
                )
            );
        else
            foreach (var request in journal.Records.Where(OperationJournal.IsPending).Take(8))
                global = global.Add(
                    new(ReasonCode.PendingOperation, PendingRequestDetail(request))
                );
        if (startupRecoveryPending)
            global = global.Add(
                new(
                    ReasonCode.PendingOperation,
                    "The startup continuation could not be verified. New launches are blocked to prevent duplicate instances."
                )
            );
        var registration = startup.RegistrationStatus(
            document.Settings.LaunchManagerAtLogin,
            document.Settings.LaunchManagerAsAdministrator
        );
        if (registration != ReasonCode.None)
            global = global.Add(new(registration));
        ImmutableArray<ActivitySnapshot> recent;
        try
        {
            recent = await activity.ReadAsync(new(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex))
        {
            recent = [];
            global = global.Add(new(ReasonCode.ActivityUnavailable, ex.Message));
        }
        var next = new ManagerSnapshot(
            snapshot.Revision + 1,
            HotkeyAnalyzer.MarkConflicts(scripts.ToImmutable()),
            document.Collections.ToImmutableArray(),
            document.Settings,
            recent,
            global.Distinct().ToImmutableArray(),
            store.ReadOnly,
            owner.Held
                && owner.Trusted
                && journal.Ready
                && !store.ReadOnly
                && (!startupRecoveryPending || startupResumeActive),
            observedAt,
            processes.GetNormalLaunchUnavailableReason()
        );
        Volatile.Write(ref snapshot, next);
        Changed?.Invoke(this, next);
    }

    string PendingRequestDetail(OperationRecord record)
    {
        string name =
            document.Scripts.FirstOrDefault(s => s.Id == record.ScriptId)?.FriendlyName
            ?? Path.GetFileNameWithoutExtension(record.ScriptPath);
        string action = record.Operation switch
        {
            ScriptOperation.Start => "starting",
            ScriptOperation.Stop => "stopping",
            ScriptOperation.Restart => "restarting",
            ScriptOperation.TogglePause => "changing the pause state of",
            ScriptOperation.ToggleSuspend => "changing the hotkey suspension state of",
            ScriptOperation.RecycleFile => "recycling",
            _ => "changing",
        };
        return $"The outcome of {action} {name} could not be confirmed. Manager will not replay this request automatically.";
    }
}
