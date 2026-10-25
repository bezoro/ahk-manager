using System.Collections.Immutable;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed class ManagerService : IManagerService
{
    readonly SemaphoreSlim lane = new(1, 1);
    readonly SettingsStore store;
    readonly OperationJournal journal;
    readonly ActivityStore activity;
    readonly LegacyImportService importer;
    readonly IEffectOwner owner;
    readonly IProcessManager processes;
    readonly UserStartupService startup;
    readonly HotkeyAnalyzer analyzer = new();
    readonly ExternalEditorService editor = new();
    readonly Dictionary<Guid, HotkeyAnalysis> analyses = [];
    readonly Dictionary<Guid, (OperationPlan Plan, DateTimeOffset Time)> plans = [];
    SettingsDocument document = new();
    ObservedProcesses observed = new([], false, []);
    ImmutableArray<Issue> storageIssues = [];
    ManagerSnapshot snapshot = ManagerSnapshot.Empty;
    DateTimeOffset? observedAt;
    bool loaded, disposed;
    int refreshQueued;
    public ManagerSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event EventHandler<ManagerSnapshot>? Changed;
    public string DataRoot { get; }

    public static ManagerService CreateDefault(string? dataRoot = null)
    {
        var owner = new UserEffectOwner();
        return new(dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManagerWinUI"), owner,
            new ProcessManager(owner, documentedControlsProven: true), new UserStartupService(new RegistryStartupAdapter(), Environment.ProcessPath ?? throw new InvalidOperationException("Manager executable unavailable.")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManager"));
    }
    // Trusted host composition only; no renderer or script input reaches this seam.
    public static ManagerService CreateForTrustedStartupHost(string dataRoot, string startupHostExecutable, out IEffectOwner effectOwner)
    {
        string executable = ScriptPaths.Canonical(startupHostExecutable, false);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(executable).Equals("AhkManager.Backend.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A validated manager host executable is required.");
        var owner = new UserEffectOwner();
        effectOwner = owner;
        return new(dataRoot, owner, new ProcessManager(owner, documentedControlsProven: true),
            new UserStartupService(new RegistryStartupAdapter(), executable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManager"));
    }
    public ManagerService(string dataRoot, IEffectOwner owner, IProcessManager processes, UserStartupService startup, string? legacyAuthorityDirectory = null)
    {
        DataRoot = ScriptPaths.LocalPath(dataRoot); this.owner = owner; this.processes = processes; this.startup = startup;
        store = new(Path.Combine(DataRoot, "settings.json")); journal = new(Path.Combine(DataRoot, "operations.json"));
        activity = new(DataRoot); importer = new(DataRoot, owner, legacyAuthorityDirectory);
        processes.Invalidated += ProcessInvalidated;
    }
    async Task<T> Exclusive<T>(Func<Task<T>> action, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            await lane.WaitAsync(ct).ConfigureAwait(false);
            try { ObjectDisposedException.ThrowIf(disposed, this); return await action().ConfigureAwait(false); }
            finally { lane.Release(); }
        }, ct).ConfigureAwait(false);
    }
    async Task<OperationResult> ResultAction(Func<Task<OperationResult>> action, CancellationToken ct)
    {
        try { return await Exclusive(action, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message); }
    }
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await Exclusive(async () =>
        {
            if (loaded) return true;
            var result = await store.LoadAsync(cancellationToken).ConfigureAwait(false); document = result.Document; storageIssues = result.Issues;
            try { await journal.LoadAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException) { storageIssues = storageIssues.Add(new(ReasonCode.PendingOperation, ex.Message)); }
            if (document.Settings.InterpreterPath is null)
            {
                var candidates = await new AutoHotkeyDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
                if (candidates.Length > 0) document = document with { Settings = document.Settings with { InterpreterPath = candidates[0].Path } };
            }
            loaded = true;
            await ObserveCore(cancellationToken).ConfigureAwait(false); await Publish(true, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!loaded) { await LoadAsync(cancellationToken).ConfigureAwait(false); return; }
        await Exclusive(async () => { await ObserveCore(cancellationToken).ConfigureAwait(false); await Publish(true, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
    }
    async void ProcessInvalidated(object? sender, EventArgs e)
    {
        if (disposed || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        try { await RefreshAsync().ConfigureAwait(false); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is ObjectDisposedException or InvalidOperationException) { }
        finally { Volatile.Write(ref refreshQueued, 0); }
    }
    async Task ObserveCore(CancellationToken ct)
    {
        var known = document.Scripts.Select(s => s.InterpreterPath).Append(document.Settings.InterpreterPath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var previous = observed;
        observed = await processes.ObserveAsync(known, ct).ConfigureAwait(false); observedAt = DateTimeOffset.UtcNow;
        if (observed.Complete)
            foreach (var ended in previous.Processes.Where(old => !observed.Processes.Any(now => now.Identity.Pid == old.Identity.Pid && now.Identity.CreationFileTime == old.Identity.CreationFileTime)))
            {
                var script = document.Scripts.FirstOrDefault(s => ScriptPaths.Equal(s.Path, ended.Identity.ScriptPath));
                if (script is not null) await Log(script.Id, ScriptOperation.Stop, new(Outcome.Confirmed), CancellationToken.None).ConfigureAwait(false);
            }
    }
    async Task Publish(bool reanalyze, CancellationToken ct)
    {
        var scripts = ImmutableArray.CreateBuilder<ScriptSnapshot>();
        foreach (var entry in document.Scripts)
        {
            var issues = ImmutableArray.CreateBuilder<Issue>();
            bool exists = File.Exists(entry.Path);
            if (reanalyze || !analyses.ContainsKey(entry.Id)) analyses[entry.Id] = exists ? await analyzer.AnalyzeAsync(entry.Path, ct).ConfigureAwait(false) : new([], AnalysisCoverage.Unanalysable, [new(ReasonCode.MissingFile)]);
            var analysis = analyses[entry.Id]; issues.AddRange(analysis.Issues.Select(i => i with { ScriptId = entry.Id }));
            var matching = observed.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, entry.Path)).ToArray();
            bool pending = journal.Records.Any(r => r.ScriptId == entry.Id && OperationJournal.IsPending(r) && r.Operation is ScriptOperation.Start or ScriptOperation.Restart or ScriptOperation.Stop);
            ScriptStatus status = matching.Length > 1 ? ScriptStatus.Multiple : pending || !observed.Complete && matching.Length == 0 ? ScriptStatus.Unknown : matching.Length == 1 ? ScriptStatus.Running : !exists ? ScriptStatus.Missing : entry.LegacyGuard || entry.LaunchBlocked ? ScriptStatus.Blocked : ScriptStatus.Stopped;
            if (matching.Length > 1) issues.Add(new(ReasonCode.MultipleInstances, null, entry.Id));
            if (!observed.Complete && matching.Length == 0) issues.Add(new(ReasonCode.ObservationIncomplete, null, entry.Id));
            if (pending) issues.Add(new(ReasonCode.PendingOperation, null, entry.Id));
            if (!exists) issues.Add(new(ReasonCode.MissingFile, null, entry.Id));
            if (entry.LegacyGuard) issues.Add(new(ReasonCode.LegacyGuard, null, entry.Id));
            if (entry.RunAtLogin == true && !document.Settings.LaunchManagerAtLogin) issues.Add(new(ReasonCode.StartupInactive, null, entry.Id));
            var keys = analysis.Hotkeys.AddRange(entry.ManualHotkeys.Select(chord => new HotkeySnapshot(chord, HotkeyAnalyzer.Normalize(chord), HotkeyOrigin.Manual)));
            scripts.Add(new(entry.Id, entry.Path, entry.FriendlyName, entry.Description, entry.CollectionId, entry.RunAtLogin, entry.RunAsAdministrator, entry.LaunchBlocked, status, matching.Length == 1 ? matching[0] : null, keys, analysis.Coverage, issues.ToImmutable(), analysis.HeaderDescription));
        }
        var global = storageIssues.AddRange(observed.Issues);
        if (!owner.Held || !owner.Trusted) global = global.Add(new(ReasonCode.EffectOwnerUnavailable));
        if (!journal.Ready) global = global.Add(new(ReasonCode.PendingOperation));
        var registration = startup.RegistrationStatus(document.Settings.LaunchManagerAtLogin);
        if (registration != ReasonCode.None) global = global.Add(new(registration));
        ImmutableArray<ActivitySnapshot> recent;
        try { recent = await activity.ReadAsync(new(), ct).ConfigureAwait(false); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { recent = []; global = global.Add(new(ReasonCode.ActivityUnavailable, ex.Message)); }
        var next = new ManagerSnapshot(snapshot.Revision + 1, HotkeyAnalyzer.MarkConflicts(scripts.ToImmutable()), document.Collections.ToImmutableArray(), document.Settings, recent, global.Distinct().ToImmutableArray(), store.ReadOnly, owner.Held && owner.Trusted && journal.Ready && !store.ReadOnly, observedAt);
        Volatile.Write(ref snapshot, next); Changed?.Invoke(this, next);
    }
    async Task<OperationResult> Commit(SettingsDocument next, Guid? scriptId, CancellationToken ct)
    {
        var result = await store.SaveAsync(next, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            document = next;
            await Log(scriptId, null, result, CancellationToken.None).ConfigureAwait(false);
            await Publish(true, CancellationToken.None).ConfigureAwait(false);
        }
        return result;
    }
    async Task Log(Guid? script, ScriptOperation? operation, OperationResult result, CancellationToken ct)
    {
        var logged = await activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, script, operation, result.Outcome, result.Code, result.Detail), ct).ConfigureAwait(false);
        if (!logged.Succeeded && !storageIssues.Any(i => i.Code == ReasonCode.ActivityUnavailable)) storageIssues = storageIssues.Add(new(ReasonCode.ActivityUnavailable, logged.Detail));
    }

    public Task<CatalogResult> AddFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Add(paths, [], cancellationToken);
    async Task<CatalogResult> Add(IReadOnlyList<string> paths, ImmutableArray<Issue> initialIssues, CancellationToken ct)
    {
        try
        {
            return await Exclusive(async () =>
            {
                var issues = initialIssues.ToBuilder(); var added = ImmutableArray.CreateBuilder<Guid>(); int duplicates = 0;
                var scripts = document.Scripts.ToList(); var seen = scripts.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths.Take(2001))
                {
                    ct.ThrowIfCancellationRequested();
                    if (scripts.Count >= 2000) { issues.Add(new(ReasonCode.FolderLimitReached)); break; }
                    try
                    {
                        string canonical = ScriptPaths.Canonical(path);
                        if (!seen.Add(canonical)) { duplicates++; continue; }
                        var record = new ScriptRecord { Path = canonical, FriendlyName = Path.GetFileNameWithoutExtension(canonical) };
                        scripts.Add(record); added.Add(record.Id);
                    }
                    catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { issues.Add(new(ReasonCode.InvalidPath, path)); }
                }
                if (paths.Count > 2000) issues.Add(new(ReasonCode.FolderLimitReached));
                if (added.Count == 0) return new CatalogResult(new(issues.Count > 0 ? Outcome.Partial : Outcome.Confirmed), [], duplicates, issues.ToImmutable());
                var saved = await Commit(document with { Scripts = scripts }, null, ct).ConfigureAwait(false);
                return new CatalogResult(saved.Succeeded && issues.Count > 0 ? saved with { Outcome = Outcome.Partial } : saved, saved.Succeeded ? added.ToImmutable() : [], duplicates, issues.ToImmutable());
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled), [], 0, initialIssues); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { return new(new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message), [], 0, initialIssues); }
    }
    public async Task<CatalogResult> AddFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var scan = await Task.Run(() =>
        {
            var files = new List<string>(); var issues = ImmutableArray.CreateBuilder<Issue>();
            try
            {
                path = ScriptPaths.LocalPath(path); SafeJsonFiles.SafePath(path);
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((path, 0)); int entries = 0;
                while (pending.Count > 0 && files.Count < 2000 && entries < 20000)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var current = pending.Pop();
                    try
                    {
                        foreach (var item in Directory.EnumerateFileSystemEntries(current.Path))
                        {
                            cancellationToken.ThrowIfCancellationRequested(); if (++entries > 20000 || files.Count >= 2000) break;
                            var attributes = File.GetAttributes(item);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { issues.Add(new(ReasonCode.InvalidPath, item)); continue; }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (current.Depth < 12) pending.Push((item, current.Depth + 1)); else issues.Add(new(ReasonCode.FolderLimitReached, item));
                            }
                            else if (item.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) files.Add(item);
                        }
                    }
                    catch (Exception ex) when (SafeJsonFiles.Known(ex)) { issues.Add(new(ReasonCode.AccessDenied, current.Path)); }
                }
                if (pending.Count > 0 || entries >= 20000 || files.Count >= 2000) issues.Add(new(ReasonCode.FolderLimitReached));
            }
            catch (Exception ex) when (SafeJsonFiles.Known(ex)) { issues.Add(new(ReasonCode.InvalidPath, path)); }
            return (Files: files, Issues: issues.ToImmutable());
        }, cancellationToken).ConfigureAwait(false);
        return await Add(scan.Files, scan.Issues, cancellationToken).ConfigureAwait(false);
    }
    public Task<OperationResult> RemoveAsync(Guid scriptId, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        if (!document.Scripts.Any(s => s.Id == scriptId)) return new(Outcome.Failed, ReasonCode.InvalidMetadata);
        // Only catalog intent is removed; running processes and source files remain untouched.
        return await Commit(document with { Scripts = document.Scripts.Where(s => s.Id != scriptId).ToList() }, scriptId, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
    public Task<OperationResult> SaveMetadataAsync(Guid scriptId, ScriptMetadata metadata, bool startupConsent = false, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        var old = document.Scripts.SingleOrDefault(s => s.Id == scriptId);
        if (old is null) return new(Outcome.Failed, ReasonCode.InvalidMetadata);
        if (startupConsent && metadata.RunAtLogin == true && (!owner.Held || !owner.Trusted)) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (metadata.RunAtLogin == true && old.RunAtLogin != true && !document.Settings.LaunchManagerAtLogin)
        {
            if (!startupConsent) return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
            if (!owner.Held || !owner.Trusted) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        }
        var updated = old with { FriendlyName = metadata.FriendlyName.Trim(), Description = metadata.Description, CollectionId = metadata.CollectionId, RunAtLogin = metadata.RunAtLogin, RunAsAdministrator = metadata.RunAsAdministrator, LaunchBlocked = metadata.LaunchBlocked, ManualHotkeys = metadata.ManualHotkeys };
        var next = document with { Scripts = document.Scripts.Select(s => s.Id == scriptId ? updated : s).ToList(), Settings = metadata.RunAtLogin == true && startupConsent ? document.Settings with { LaunchManagerAtLogin = true } : document.Settings };
        var saved = await Commit(next, scriptId, cancellationToken).ConfigureAwait(false);
        if (saved.Succeeded && startupConsent && metadata.RunAtLogin == true)
        {
            var registered = await startup.SetEnabledAsync(true, true, CancellationToken.None).ConfigureAwait(false);
            await Publish(false, CancellationToken.None).ConfigureAwait(false);
            if (!registered.Succeeded) return registered with { Outcome = Outcome.Partial };
        }
        return saved;
    }, cancellationToken);
    public Task<OperationResult> SaveCollectionAsync(Guid? collectionId, string name, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        name = name.Trim();
        if (name.Length is 0 or > 120 || document.Collections.Any(c => c.Id != collectionId && c.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) || collectionId.HasValue && !document.Collections.Any(c => c.Id == collectionId)) return new(Outcome.Failed, ReasonCode.InvalidMetadata);
        var collections = document.Collections.ToList();
        if (collectionId.HasValue) collections = collections.Select(c => c.Id == collectionId ? c with { Name = name } : c).ToList();
        else collections.Add(new(Guid.NewGuid(), name));
        return await Commit(document with { Collections = collections }, null, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
    public Task<OperationResult> RemoveCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default) => ResultAction(() => Commit(document with { Collections = document.Collections.Where(c => c.Id != collectionId).ToList(), Scripts = document.Scripts.Select(s => s.CollectionId == collectionId ? s with { CollectionId = null } : s).ToList() }, null, cancellationToken), cancellationToken);

    bool ValidSelection(SelectionContext selection, bool safe)
    {
        if (selection.StateRevision != snapshot.Revision) return false;
        var projection = ScriptViews.Project(snapshot, selection.Query, selection.ScriptId);
        return safe ? projection.VisibleIds.Contains(selection.ScriptId) : projection.EffectiveSelectedId == selection.ScriptId;
    }
    ReasonCode Policy(ScriptRecord script, ScriptOperation operation, ProcessIdentity? expected)
    {
        if (operation is ScriptOperation.Edit or ScriptOperation.OpenFolder) return File.Exists(script.Path) ? ReasonCode.None : ReasonCode.MissingFile;
        if (operation == ScriptOperation.Recover) return ReasonCode.None;
        if (!owner.Held || !owner.Trusted) return ReasonCode.EffectOwnerUnavailable;
        if (store.ReadOnly) return ReasonCode.ReadOnlySettings;
        if (!journal.Ready || journal.Records.Any(r => OperationJournal.IsPending(r) && (r.Operation is ScriptOperation.Start or ScriptOperation.Restart || r.ScriptId == script.Id && (r.Operation == operation || r.Operation == ScriptOperation.Stop)))) return ReasonCode.PendingOperation;
        if (script.LegacyGuard) return ReasonCode.LegacyGuard;
        if (!processes.Supports(operation)) return ReasonCode.CapabilityUnverified;
        var matches = observed.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, script.Path)).ToArray();
        if (operation is ScriptOperation.Start or ScriptOperation.Restart)
        {
            if (script.LaunchBlocked) return ReasonCode.LegacyGuard;
            if (!File.Exists(script.Path)) return ReasonCode.MissingFile;
            if (!observed.Complete) return ReasonCode.ObservationIncomplete;
            if (string.IsNullOrWhiteSpace(script.InterpreterPath ?? document.Settings.InterpreterPath)) return ReasonCode.UnsupportedInterpreter;
            if (script.RunAsAdministrator && operation == ScriptOperation.Restart) return ReasonCode.ManualElevationRequired;
            if (observed.Processes.Any(p => IsSupervisorRelated(p.Identity.ScriptPath, script.Path))) return ReasonCode.LegacyGuard;
        }
        if (operation == ScriptOperation.Start) return matches.Length > 0 ? ReasonCode.AlreadyRunning : ReasonCode.None;
        if (matches.Length != 1) return matches.Length > 1 ? ReasonCode.MultipleInstances : ReasonCode.IdentityChanged;
        if (operation == ScriptOperation.ToggleSuspend && !matches[0].Suspended.HasValue) return ReasonCode.StateNotObservable;
        if (expected is null || !NativeProcessIdentity.Same(expected, matches[0].Identity)) return ReasonCode.IdentityChanged;
        return ReasonCode.None;
    }
    static bool IsSupervisorRelated(string active, string target)
    {
        static bool Child(string child, string supervisor) => Path.GetFileName(supervisor).Equals("startup.ahk", StringComparison.OrdinalIgnoreCase) && ScriptPaths.Equal(Path.GetDirectoryName(child), Path.Combine(Path.GetDirectoryName(supervisor)!, "scripts", "startup"));
        return Child(active, target) || Child(target, active);
    }
    public Task<OperationPlan> PrepareAsync(SelectionContext selection, ScriptOperation operation, CancellationToken cancellationToken = default) => Exclusive(() =>
    {
        var script = document.Scripts.SingleOrDefault(s => s.Id == selection.ScriptId);
        bool safe = operation is ScriptOperation.Edit or ScriptOperation.OpenFolder or ScriptOperation.Recover;
        var identity = snapshot.Scripts.FirstOrDefault(s => s.Id == selection.ScriptId)?.Process?.Identity;
        var reason = script is null || !ValidSelection(selection, safe) ? ReasonCode.StaleSelection : Policy(script, operation, identity);
        var plan = new OperationPlan(Guid.NewGuid(), selection.ScriptId, operation, snapshot.Revision, selection.Query.Version, identity, reason == ReasonCode.None, operation is ScriptOperation.Stop or ScriptOperation.Restart || script?.RunAsAdministrator == true && operation == ScriptOperation.Start, reason, selection);
        foreach (var key in plans.Where(p => DateTimeOffset.UtcNow - p.Value.Time > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray()) plans.Remove(key);
        if (plans.Count < 128) plans[plan.CorrelationId] = (plan, DateTimeOffset.UtcNow);
        return Task.FromResult(plan);
    }, cancellationToken);

    public Task<OperationResult> PerformAsync(OperationPlan plan, SelectionContext currentSelection, bool confirmed, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        bool safe = plan.Operation is ScriptOperation.Edit or ScriptOperation.OpenFolder or ScriptOperation.Recover;
        if (!plans.Remove(plan.CorrelationId, out var prepared) || prepared.Plan != plan || DateTimeOffset.UtcNow - prepared.Time > TimeSpan.FromMinutes(2) || !plan.Allowed || plan.Selection != currentSelection || plan.StateRevision != snapshot.Revision || !ValidSelection(currentSelection, safe)) return new(Outcome.Failed, ReasonCode.StaleSelection);
        if (plan.RequiresConfirmation && !confirmed) return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
        var script = document.Scripts.SingleOrDefault(s => s.Id == plan.ScriptId);
        if (script is null) return new(Outcome.Failed, ReasonCode.StaleSelection);
        if (plan.Operation is ScriptOperation.Edit or ScriptOperation.OpenFolder) return await editor.OpenAsync(script.Path, document.Settings, plan.Operation == ScriptOperation.OpenFolder, cancellationToken).ConfigureAwait(false);
        if (plan.Operation == ScriptOperation.Recover) return await RecoverCore(cancellationToken).ConfigureAwait(false);
        if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false)) return new(Outcome.Failed, ReasonCode.ConcurrentSettingsChange);
        await ObserveCore(cancellationToken).ConfigureAwait(false);
        var reason = Policy(script, plan.Operation, plan.ExpectedIdentity);
        if (reason != ReasonCode.None) { await Publish(false, cancellationToken).ConfigureAwait(false); return new(Outcome.Failed, reason); }
        return await ExecuteProcess(script, plan.Operation, plan.ExpectedIdentity, plan.CorrelationId, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
    async Task<OperationResult> ExecuteProcess(ScriptRecord script, ScriptOperation operation, ProcessIdentity? expected, Guid correlation, CancellationToken ct)
    {
        string interpreter = script.InterpreterPath ?? document.Settings.InterpreterPath ?? "";
        string scriptHash = "", interpreterHash = "";
        if (operation is ScriptOperation.Start or ScriptOperation.Restart)
        {
            try { var verified = AutoHotkeyDiscovery.Validate(interpreter); interpreter = verified.Path; interpreterHash = verified.Hash; scriptHash = ScriptPaths.Fingerprint(ScriptPaths.Canonical(script.Path)); await AutoHotkeyDiscovery.ValidateScriptVersionAsync(script.Path, verified, ct).ConfigureAwait(false); }
            catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { return new(Outcome.Failed, ReasonCode.UnsupportedInterpreter, ex.Message); }
        }
        var record = new OperationRecord(correlation, script.Id, operation, script.Path, interpreter, scriptHash, interpreterHash, owner.Sid, owner.LoginKey, DateTimeOffset.UtcNow, Outcome.Pending, expected);
        var reserved = await journal.PutAsync(record, ct).ConfigureAwait(false);
        if (!reserved.Succeeded) return reserved;
        await Log(script.Id, operation, new(Outcome.Pending, CorrelationId: correlation), CancellationToken.None).ConfigureAwait(false);
        OperationResult result;
        bool oldExited = false;
        bool dispatchAttempted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!owner.Held || !owner.Trusted || !owner.Allows(script.Path, interpreter)) result = new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
            else
            {
                if (operation is ScriptOperation.Stop or ScriptOperation.Restart or ScriptOperation.TogglePause or ScriptOperation.ToggleSuspend)
                {
                    dispatchAttempted = true;
                    result = await processes.ControlAsync(expected!, operation == ScriptOperation.Restart ? ScriptOperation.Stop : operation, ct).ConfigureAwait(false);
                    oldExited = operation == ScriptOperation.Restart && result.Succeeded;
                }
                else result = new(Outcome.Accepted);
                if (operation == ScriptOperation.Start || oldExited)
                {
                    if (ct.IsCancellationRequested) result = new(oldExited ? Outcome.Partial : Outcome.CancelledBeforeDispatch, oldExited ? ReasonCode.RestartPartial : ReasonCode.Cancelled);
                    else
                    {
                        if (oldExited)
                        {
                            // Persist the fact old target exited before the replacement can become ambiguous.
                            record = record with { Identity = null, Code = ReasonCode.RestartPartial, RestartOldExited = true };
                            var marked = await journal.PutAsync(record, CancellationToken.None).ConfigureAwait(false);
                            if (!marked.Succeeded) { await Publish(false, CancellationToken.None).ConfigureAwait(false); return new(Outcome.Partial, ReasonCode.RestartPartial, marked.Detail, correlation); }
                            await ObserveCore(ct).ConfigureAwait(false);
                            var permitted = PolicyForReplacement(script);
                            if (permitted != ReasonCode.None) { result = new(Outcome.Partial, ReasonCode.RestartPartial, permitted.ToString()); goto Finalize; }
                        }
                        dispatchAttempted = true;
                        var launched = await processes.StartAsync(interpreter, script.Path, script.RunAsAdministrator, ct).ConfigureAwait(false);
                        result = launched.Result;
                        record = record with { Identity = launched.Process?.Identity };
                        if (launched.Process is not null && (launched.Process.Identity.ScriptHash != scriptHash || launched.Process.Identity.InterpreterHash != interpreterHash)) result = new(Outcome.Unknown, ReasonCode.LaunchUncertain);
                        if (oldExited && !result.Succeeded && result.Outcome is not (Outcome.Unknown or Outcome.Pending)) result = result with { Outcome = Outcome.Partial, Code = ReasonCode.RestartPartial };
                    }
                }
            }
        }
        catch (OperationCanceledException) { result = new(dispatchAttempted ? Outcome.Unknown : oldExited ? Outcome.Partial : Outcome.CancelledBeforeDispatch, oldExited ? ReasonCode.RestartPartial : dispatchAttempted ? ReasonCode.LaunchUncertain : ReasonCode.Cancelled); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException or System.ComponentModel.Win32Exception) { result = new(Outcome.Unknown, oldExited ? ReasonCode.RestartPartial : ReasonCode.LaunchUncertain, ex.Message); }
        Finalize:
        result = result with { CorrelationId = correlation };
        var persisted = await journal.PutAsync(record with { Outcome = result.Outcome, Code = result.Code }, CancellationToken.None).ConfigureAwait(false);
        if (!persisted.Succeeded) result = new(Outcome.Unknown, ReasonCode.StorageFailure, persisted.Detail, correlation);
        await Log(script.Id, operation, result, CancellationToken.None).ConfigureAwait(false);
        await ObserveCore(CancellationToken.None).ConfigureAwait(false); await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return result;
    }
    ReasonCode PolicyForReplacement(ScriptRecord script)
    {
        if (!owner.Held || !owner.Trusted) return ReasonCode.EffectOwnerUnavailable;
        if (!observed.Complete) return ReasonCode.ObservationIncomplete;
        if (observed.Processes.Any(p => ScriptPaths.Equal(p.Identity.ScriptPath, script.Path))) return ReasonCode.AlreadyRunning;
        if (observed.Processes.Any(p => IsSupervisorRelated(p.Identity.ScriptPath, script.Path))) return ReasonCode.LegacyGuard;
        return ReasonCode.None;
    }

    public Task<OperationResult> SaveSettingsAsync(ManagerSettings settings, bool startupConsent = false, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        bool registrationChange = settings.LaunchManagerAtLogin != document.Settings.LaunchManagerAtLogin;
        if (registrationChange && (!owner.Held || !owner.Trusted)) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (registrationChange && settings.LaunchManagerAtLogin && !startupConsent) return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
        if (settings.InterpreterPath is { Length: > 0 } path)
        {
            try { settings = settings with { InterpreterPath = AutoHotkeyDiscovery.Validate(path).Path }; }
            catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { return new(Outcome.Failed, ReasonCode.UnsupportedInterpreter, ex.Message); }
        }
        var saved = await Commit(document with { Settings = settings }, null, cancellationToken).ConfigureAwait(false);
        if (!saved.Succeeded || !registrationChange) return saved;
        var registered = await startup.SetEnabledAsync(settings.LaunchManagerAtLogin, startupConsent, CancellationToken.None).ConfigureAwait(false);
        await Log(null, null, registered, CancellationToken.None).ConfigureAwait(false); await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return registered.Succeeded ? saved : registered with { Outcome = Outcome.Partial };
    }, cancellationToken);
    public Task<OperationResult> RunLoginAsync(CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        if (!document.Settings.LaunchManagerAtLogin || startup.RegistrationStatus(true) != ReasonCode.None) return new(Outcome.Failed, ReasonCode.StartupInactive);
        if (!owner.Held || !owner.Trusted || owner.LoginKey.Length == 0) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (document.LastLoginKey == owner.LoginKey) return new(Outcome.Confirmed, ReasonCode.StartupAlreadyProcessed);
        var inspection = await startup.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!inspection.Complete) return new(Outcome.Failed, ReasonCode.ExternalStartupConflict);
        var reserve = await Commit(document with { LastLoginKey = owner.LoginKey }, null, cancellationToken).ConfigureAwait(false);
        if (!reserve.Succeeded) return reserve;
        bool partial = false;
        foreach (var script in document.Scripts.Where(s => s.RunAtLogin == true).ToArray())
        {
            if (cancellationToken.IsCancellationRequested) { partial = true; break; }
            await ObserveCore(CancellationToken.None).ConfigureAwait(false);
            var reason = script.RunAsAdministrator ? ReasonCode.ManualElevationRequired : inspection.ScriptPaths.Any(p => ScriptPaths.Equal(p, script.Path)) ? ReasonCode.ExternalStartupConflict : Policy(script, ScriptOperation.Start, null);
            if (reason == ReasonCode.AlreadyRunning) continue;
            if (reason != ReasonCode.None) { partial = true; await Log(script.Id, ScriptOperation.Start, new(Outcome.Failed, reason), CancellationToken.None).ConfigureAwait(false); continue; }
            var result = await ExecuteProcess(script, ScriptOperation.Start, null, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            partial |= !result.Succeeded;
            if (cancellationToken.IsCancellationRequested) break;
        }
        await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return new(partial ? Outcome.Partial : Outcome.Confirmed);
    }, cancellationToken);
    public Task<ImmutableArray<ActivitySnapshot>> GetActivityAsync(ActivityQuery query, CancellationToken cancellationToken = default) => Exclusive(() => activity.ReadAsync(query, cancellationToken), cancellationToken);
    public async Task<ImportPreview> PreviewImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        try { return (await importer.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false)).Preview; }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException) { return new(sourcePath, "", 0, [new(ReasonCode.ImportInvalid, ex.Message)], false); }
    }
    public Task<OperationResult> ImportAsync(ImportPreview preview, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        if (!owner.Held || !owner.Trusted) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (!preview.CanImport) return new(Outcome.Failed, ReasonCode.ImportInvalid);
        var import = await importer.ReadAsync(preview.SourcePath, cancellationToken).ConfigureAwait(false);
        if (import.Preview.SourceHash != preview.SourceHash) return new(Outcome.Failed, ReasonCode.ImportSourceChanged);
        var backup = await importer.PreserveSourceAsync(import, cancellationToken).ConfigureAwait(false);
        if (!backup.Succeeded) return backup;
        var paths = document.Scripts.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase); var ids = document.Scripts.Select(s => s.Id).ToHashSet();
        var scripts = document.Scripts.ToList();
        foreach (var script in import.Scripts)
        {
            if (!paths.Add(script.Path)) continue;
            var entry = script;
            if (!ids.Add(entry.Id)) { entry = entry with { Id = Guid.NewGuid() }; ids.Add(entry.Id); }
            scripts.Add(entry);
        }
        // Imported preferences do not change the Windows registration or Manager login preference.
        return await Commit(document with { Scripts = scripts }, null, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
    public Task<OperationResult> RestoreBackupAsync(CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        var restored = await store.RestoreBackupAsync(cancellationToken).ConfigureAwait(false);
        if (!restored.Succeeded) return restored;
        var result = await store.LoadAsync(cancellationToken).ConfigureAwait(false); document = result.Document; storageIssues = result.Issues;
        await Publish(true, cancellationToken).ConfigureAwait(false); return restored;
    }, cancellationToken);
    public Task<OperationResult> RecoverPendingAsync(CancellationToken cancellationToken = default) => ResultAction(() => RecoverCore(cancellationToken), cancellationToken);
    async Task<OperationResult> RecoverCore(CancellationToken ct)
    {
        if (!owner.Held || !owner.Trusted) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (!journal.Ready) return new(Outcome.Failed, ReasonCode.PendingOperation);
        await ObserveCore(ct).ConfigureAwait(false); bool unresolved = false;
        foreach (var record in journal.Records.Where(OperationJournal.IsPending).ToArray())
        {
            var targets = observed.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, record.ScriptPath)).ToArray();
            bool same = record.Identity is not null && targets.Any(p => NativeProcessIdentity.Same(p.Identity, record.Identity));
            Outcome? resolution = (record.Operation == ScriptOperation.Start || record.Operation == ScriptOperation.Restart && record.RestartOldExited) && same ? Outcome.Confirmed : observed.Complete && !targets.Any() ? Outcome.Partial : null;
            if (resolution is null) { unresolved = true; continue; }
            var saved = await journal.PutAsync(record with { Outcome = resolution.Value, Code = resolution == Outcome.Partial ? ReasonCode.LaunchUncertain : ReasonCode.None }, ct).ConfigureAwait(false);
            if (!saved.Succeeded) { unresolved = true; break; }
        }
        var scripts = document.Scripts.ToList(); bool changed = false;
        for (int i = 0; i < scripts.Count; i++)
            if (scripts[i].LegacyGuard)
            {
                if (await importer.GuardClearedAsync(scripts[i], ct).ConfigureAwait(false)) { scripts[i] = scripts[i] with { LegacyGuard = false }; changed = true; }
                else unresolved = true;
            }
        if (changed) { var result = await Commit(document with { Scripts = scripts }, null, ct).ConfigureAwait(false); if (!result.Succeeded) return result; }
        await Publish(false, ct).ConfigureAwait(false);
        return new(unresolved ? Outcome.Unknown : Outcome.Confirmed, unresolved ? ReasonCode.PendingOperation : ReasonCode.None);
    }
    public async ValueTask DisposeAsync()
    {
        await lane.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; processes.Invalidated -= ProcessInvalidated; await processes.DisposeAsync().ConfigureAwait(false); owner.Dispose(); }
        finally { lane.Release(); }
    }
}
