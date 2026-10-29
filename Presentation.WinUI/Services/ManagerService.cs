using System.Collections.Immutable;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed partial class ManagerService : IManagerService
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
    readonly Dictionary<Guid, (RecyclePlan Plan, DateTimeOffset Time)> recyclePlans = [];
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
            await LoadStartupRecovery().ConfigureAwait(false);
            if (document.Settings.InterpreterPath is null)
            {
                var candidates = await new AutoHotkeyDiscovery().DiscoverAsync(cancellationToken).ConfigureAwait(false);
                if (candidates.Length > 0) document = document with { Settings = document.Settings with { InterpreterPath = candidates[0].Path } };
            }
            loaded = true;
            await ObserveCore(cancellationToken).ConfigureAwait(false); await ReconcileObservedCore(cancellationToken).ConfigureAwait(false); await Publish(true, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!loaded) { await LoadAsync(cancellationToken).ConfigureAwait(false); return; }
        await Exclusive(async () => { await ObserveCore(cancellationToken).ConfigureAwait(false); await ReconcileObservedCore(cancellationToken).ConfigureAwait(false); await Publish(true, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
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
            bool pending = journal.Records.Any(r => OperationJournal.IsPending(r) && (r.ScriptId == entry.Id && r.Operation is ScriptOperation.Start or ScriptOperation.Restart or ScriptOperation.Stop || r.Operation == ScriptOperation.RecycleFile && (r.ScriptId == entry.Id || ScriptPaths.Equal(r.ScriptPath, entry.Path))));
            ScriptStatus status = matching.Length > 1 ? ScriptStatus.Multiple : pending ? ScriptStatus.Unknown : matching.Length == 1 ? ScriptStatus.Running : !exists ? ScriptStatus.Missing : !observed.Complete ? ScriptStatus.Unknown : entry.LegacyGuard || entry.LaunchBlocked ? ScriptStatus.Blocked : ScriptStatus.Stopped;
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
        if (!journal.Ready) global = global.Add(new(ReasonCode.PendingOperation, "The launch history could not be read. Launch actions are unavailable."));
        else foreach (var request in journal.Records.Where(OperationJournal.IsPending).Take(8)) global = global.Add(new(ReasonCode.PendingOperation, PendingRequestDetail(request)));
        if (startupRecoveryPending) global = global.Add(new(ReasonCode.PendingOperation, "The startup continuation could not be verified. New launches are blocked to prevent duplicate instances."));
        var registration = startup.RegistrationStatus(document.Settings.LaunchManagerAtLogin, document.Settings.LaunchManagerAsAdministrator);
        if (registration != ReasonCode.None) global = global.Add(new(registration));
        ImmutableArray<ActivitySnapshot> recent;
        try { recent = await activity.ReadAsync(new(), ct).ConfigureAwait(false); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { recent = []; global = global.Add(new(ReasonCode.ActivityUnavailable, ex.Message)); }
        var next = new ManagerSnapshot(snapshot.Revision + 1, HotkeyAnalyzer.MarkConflicts(scripts.ToImmutable()), document.Collections.ToImmutableArray(), document.Settings, recent, global.Distinct().ToImmutableArray(), store.ReadOnly, owner.Held && owner.Trusted && journal.Ready && !store.ReadOnly && (!startupRecoveryPending || startupResumeActive), observedAt, processes.GetNormalLaunchUnavailableReason());
        Volatile.Write(ref snapshot, next); Changed?.Invoke(this, next);
    }
    string PendingRequestDetail(OperationRecord record)
    {
        string name = document.Scripts.FirstOrDefault(s => s.Id == record.ScriptId)?.FriendlyName ?? Path.GetFileNameWithoutExtension(record.ScriptPath);
        string action = record.Operation switch { ScriptOperation.Start => "starting", ScriptOperation.Stop => "stopping", ScriptOperation.Restart => "restarting", ScriptOperation.TogglePause => "changing the pause state of", ScriptOperation.ToggleSuspend => "changing the hotkey suspension state of", ScriptOperation.RecycleFile => "recycling", _ => "changing" };
        return $"The outcome of {action} {name} could not be confirmed. Manager will not replay this request automatically.";
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
            var registered = document.Settings.LaunchManagerAsAdministrator && !processes.IsAdministrator
                ? new OperationResult(Outcome.Failed, ReasonCode.ManualElevationRequired, "The preference was saved. Reopen Manager as administrator to finish configuring Windows startup.")
                : await startup.SetEnabledAsync(true, true, document.Settings.LaunchManagerAsAdministrator, CancellationToken.None).ConfigureAwait(false);
            await Publish(false, CancellationToken.None).ConfigureAwait(false);
            if (!registered.Succeeded) return registered with { Outcome = Outcome.Partial };
        }
        return saved;
    }, cancellationToken);

    bool RecyclePending(Guid id, string path) => journal.Records.Any(r => r.Operation == ScriptOperation.RecycleFile && OperationJournal.IsPending(r) && (r.ScriptId == id || ScriptPaths.Equal(r.ScriptPath, path)));
    ReasonCode FileAuthority(ScriptRecord script)
    {
        if (store.ReadOnly) return ReasonCode.ReadOnlySettings;
        if (!owner.Held || !owner.Trusted) return ReasonCode.EffectOwnerUnavailable;
        if (!journal.Ready || RecyclePending(script.Id, script.Path) || journal.Records.Any(r => OperationJournal.IsPending(r) && (r.ScriptId == script.Id || ScriptPaths.Equal(r.ScriptPath, script.Path)))) return ReasonCode.PendingOperation;
        if (script.LegacyGuard) return ReasonCode.LegacyGuard;
        if (!observed.Complete) return ReasonCode.ObservationIncomplete;
        return ReasonCode.None;
    }
    public Task<OperationResult> RelinkAsync(SelectionContext selection, string oldPath, string replacementPath, bool startupConsent, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        var script = document.Scripts.SingleOrDefault(s => s.Id == selection.ScriptId);
        if (script is null || !ValidSelection(selection, true) || !ScriptPaths.Equal(script.Path, oldPath)) return new(Outcome.Failed, ReasonCode.StaleSelection);
        if (File.Exists(oldPath)) { await Publish(true, cancellationToken).ConfigureAwait(false); return new(Outcome.Failed, ReasonCode.StaleSelection); }
        await ObserveCore(cancellationToken).ConfigureAwait(false);
        var denied = FileAuthority(script); if (denied != ReasonCode.None) return new(Outcome.Failed, denied);
        string path;
        try { path = ScriptPaths.Canonical(replacementPath); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { return new(Outcome.Failed, ReasonCode.InvalidPath, ex.Message); }
        if (document.Scripts.Any(s => s.Id != script.Id && ScriptPaths.Equal(s.Path, path))) return new(Outcome.Failed, ReasonCode.InvalidPath);
        if (RecyclePending(script.Id, path) || journal.Records.Any(r => OperationJournal.IsPending(r) && ScriptPaths.Equal(r.ScriptPath, path))) return new(Outcome.Failed, ReasonCode.PendingOperation);
        if (observed.Processes.Any(p => ScriptPaths.Equal(p.Identity.ScriptPath, oldPath) || ScriptPaths.Equal(p.Identity.ScriptPath, path))) return new(Outcome.Failed, ReasonCode.AlreadyRunning);
        if (script.RunAtLogin == true && !startupConsent) return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
        if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false)) return new(Outcome.Failed, ReasonCode.ConcurrentSettingsChange);
        if (File.Exists(oldPath)) { await Publish(true, cancellationToken).ConfigureAwait(false); return new(Outcome.Failed, ReasonCode.StaleSelection); }
        return await Commit(document with { Scripts = document.Scripts.Select(s => s.Id == script.Id ? s with { Path = path } : s).ToList() }, script.Id, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
    public Task<RecyclePlan> PrepareRecycleAsync(SelectionContext selection, CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        var script = document.Scripts.SingleOrDefault(s => s.Id == selection.ScriptId);
        ReasonCode denied = script is null || !ValidSelection(selection, true) ? ReasonCode.StaleSelection : ReasonCode.None;
        ScriptFileIdentity? file = null; ProcessIdentity? identity = null;
        if (denied == ReasonCode.None)
        {
            await ObserveCore(cancellationToken).ConfigureAwait(false);
            denied = FileAuthority(script!);
            var matches = observed.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, script!.Path)).ToArray();
            if (denied == ReasonCode.None && matches.Length > 1) denied = ReasonCode.MultipleInstances;
            if (denied == ReasonCode.None && matches.Length == 1)
            {
                identity = matches[0].Identity;
                denied = !matches[0].WindowVerified ? ReasonCode.WindowUnverified : Policy(script!, ScriptOperation.Stop, identity);
            }
            if (denied == ReasonCode.None)
            {
                try { file = ScriptPaths.FileIdentity(script!.Path); }
                catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { denied = ReasonCode.InvalidPath; }
            }
        }
        var plan = new RecyclePlan(Guid.NewGuid(), selection.ScriptId, script?.Path ?? "", file, identity, selection, denied == ReasonCode.None, denied);
        foreach (var key in recyclePlans.Where(p => DateTimeOffset.UtcNow - p.Value.Time > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray()) recyclePlans.Remove(key);
        if (recyclePlans.Count < 128 && plan.Allowed) recyclePlans[plan.OperationId] = (plan, DateTimeOffset.UtcNow);
        return plan;
    }, cancellationToken);
    public Task<RecycleAuthorization> AuthorizeRecycleAsync(Guid operationId, SelectionContext selection, bool confirmed, CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        bool stopped = false;
        RecycleAuthorization Deny(ReasonCode code, string? detail = null, Outcome outcome = Outcome.Failed) => new(new(stopped && outcome != Outcome.Unknown ? Outcome.Partial : outcome, code, stopped ? "The running instance was stopped. " + detail : detail, operationId), null, stopped);
        if (!recyclePlans.Remove(operationId, out var held) || DateTimeOffset.UtcNow - held.Time > TimeSpan.FromMinutes(2) || held.Plan.Selection != selection || !ValidSelection(selection, true)) return Deny(ReasonCode.StaleSelection);
        if (!confirmed) return Deny(ReasonCode.Cancelled, outcome: Outcome.CancelledBeforeDispatch);
        var plan = held.Plan;
        var script = document.Scripts.SingleOrDefault(s => s.Id == plan.ScriptId);
        if (script is null || !ScriptPaths.Equal(script.Path, plan.Path)) return Deny(ReasonCode.StaleSelection);
        try
        {
            if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false)) return Deny(ReasonCode.ConcurrentSettingsChange);
            await ObserveCore(cancellationToken).ConfigureAwait(false);
            var denied = FileAuthority(script); if (denied != ReasonCode.None) return Deny(denied);
            if (ScriptPaths.FileIdentity(plan.Path) != plan.File) return Deny(ReasonCode.IdentityChanged);
            var matches = observed.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, plan.Path)).ToArray();
            if (matches.Length > 0)
            {
                if (matches.Length != 1 || plan.ExpectedIdentity is null || !NativeProcessIdentity.Same(matches[0].Identity, plan.ExpectedIdentity)) return Deny(ReasonCode.IdentityChanged);
                denied = !matches[0].WindowVerified ? ReasonCode.WindowUnverified : Policy(script, ScriptOperation.Stop, plan.ExpectedIdentity);
                if (denied != ReasonCode.None) return Deny(denied);
                var stop = await ExecuteProcess(script, ScriptOperation.Stop, plan.ExpectedIdentity, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                stopped = stop.StopConfirmed;
                if (!stop.Result.Succeeded) return Deny(stop.Result.Code, stop.Result.Detail, stop.Result.Outcome);
            }
            await ObserveCore(cancellationToken).ConfigureAwait(false);
            if (!observed.Complete) return Deny(ReasonCode.ObservationIncomplete);
            if (observed.Processes.Any(p => ScriptPaths.Equal(p.Identity.ScriptPath, plan.Path))) return Deny(ReasonCode.AlreadyRunning);
            denied = FileAuthority(script); if (denied != ReasonCode.None) return Deny(denied);
            if (!await store.VerifyLoadedAsync(cancellationToken).ConfigureAwait(false)) return Deny(ReasonCode.ConcurrentSettingsChange);
            if (ScriptPaths.FileIdentity(plan.Path) != plan.File) return Deny(ReasonCode.IdentityChanged);
            cancellationToken.ThrowIfCancellationRequested();
            var record = new OperationRecord(operationId, script.Id, ScriptOperation.RecycleFile, plan.Path, "", plan.File!.Hash, "", owner.Sid, owner.LoginKey, DateTimeOffset.UtcNow, Outcome.Pending, plan.ExpectedIdentity, RecycleStopped: stopped);
            var saved = await journal.PutAsync(record, CancellationToken.None).ConfigureAwait(false);
            await Publish(false, CancellationToken.None).ConfigureAwait(false);
            return saved.Succeeded ? new RecycleAuthorization(new(Outcome.Pending, CorrelationId: operationId), plan.File, stopped) : Deny(saved.Code, saved.Detail, Outcome.Unknown);
        }
        catch (OperationCanceledException) { return Deny(ReasonCode.Cancelled, outcome: Outcome.CancelledBeforeDispatch); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return Deny(ReasonCode.StorageFailure, ex.Message); }
    }, cancellationToken);
    public Task<OperationResult> CompleteRecycleAsync(Guid operationId, Outcome receipt, string? detail, CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        var record = journal.Records.SingleOrDefault(r => r.Id == operationId && r.Operation == ScriptOperation.RecycleFile);
        if (record is null || receipt is not (Outcome.Confirmed or Outcome.Failed or Outcome.Unknown)) return new(Outcome.Failed, ReasonCode.InvalidMetadata);
        if (record.Outcome == Outcome.Confirmed || record.RecycleReceipt || !OperationJournal.IsPending(record)) return new(record.Outcome, record.Code, CorrelationId: operationId);
        if (receipt != Outcome.Confirmed)
        {
            var outcome = receipt == Outcome.Unknown ? Outcome.Unknown : record.RecycleStopped ? Outcome.Partial : Outcome.Failed;
            var result = new OperationResult(outcome, receipt == Outcome.Unknown ? ReasonCode.PendingOperation : ReasonCode.DispatchFailed, (record.RecycleStopped ? "The running instance was stopped. " : "") + detail, operationId);
            var save = await journal.PutAsync(record with { Outcome = outcome, Code = result.Code }, CancellationToken.None).ConfigureAwait(false);
            await Log(record.ScriptId, ScriptOperation.RecycleFile, result, CancellationToken.None).ConfigureAwait(false);
            await Publish(false, CancellationToken.None).ConfigureAwait(false);
            return save.Succeeded ? result : new(Outcome.Unknown, ReasonCode.StorageFailure, detail, operationId);
        }
        // Receipt is durable before catalog mutation; absence alone never reaches this branch.
        record = record with { RecycleReceipt = true };
        var received = await journal.PutAsync(record, CancellationToken.None).ConfigureAwait(false);
        if (!received.Succeeded) return new(Outcome.Unknown, ReasonCode.StorageFailure, "The file was moved to the Recycle Bin, but its receipt could not be saved.", operationId);
        var script = document.Scripts.SingleOrDefault(s => s.Id == record.ScriptId);
        OperationResult committed;
        if (File.Exists(record.ScriptPath) || script is null || !ScriptPaths.Equal(script.Path, record.ScriptPath) || !await store.VerifyLoadedAsync(CancellationToken.None).ConfigureAwait(false)) committed = new(Outcome.Partial, ReasonCode.ConcurrentSettingsChange, "The file was moved to the Recycle Bin. Review and remove its entry from the list.", operationId);
        else committed = await Commit(document with { Scripts = document.Scripts.Where(s => s.Id != record.ScriptId).ToList() }, record.ScriptId, CancellationToken.None).ConfigureAwait(false);
        var final = committed.Succeeded ? new OperationResult(Outcome.Confirmed, CorrelationId: operationId) : new(Outcome.Partial, committed.Code, "The file was moved to the Recycle Bin, but its entry was preserved. Remove it from the list after fixing storage.", operationId);
        var persisted = await journal.PutAsync(record with { Outcome = final.Outcome, Code = final.Code }, CancellationToken.None).ConfigureAwait(false);
        await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return persisted.Succeeded ? final : new(Outcome.Unknown, ReasonCode.StorageFailure, final.Detail, operationId);
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
        if (operation == ScriptOperation.RecycleFile) return ReasonCode.CapabilityUnverified;
        if (RecyclePending(script.Id, script.Path)) return ReasonCode.PendingOperation;
        if (startupRecoveryPending && !startupResumeActive) return ReasonCode.PendingOperation;
        if (!owner.Held || !owner.Trusted) return ReasonCode.EffectOwnerUnavailable;
        if (store.ReadOnly) return ReasonCode.ReadOnlySettings;
        if (!journal.Ready || journal.Records.Any(r => OperationJournal.IsPending(r) && (r.Operation is ScriptOperation.Start or ScriptOperation.Restart || r.ScriptId == script.Id && (r.Operation == operation || r.Operation == ScriptOperation.Stop)))) return ReasonCode.PendingOperation;
        if (script.LegacyGuard) return ReasonCode.LegacyGuard;
        if (!processes.Supports(operation)) return ReasonCode.CapabilityUnverified;
        var matches = observed.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, script.Path)).ToArray();
        if (operation is ScriptOperation.Start or ScriptOperation.Restart)
        {
            if (!script.RunAsAdministrator && processes.GetNormalLaunchUnavailableReason() is ReasonCode launchDenied) return launchDenied;
            if (script.LaunchBlocked) return ReasonCode.LegacyGuard;
            if (!File.Exists(script.Path)) return ReasonCode.MissingFile;
            if (!observed.Complete) return ReasonCode.ObservationIncomplete;
            if (string.IsNullOrWhiteSpace(script.InterpreterPath ?? document.Settings.InterpreterPath)) return ReasonCode.UnsupportedInterpreter;
            if (script.RunAsAdministrator && operation == ScriptOperation.Restart && !processes.IsAdministrator) return ReasonCode.ManualElevationRequired;
            if (observed.Processes.Any(p => IsSupervisorRelated(p.Identity.ScriptPath, script.Path))) return ReasonCode.LegacyGuard;
        }
        if (operation == ScriptOperation.Start) return matches.Length > 0 ? ReasonCode.AlreadyRunning : ReasonCode.None;
        if (matches.Length != 1) return matches.Length > 1 ? ReasonCode.MultipleInstances : ReasonCode.IdentityChanged;
        if (matches[0].ControlUnavailableReason is ReasonCode access) return access;
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
        return (await ExecuteProcess(script, plan.Operation, plan.ExpectedIdentity, plan.CorrelationId, cancellationToken).ConfigureAwait(false)).Result;
    }, cancellationToken);
    async Task<(OperationResult Result, bool StopConfirmed)> ExecuteProcess(ScriptRecord script, ScriptOperation operation, ProcessIdentity? expected, Guid correlation, CancellationToken ct)
    {
        string interpreter = script.InterpreterPath ?? document.Settings.InterpreterPath ?? "";
        string scriptHash = "", interpreterHash = "";
        async Task<(OperationResult Result, bool StopConfirmed)> Refused(OperationResult result)
        {
            result = result with { CorrelationId = correlation };
            await Log(script.Id, operation, result with { Detail = $"{script.FriendlyName}: {result.Detail ?? LoginReason(result.Code)}" }, CancellationToken.None).ConfigureAwait(false);
            return (result, false);
        }
        var continuationRefusal = await InvalidatePreparedStartupForStart(script.Id, operation).ConfigureAwait(false);
        if (continuationRefusal != ReasonCode.None) return await Refused(new(Outcome.Failed, continuationRefusal)).ConfigureAwait(false);
        if (operation is ScriptOperation.Start or ScriptOperation.Restart)
        {
            try { var verified = AutoHotkeyDiscovery.Validate(interpreter); interpreter = verified.Path; interpreterHash = verified.Hash; scriptHash = ScriptPaths.Fingerprint(ScriptPaths.Canonical(script.Path)); await AutoHotkeyDiscovery.ValidateScriptVersionAsync(script.Path, verified, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return await Refused(new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)).ConfigureAwait(false); }
            catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { return await Refused(new(Outcome.Failed, ReasonCode.UnsupportedInterpreter, ex.Message)).ConfigureAwait(false); }
        }
        var record = new OperationRecord(correlation, script.Id, operation, script.Path, interpreter, scriptHash, interpreterHash, owner.Sid, owner.LoginKey, DateTimeOffset.UtcNow, Outcome.Pending, expected);
        OperationResult reserved;
        try { reserved = await journal.PutAsync(record, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return await Refused(new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)).ConfigureAwait(false); }
        if (!reserved.Succeeded) return await Refused(reserved).ConfigureAwait(false);
        await Log(script.Id, operation, new(Outcome.Pending, CorrelationId: correlation), CancellationToken.None).ConfigureAwait(false);
        OperationResult result;
        bool oldExited = false;
        bool stopConfirmed = false;
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
                    stopConfirmed = operation == ScriptOperation.Stop && result.Succeeded;
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
                            if (!marked.Succeeded) { await Publish(false, CancellationToken.None).ConfigureAwait(false); return (new(Outcome.Partial, ReasonCode.RestartPartial, marked.Detail, correlation), stopConfirmed); }
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
        return (result, stopConfirmed);
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
        bool registrationChange = settings.LaunchManagerAtLogin != document.Settings.LaunchManagerAtLogin
            || settings.LaunchManagerAtLogin && settings.LaunchManagerAsAdministrator != document.Settings.LaunchManagerAsAdministrator;
        bool registrationNeeded = registrationChange || startup.RegistrationStatus(settings.LaunchManagerAtLogin, settings.LaunchManagerAsAdministrator) != ReasonCode.None;
        if (registrationNeeded && (!owner.Held || !owner.Trusted)) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (registrationChange && settings.LaunchManagerAtLogin && !startupConsent) return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
        if (settings.InterpreterPath is { Length: > 0 } path)
        {
            try { settings = settings with { InterpreterPath = AutoHotkeyDiscovery.Validate(path).Path }; }
            catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { return new(Outcome.Failed, ReasonCode.UnsupportedInterpreter, ex.Message); }
        }
        var saved = await Commit(document with { Settings = settings }, null, cancellationToken).ConfigureAwait(false);
        if (!saved.Succeeded || !registrationNeeded) return saved;
        // A saved enabled preference is durable intent. Reconcile the actual Windows
        // registration on a later save too, including after a previous partial result.
        var registered = settings.LaunchManagerAtLogin && settings.LaunchManagerAsAdministrator && !processes.IsAdministrator
            ? new OperationResult(Outcome.Failed, ReasonCode.ManualElevationRequired, "The preferences were saved. Confirm Windows permission to finish configuring administrator autostart.")
            : await startup.SetEnabledAsync(settings.LaunchManagerAtLogin, startupConsent || document.Settings.LaunchManagerAtLogin, settings.LaunchManagerAsAdministrator, CancellationToken.None).ConfigureAwait(false);
        await Log(null, null, registered, CancellationToken.None).ConfigureAwait(false); await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return registered.Succeeded ? saved : registered with { Outcome = Outcome.Partial };
    }, cancellationToken);
    public Task<OperationResult> RunLoginAsync(CancellationToken cancellationToken = default) => ResultAction(async () =>
    {
        async Task<OperationResult> Abort(string stage, OperationResult result, ImmutableArray<Issue> issues = default)
        {
            string detail = $"Windows startup stopped while {stage}. {LoginReason(result.Code)}";
            if (!issues.IsDefaultOrEmpty)
                detail += "\n" + string.Join("\n", issues.Take(8).Select(issue => issue.Detail is { Length: > 0 } reason ? reason[..Math.Min(reason.Length, 240)] : LoginReason(issue.Code)));
            var explained = result with { Detail = detail };
            await Log(null, ScriptOperation.Start, explained, CancellationToken.None).ConfigureAwait(false);
            await Publish(false, CancellationToken.None).ConfigureAwait(false);
            return explained;
        }
        var registration = document.Settings.LaunchManagerAtLogin ? startup.RegistrationStatus(true, document.Settings.LaunchManagerAsAdministrator) : ReasonCode.StartupInactive;
        if (registration != ReasonCode.None) return await Abort("checking the startup configuration", new(Outcome.Failed, registration)).ConfigureAwait(false);
        if (!owner.Held || !owner.Trusted || owner.LoginKey.Length == 0) return await Abort("obtaining permission to manage scripts", new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable)).ConfigureAwait(false);
        if (document.LastLoginKey == owner.LoginKey) return new(Outcome.Confirmed, ReasonCode.StartupAlreadyProcessed);
        StartupInspection inspection;
        try { inspection = await startup.InspectAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return await Abort("checking other startup configurations", new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)).ConfigureAwait(false); }
        if (!inspection.Complete) return await Abort("checking other startup configurations", new(Outcome.Failed, ReasonCode.ExternalStartupConflict), inspection.Issues).ConfigureAwait(false);
        OperationResult reserve;
        try { reserve = await Commit(document with { LastLoginKey = owner.LoginKey }, null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return await Abort("saving this session's startup record", new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)).ConfigureAwait(false); }
        if (!reserve.Succeeded) return await Abort("saving this session's startup record", reserve).ConfigureAwait(false);
        int started = 0, running = 0, failed = 0; var details = new List<string>();
        var continuationCandidates = new List<ScriptRecord>();
        void Failure(ScriptRecord script, ReasonCode reason)
        {
            failed++;
            if (details.Count < 24) details.Add($"{script.FriendlyName}: {LoginReason(reason)}");
        }
        foreach (var script in document.Scripts.Where(s => s.RunAtLogin == true).ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Failure(script, ReasonCode.Cancelled);
                await Log(script.Id, ScriptOperation.Start, new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled, $"{script.FriendlyName}: {LoginReason(ReasonCode.Cancelled)}"), CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            await ObserveCore(CancellationToken.None).ConfigureAwait(false);
            var reason = cancellationToken.IsCancellationRequested ? ReasonCode.Cancelled : script.RunAsAdministrator && !processes.IsAdministrator ? ReasonCode.ManualElevationRequired : inspection.ScriptPaths.Any(p => ScriptPaths.Equal(p, script.Path)) ? ReasonCode.ExternalStartupConflict : Policy(script, ScriptOperation.Start, null);
            if (reason == ReasonCode.AlreadyRunning) { running++; continue; }
            if (reason != ReasonCode.None)
            {
                if (reason == ReasonCode.ObservationIncomplete && !script.RunAsAdministrator) continuationCandidates.Add(script);
                Failure(script, reason); await Log(script.Id, ScriptOperation.Start, new(reason == ReasonCode.Cancelled ? Outcome.CancelledBeforeDispatch : Outcome.Failed, reason, $"{script.FriendlyName}: {LoginReason(reason)}"), CancellationToken.None).ConfigureAwait(false); continue;
            }
            var execution = await ExecuteProcess(script, ScriptOperation.Start, null, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            if (execution.Result.Succeeded) started++; else Failure(script, execution.Result.Code);
        }
        string detail = $"Started: {started}. Already running: {running}. Not started or unconfirmed: {failed}.";
        if (details.Count > 0) detail += "\n\n" + string.Join("\n", details);
        if (failed > details.Count) detail += "\nSee Activity for the remaining results.";
        if (continuationCandidates.Count > 0 && !processes.IsAdministrator && !journal.Records.Any(OperationJournal.IsPending))
        {
            try { await PrepareStartupContinuationCore(continuationCandidates).ConfigureAwait(false); detail += "\n\nYou can request Windows permission to inspect processes and continue only the standard-privilege scripts that have not been started."; }
            catch (Exception ex) when (SafeJsonFiles.Known(ex)) { detail += "\nStartup could not be continued safely."; }
        }
        var summary = new OperationResult(failed > 0 ? Outcome.Partial : Outcome.Confirmed, Detail: detail);
        await Log(null, ScriptOperation.Start, summary, CancellationToken.None).ConfigureAwait(false);
        await Publish(false, CancellationToken.None).ConfigureAwait(false);
        return summary;
    }, cancellationToken);
    static string LoginReason(ReasonCode reason) => reason switch
    {
        ReasonCode.ManualElevationRequired => "Administrator autostart was skipped because Manager does not have administrator access. A manual launch can still request Windows permission.",
        ReasonCode.ObservationIncomplete => "The process inventory is incomplete; starting another instance is unsafe.",
        ReasonCode.PendingOperation => "Not started: the result of a previous request is still unconfirmed. Manager will not replay the launch automatically.",
        ReasonCode.LaunchUncertain => "The launch outcome is unknown. A new launch is blocked to prevent duplicate instances.",
        ReasonCode.AccessDenied => "Windows denied access to the file or process.",
        ReasonCode.MissingFile => "The file was not found.",
        ReasonCode.UnsupportedInterpreter => "The selected interpreter or the version required by the script is incompatible.",
        ReasonCode.ExternalStartupConflict => "Another automatic startup configuration may launch a duplicate of this script.",
        ReasonCode.LegacyGuard => "A catalog safeguard or launch block prevents this script from starting.",
        ReasonCode.Cancelled => "The launch was cancelled.",
        ReasonCode.DispatchFailed => "The launch request could not be dispatched.",
        ReasonCode.EffectOwnerUnavailable => "The authority required to start scripts is unavailable.",
        ReasonCode.StartupInactive => "Windows startup is disabled or unregistered. Review this option in Preferences.",
        ReasonCode.StartupEntryConflict => "The Windows startup entry belongs to another configuration. Review it in Preferences.",
        ReasonCode.ConcurrentSettingsChange => "Another instance changed the settings. Refresh the state before trying again.",
        ReasonCode.ReadOnlySettings or ReasonCode.StorageFailure => "The data must be recovered before scripts can start.",
        _ => $"Startup is unavailable ({reason}). See Activity."
    };
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
    async Task ReconcileObservedCore(CancellationToken ct)
    {
        if (!owner.Held || !owner.Trusted || !journal.Ready) return;
        if (!await ResolveObservedPendingCore(ct).ConfigureAwait(false)) await SettleExpiredStartupContinuationsCore().ConfigureAwait(false);
    }
    async Task<bool> ResolveObservedPendingCore(CancellationToken ct)
    {
        bool unresolved = false;
        foreach (var record in journal.Records.Where(OperationJournal.IsPending).ToArray())
        {
            if (record.Operation == ScriptOperation.RecycleFile) { unresolved = true; continue; }
            var targets = observed.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, record.ScriptPath)).ToArray();
            bool same = record.Identity is not null && targets.Any(p => NativeProcessIdentity.Same(p.Identity, record.Identity));
            Outcome? resolution = (record.Operation == ScriptOperation.Start || record.Operation == ScriptOperation.Restart && record.RestartOldExited) && same ? Outcome.Confirmed : observed.Complete && !targets.Any() ? Outcome.Partial : null;
            if (resolution is null) { unresolved = true; continue; }
            var saved = await journal.PutAsync(record with { Outcome = resolution.Value, Code = resolution == Outcome.Partial ? ReasonCode.LaunchUncertain : ReasonCode.None }, ct).ConfigureAwait(false);
            if (!saved.Succeeded) { unresolved = true; break; }
        }
        return unresolved;
    }
    async Task<OperationResult> RecoverCore(CancellationToken ct)
    {
        if (!owner.Held || !owner.Trusted) return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (!journal.Ready) return new(Outcome.Failed, ReasonCode.PendingOperation);
        await ObserveCore(ct).ConfigureAwait(false); bool unresolved = await ResolveObservedPendingCore(ct).ConfigureAwait(false);
        var scripts = document.Scripts.ToList(); bool changed = false;
        for (int i = 0; i < scripts.Count; i++)
            if (scripts[i].LegacyGuard)
            {
                if (await importer.GuardClearedAsync(scripts[i], ct).ConfigureAwait(false)) { scripts[i] = scripts[i] with { LegacyGuard = false }; changed = true; }
            }
        if (changed) { var result = await Commit(document with { Scripts = scripts }, null, ct).ConfigureAwait(false); if (!result.Succeeded) return result; }
        if (startupRecoveryPending && !await RecoverStartupContinuationCore().ConfigureAwait(false)) unresolved = true;
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
