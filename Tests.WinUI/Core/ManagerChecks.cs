using System.Collections.Immutable;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.WinUI.Tests.Core;

public static partial class CoreSuite
{
    static string InterpreterFixture = "";
    public static async Task RunAdministratorRestartChecksAsync(string root, Action<bool, string> check)
    {
        string interpreter = (await new AutoHotkeyDiscovery().DiscoverAsync())[0].Path;
        string script = Path.Combine(root, "admin-restart.ahk");
        await File.WriteAllTextAsync(script, "#Requires AutoHotkey v2.0\n");
        var owner = new FakeOwner(root); var processes = new FakeProcesses(owner);
        await using var manager = new ManagerService(Path.Combine(root, "data"), owner, processes, new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe")));
        await manager.LoadAsync(); var added = await manager.AddFilesAsync([script]); Guid id = added.Added.Single();
        await manager.SaveMetadataAsync(id, new("Admin restart", "", null, false, true, false, []));
        processes.SetRunning(script, interpreter); await manager.RefreshAsync();
        check(!(await manager.PrepareAsync(Select(manager, id), ScriptOperation.Restart)).Allowed, "F5 normal authority refuses admin restart");
        processes.Administrator = true;
        var restart = await manager.PrepareAsync(Select(manager, id), ScriptOperation.Restart);
        check(restart.Allowed, "F5 authenticated high authority permits admin restart plan");
        if (restart.Allowed)
        {
            processes.NextControl = new(Outcome.Unknown, ReasonCode.ExitNotObserved);
            check((await manager.PerformAsync(restart, restart.Selection, true)).Outcome == Outcome.Unknown && processes.Starts == 0, "F5 uncertain stop never creates admin replacement");
            processes.Current = new([], true, []); await manager.RecoverPendingAsync();
            processes.SetRunning(script, interpreter); processes.NextControl = new(Outcome.Confirmed); await manager.RefreshAsync();
            restart = await manager.PrepareAsync(Select(manager, id), ScriptOperation.Restart);
            check((await manager.PerformAsync(restart, restart.Selection, true)).Succeeded && processes.Starts == 1 && processes.LastElevated, "F5 confirmed stop launches one explicit admin replacement");
        }
    }
    static async Task RunManagerAsync()
    {
        var interpreters = await new AutoHotkeyDiscovery().DiscoverAsync();
        Check(interpreters.Length > 0, "environment has a validated real v2 executable for read-only version fixtures");
        string interpreter = interpreters[0].Path;
        InterpreterFixture = interpreter;
        await FileActionsAsync(interpreter);
        await HeaderDescriptionsAsync(interpreter);
        await PreserveInactiveStartupAsync(interpreter);
        var root = Path.Combine(FixtureRoot, "manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "ação com espaço.ahk");
        await File.WriteAllTextAsync(script, "#Requires AutoHotkey v2.0\n#SingleInstance Off\n^!a::return\n");
        var fixtureOwner = new FakeOwner(root);
        var boundary = new FakeProcesses(fixtureOwner);
        var registration = new FakeStartup();
        var startup = new UserStartupService(registration, Path.Combine(root, "AHK Manager.exe"));
        await using var manager = new ManagerService(Path.Combine(root, "data"), fixtureOwner, boundary, startup);
        await manager.LoadAsync();
        Check(!manager.Snapshot.Settings.LaunchManagerAtLogin && registration.Writes == 0, "loading performs no startup registration");
        var added = await manager.AddFilesAsync(new[] { script, script.ToUpperInvariant(), Path.Combine(root, "missing.ahk") });
        Check(added.Added.Length == 1 && added.DuplicateCount == 1 && added.Result.Outcome == Outcome.Partial, "canonical add is bounded partial and case-insensitive deduplicated");
        Guid id = added.Added[0];
        var row = manager.Snapshot.Scripts.Single();
        var collection = await manager.SaveCollectionAsync(null, "Produtividade");
        Check(collection.Succeeded, "collection persisted");
        Guid collectionId = manager.Snapshot.Collections.Single().Id;
        var metadata = new ScriptMetadata("Nome amigável", "Descrição", collectionId, false, false, false, ["Ctrl+Alt+A"]);
        Check((await manager.SaveMetadataAsync(id, metadata)).Succeeded, "metadata saved separately from source");
        Check((await File.ReadAllTextAsync(script)).StartsWith("#Requires", StringComparison.Ordinal), "metadata never edits script bytes");
        Check((await manager.RemoveCollectionAsync(collectionId)).Succeeded && manager.Snapshot.Scripts.Single().CollectionId is null, "collection removal unassigns scripts without deleting them");
        var stale = Select(manager, id);
        var stalePlan = await manager.PrepareAsync(stale, ScriptOperation.Start);
        await manager.SaveCollectionAsync(null, "Sistema");
        Check((await manager.PerformAsync(stalePlan, stale, true)).Code == ReasonCode.StaleSelection && boundary.Starts == 0, "changed state rejects confirmed stale launch plan");
        var selected = Select(manager, id);
        var hiddenPlan = await manager.PrepareAsync(selected, ScriptOperation.Start);
        var hidden = selected with { Query = new(Search: "not visible", Version: selected.Query.Version + 1) };
        Check((await manager.PerformAsync(hiddenPlan, hidden, true)).Code == ReasonCode.StaleSelection && boundary.Starts == 0, "changed query rejects hidden target even during confirmation");
        fixtureOwner.Held = false;
        Check(!(await manager.PrepareAsync(Select(manager, id), ScriptOperation.Start)).Allowed, "owner conflict blocks effects before dispatch");
        fixtureOwner.Held = true;
        boundary.SetRunning(script, interpreter);
        await manager.RefreshAsync();
        Check(!(await manager.PrepareAsync(Select(manager, id), ScriptOperation.Start)).Allowed && boundary.Starts == 0, "external proved instance prevents duplicate start");
        var pause = await manager.PrepareAsync(Select(manager, id), ScriptOperation.TogglePause);
        Check(pause.Allowed && boundary.Controls == 0, "preparing explicit pause toggle does not itself dispatch");
        boundary.NextControl = new(Outcome.Unknown, ReasonCode.StateNotObservable);
        var toggle = await manager.PrepareAsync(Select(manager, id), ScriptOperation.ToggleSuspend);
        Check(toggle.Allowed, "proved suspend state permits distinct suspend request");
        Check((await manager.PerformAsync(toggle, toggle.Selection, true)).Outcome == Outcome.Unknown, "accepted toggle is not confirmed state");
        Check((await manager.PrepareAsync(Select(manager, id), ScriptOperation.Stop)).Allowed, "unknown toggle does not poison proved graceful stop eligibility");
        var restart = await manager.PrepareAsync(Select(manager, id), ScriptOperation.Restart);
        boundary.NextControl = new(Outcome.Unknown, ReasonCode.ExitNotObserved);
        Check((await manager.PerformAsync(restart, restart.Selection, true)).Outcome == Outcome.Unknown && boundary.Starts == 0, "restart never launches replacement when old exit is unconfirmed");
        boundary.Current = new([], true, []);
        Check((await manager.RecoverPendingAsync()).Succeeded, "explicit recovery resolves proved absent targets without replay");
        boundary.UnknownStart = true;
        var start = await manager.PrepareAsync(Select(manager, id), ScriptOperation.Start);
        Check((await manager.PerformAsync(start, start.Selection, true)).Outcome == Outcome.Unknown && boundary.Starts == 1, "ambiguous launch remains unknown and journaled");
        Check(manager.Snapshot.Scripts.Single().Status == ScriptStatus.Unknown, "pending launch is visibly unknown");
        Check(!(await manager.PrepareAsync(Select(manager, id), ScriptOperation.Start)).Allowed && boundary.Starts == 1, "unknown launch cannot be blindly retried");
        var edit = await manager.PrepareAsync(Select(manager, id), ScriptOperation.Edit);
        Check(edit.Allowed, "unknown row remains inspectable with safe edit preparation");
        Check((await manager.RecoverPendingAsync()).Outcome == Outcome.Unknown && boundary.Starts == 1, "path-only observation cannot adopt ambiguous launch as proven ownership");
        boundary.Current = new([], true, []);
        Check((await manager.RecoverPendingAsync()).Succeeded, "explicit no-target recovery clears duplicate risk");
        var consent = await manager.SaveMetadataAsync(id, metadata with { CollectionId = null, RunAtLogin = true });
        Check(consent.Code == ReasonCode.StartupConsentRequired && registration.Writes == 0, "script startup preference never silently enables manager registration");
        Check((await manager.SaveMetadataAsync(id, metadata with { CollectionId = null, RunAtLogin = true }, true)).Succeeded && registration.Writes == 1, "explicit script startup consent registers one fake owned entry");
        Check(registration.Entry == startup.Command && registration.Entry.Contains("--login --background", StringComparison.Ordinal), "owned login command quoted and background-specific");
        boundary.UnknownStart = false;
        var login = await manager.RunLoginAsync();
        int launched = boundary.Starts;
        Check(login.Succeeded && launched == 2, "login executes marked script using fake process boundary once");
        Check((await manager.RunLoginAsync()).Code == ReasonCode.StartupAlreadyProcessed && boundary.Starts == launched, "durable logon key suppresses duplicate activation");
        Check((await manager.RemoveAsync(id)).Succeeded && File.Exists(script) && boundary.Current.Processes.Length == 1, "catalog removal preserves script file and running process");
        await manager.DisposeAsync();
        Check(boundary.Current.Processes.Length == 1, "manager disposal never kills scripts");
        var conflict = new FakeStartup { Entry = "another application's entry" };
        Check((await new UserStartupService(conflict, Path.Combine(root, "AHK Manager.exe")).SetEnabledAsync(false, false)).Code == ReasonCode.StartupEntryConflict && conflict.Writes == 0, "foreign startup entry never overwritten or removed");
        var activity = new ActivityStore(Path.Combine(root, "activity-test"), 1024, 2);
        for (int i = 0; i < 40; i++) await activity.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, id, ScriptOperation.Start, Outcome.Failed, ReasonCode.DispatchFailed, new string('x', 200)));
        var events = await activity.ReadAsync(new(ScriptId: id, Outcome: Outcome.Failed));
        Check(events.Length > 0 && Directory.EnumerateFiles(Path.Combine(root, "activity-test"), "activity.jsonl*").Count() <= 3, "activity is readable and size-rotated with bounded retention");
        Check(ScriptPaths.SplitCommand(ScriptPaths.QuoteArgument(interpreter) + " " + ScriptPaths.QuoteArgument(script)).SequenceEqual(new[] { interpreter, script }), "quoted Unicode argument roundtrip is literal");
        var wrong = AutoHotkeyDiscovery.Validate(interpreter);
        Check(wrong.Version.StartsWith('2'), "interpreter product metadata establishes actual v2");
        Console.WriteLine("FIXTURE " + root);
    }
    static async Task PreserveInactiveStartupAsync(string interpreter)
    {
        string root = Path.Combine(FixtureRoot, "inactive-startup"), path = Path.Combine(root, "fixture.ahk"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(root); Directory.CreateDirectory(data); await File.WriteAllTextAsync(path, "#Requires AutoHotkey v2\n");
        Guid id = Guid.NewGuid(); var store = new SettingsStore(Path.Combine(data, "settings.json")); var original = await store.LoadAsync();
        Check((await store.SaveAsync(original.Document with { Scripts = [new() { Id = id, Path = path, FriendlyName = "Fixture", RunAtLogin = true }], Settings = new() { InterpreterPath = interpreter, LaunchManagerAtLogin = false } })).Succeeded, "seed existing true row startup while global startup inactive");
        var owner = new FakeOwner(root); var processes = new FakeProcesses(owner); var registration = new FakeStartup();
        await using var manager = new ManagerService(data, owner, processes, new UserStartupService(registration, Path.Combine(root, "Manager.exe")), Path.Combine(root, "legacy-absent")); await manager.LoadAsync();
        Check((await manager.SaveMetadataAsync(id, new("Fixture", "", null, true, true, false, []))).Succeeded, "changing administrator preference preserves existing inactive startup intent");
        Check(manager.Snapshot.Scripts.Single().RunAsAdministrator && manager.Snapshot.Scripts.Single().RunAtLogin == true && !manager.Snapshot.Settings.LaunchManagerAtLogin && registration.Writes == 0 && processes.Starts == 0, "administrator checkbox neither registers startup nor launches or elevates");
    }
    static async Task HeaderDescriptionsAsync(string interpreter)
    {
        string root = Path.Combine(FixtureRoot, "header-manager"), script = Path.Combine(root, "header.ahk"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(script, "; @description original header\n#Requires AutoHotkey v2\n^j::return");
        var owner = new FakeOwner(root); var processes = new FakeProcesses(owner); var registration = new FakeStartup();
        var startup = new UserStartupService(registration, Path.Combine(root, "Manager.exe")); string data = Path.Combine(root, "data");
        await using (var service = new ManagerService(data, owner, processes, startup, Path.Combine(root, "legacy-absent")))
        {
            await service.LoadAsync(); var added = await service.AddFilesAsync([script]); Guid id = added.Added.Single();
            Check(service.Snapshot.Scripts.Single().HeaderDescription == "original header", "Add obtains root header without effects");
            Check((await service.SaveMetadataAsync(id, new("Header fixture", "manual fallback", null, false, false, false, []))).Succeeded, "manual description saved separately");
            await File.WriteAllBytesAsync(Path.Combine(root, "bad.ahk"), [0xc3]);
            await File.WriteAllTextAsync(script, "; @description changed\n; @description second line\n#Include bad.ahk\n^j::return"); await service.RefreshAsync();
            Check(service.Snapshot.Scripts.Single().HeaderDescription == "changed\nsecond line" && service.Snapshot.Scripts.Single().Description == "manual fallback", "F5 reanalysis changes derived text while preserving manual");
            await File.WriteAllTextAsync(script, "; @description a\0b\n#Requires AutoHotkey v2"); await service.RefreshAsync(); var invalid = service.Snapshot.Scripts.Single();
            Check(invalid.HeaderDescription is null && invalid.Description == "manual fallback" && invalid.Issues.Any(i => i.Code == ReasonCode.PartialAnalysis && i.Detail is { Length: <= 128 } detail && !detail.Contains('\0')), "NUL root tag yields bounded safe issue and intact fallback");
            await File.WriteAllTextAsync(script, "#Requires AutoHotkey v2\n^j::return"); await service.RefreshAsync();
            Check(service.Snapshot.Scripts.Single().HeaderDescription is null && service.Snapshot.Scripts.Single().Description == "manual fallback", "removed tags immediately restore fallback on refresh");
            string persisted = await File.ReadAllTextAsync(Path.Combine(data, "settings.json")); Check(!persisted.Contains("HeaderDescription", StringComparison.OrdinalIgnoreCase) && !persisted.Contains("original header"), "derived header never enters persisted settings");
        }
        await File.WriteAllTextAsync(script, "; @description reloaded header\n#Requires AutoHotkey v2");
        await using (var reloaded = new ManagerService(data, owner, processes, startup, Path.Combine(root, "legacy-absent")))
        {
            await reloaded.LoadAsync(); Check(reloaded.Snapshot.Scripts.Single().HeaderDescription == "reloaded header" && reloaded.Snapshot.Scripts.Single().Description == "manual fallback", "Load derives fresh header and reloads manual independently");
            File.Delete(script); await reloaded.RefreshAsync(); Check(reloaded.Snapshot.Scripts.Single().HeaderDescription is null, "missing file clears stale derived header");
        }
        Check(processes.Starts == 0 && processes.Controls == 0 && registration.Writes == 0, "header refresh/save/load never starts AHK or writes real/fake startup");
    }
    static SelectionContext Select(ManagerService manager, Guid id) => new(id, new(Version: 7), manager.Snapshot.Revision);

    static async Task FileActionsAsync(string interpreter)
    {
        string root = Path.Combine(FixtureRoot, "file-actions"); Directory.CreateDirectory(root);
        async Task<(ManagerService Manager, FakeProcesses Processes, FakeStartup Startup, string Path)> Create(string name)
        {
            string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "inert.ahk"); await File.WriteAllTextAsync(path, "#Requires AutoHotkey v2.0\nF13::return\n");
            var owner = new FakeOwner(folder); var processes = new FakeProcesses(owner); var startup = new FakeStartup();
            var manager = new ManagerService(Path.Combine(folder, "data"), owner, processes, new UserStartupService(startup, Path.Combine(folder, "Manager.exe")));
            await manager.LoadAsync(); await manager.AddFilesAsync([path]);
            return (manager, processes, startup, path);
        }
        var locate = await Create("locate");
        await using (var manager = locate.Manager)
        {
            var id = manager.Snapshot.Scripts.Single().Id;
            var metadata = new ScriptMetadata("Preserved", "description", null, true, true, true, ["Ctrl+F13"]);
            Check((await manager.SaveMetadataAsync(id, metadata, true)).Succeeded, "file fixture metadata/startup saved through fake boundary");
            int startupWrites = locate.Startup.Writes;
            string replacement = Path.Combine(Path.GetDirectoryName(locate.Path)!, "renamed.ahk"); await File.WriteAllTextAsync(replacement, "F13::return");
            File.Delete(locate.Path); await manager.RefreshAsync();
            Check(manager.Snapshot.Scripts.Single().Status == ScriptStatus.Missing, "missing row stays selectable");
            Check((await manager.RelinkAsync(Select(manager, id), locate.Path, replacement, false)).Code == ReasonCode.StartupConsentRequired, "relink startup intent requires explicit consent");
            var result = await manager.RelinkAsync(Select(manager, id), locate.Path, replacement, true);
            var row = manager.Snapshot.Scripts.Single();
            Check(result.Succeeded && row.Id == id && row.Path == replacement && row.FriendlyName == metadata.FriendlyName && row.Description == metadata.Description && row.RunAtLogin == true && row.RunAsAdministrator && row.LaunchBlocked && row.Hotkeys.Any(h => h.Origin == HotkeyOrigin.Manual && h.Chord == "Ctrl+F13"), "relink changes only sameID path and preserves metadata/manual/login/admin/block preferences");
            Check(locate.Startup.Writes == startupWrites && locate.Processes.Starts == 0 && locate.Processes.Controls == 0, "relink consent performs zero startup/process effects");
            File.Delete(replacement); await manager.RefreshAsync();
            var missingSelection = Select(manager, id);
            Check((await manager.RelinkAsync(missingSelection, replacement, replacement + ".missing.ahk", true)).Code == ReasonCode.InvalidPath && manager.Snapshot.Scripts.Single().Path == replacement, "disappeared picker target preserves registration");
            string duplicate = Path.Combine(Path.GetDirectoryName(locate.Path)!, "duplicate.ahk"); await File.WriteAllTextAsync(duplicate, "F24::return"); await manager.AddFilesAsync([duplicate]);
            Check((await manager.RelinkAsync(Select(manager, id), replacement, duplicate.ToUpperInvariant(), true)).Code == ReasonCode.InvalidPath, "relink duplicate canonical path rejected case-insensitively");
            Check((await manager.RelinkAsync(missingSelection, replacement, duplicate, true)).Code == ReasonCode.StaleSelection, "relink stale revision rejected");
            await File.WriteAllTextAsync(replacement, "F13::return");
            Check((await manager.RelinkAsync(Select(manager, id), replacement, duplicate, true)).Code == ReasonCode.StaleSelection && manager.Snapshot.Scripts.Single(s => s.Id == id).Status != ScriptStatus.Missing, "old path reappearing rejects recovery and refreshes missing status");
        }
        var targetedFailures = new List<string>();
        foreach (string scenario in new[] { "cancel", "stopped", "running-failure", "veto", "changed-process", "changed-file", "incomplete", "unknown", "late-receipt", "replacement", "catalog-failure", "post-stop-cancel", "journal-stop-write", "journal-receipt-write" })
        {
            var fixture = await Create(scenario); await using var manager = fixture.Manager;
            var id = manager.Snapshot.Scripts.Single().Id;
            if (scenario is "running-failure" or "veto" or "changed-process" or "post-stop-cancel" or "journal-stop-write") { fixture.Processes.SetRunning(fixture.Path, interpreter); await manager.RefreshAsync(); }
            var plan = await manager.PrepareRecycleAsync(Select(manager, id)); Check(plan.Allowed, "prepare recycle without effect " + scenario);
            Check(fixture.Processes.Controls == 0 && File.Exists(fixture.Path), "prepare never stops or deletes " + scenario);
            if (scenario == "cancel")
            {
                var cancelled = await manager.AuthorizeRecycleAsync(plan.OperationId, plan.Selection, false);
                Check(cancelled.Result.Outcome == Outcome.CancelledBeforeDispatch && File.Exists(fixture.Path) && manager.Snapshot.Scripts.Length == 1, "defaultCancel keeps file and catalog"); continue;
            }
            if (scenario == "veto") fixture.Processes.NextControl = new(Outcome.Unknown, ReasonCode.ExitNotObserved);
            if (scenario == "changed-process") fixture.Processes.Current = fixture.Processes.Current with { Processes = [fixture.Processes.Current.Processes[0] with { Identity = fixture.Processes.Current.Processes[0].Identity with { CreationFileTime = plan.ExpectedIdentity!.CreationFileTime + 1 } }] };
            if (scenario == "changed-file") await File.AppendAllTextAsync(fixture.Path, "; changed");
            if (scenario == "incomplete") fixture.Processes.Current = fixture.Processes.Current with { Complete = false };
            using var cancellationAfterStop = new CancellationTokenSource(); FileStream? journalWriteLock = null;
            if (scenario == "post-stop-cancel") fixture.Processes.AfterControl = () => cancellationAfterStop.Cancel();
            if (scenario == "journal-stop-write") fixture.Processes.AfterControl = () => journalWriteLock = new FileStream(Path.Combine(manager.DataRoot, "operations.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            RecycleAuthorization authorized;
            try { authorized = await manager.AuthorizeRecycleAsync(plan.OperationId, plan.Selection, true, cancellationAfterStop.Token); }
            finally { journalWriteLock?.Dispose(); }
            if (scenario is "post-stop-cancel" or "journal-stop-write")
            {
                Check(authorized.File is null && File.Exists(fixture.Path) && manager.Snapshot.Scripts.Length == 1 && fixture.Processes.Current.Processes.IsEmpty && fixture.Processes.Starts == 0, "instrumented fake confirmsStop before cancellation/journal-write refusal; file/catalog preserved " + scenario);
                bool truthful = authorized.Stopped && authorized.Result.Outcome is Outcome.Partial or Outcome.Unknown && authorized.Result.Detail?.Contains("encerrada", StringComparison.Ordinal) == true;
                Console.WriteLine((truthful ? "PASS " : "FAIL ") + "confirmedStop remains reported after " + scenario);
                if (!truthful) targetedFailures.Add(scenario);
                continue;
            }
            if (scenario is "veto" or "changed-process" or "changed-file" or "incomplete")
            {
                Check(authorized.File is null && File.Exists(fixture.Path) && manager.Snapshot.Scripts.Length == 1, "unsafe identity/observation/veto denies native dispatch " + scenario);
                if (scenario == "changed-process") Check(fixture.Processes.Controls == 0, "replacement process never stopped");
                continue;
            }
            Check(authorized.Result.Outcome == Outcome.Pending && authorized.File is not null && File.Exists(fixture.Path), "durable authorization precedes injected native dispatch " + scenario);
            Check((await manager.AuthorizeRecycleAsync(plan.OperationId, plan.Selection, true)).File is null, "recycle authorization is single-use " + scenario);
            if (scenario == "running-failure")
            {
                Check(authorized.Stopped && fixture.Processes.Controls == 1 && fixture.Processes.Current.Processes.IsEmpty, "exact verified gracefulStop completed before native permission failure");
                var failure = await manager.CompleteRecycleAsync(plan.OperationId, Outcome.Failed, "permission denied");
                Check(failure.Outcome == Outcome.Partial && failure.Detail!.Contains("encerrada") && File.Exists(fixture.Path) && manager.Snapshot.Scripts.Length == 1 && fixture.Processes.Starts == 0, "UAC denial after stop preserves file/catalog and stopped state without restart");
            }
            else if (scenario == "unknown")
            {
                await manager.CompleteRecycleAsync(plan.OperationId, Outcome.Unknown, "deadline");
                Check((await manager.RecoverPendingAsync()).Outcome == Outcome.Unknown, "recycleUnknown never resolves from process absence");
                Check((await manager.RemoveAsync(id)).Succeeded && File.Exists(fixture.Path), "safe list removal retains file and journal uncertainty");
                var readded = await manager.AddFilesAsync([fixture.Path]); var newId = readded.Added.Single();
                Check(newId != id && (await manager.PrepareAsync(Select(manager, newId), ScriptOperation.Start)).Reason == ReasonCode.PendingOperation && (await manager.PrepareRecycleAsync(Select(manager, newId))).Reason == ReasonCode.PendingOperation, "pending canonical path survives Remove/readd newID and blocks start/recycle");
                File.Delete(fixture.Path); string newPath = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "replacement.ahk"); await File.WriteAllTextAsync(newPath, "F13::return"); await manager.RefreshAsync();
                Check((await manager.RelinkAsync(Select(manager, newId), fixture.Path, newPath, true)).Code == ReasonCode.PendingOperation && (await manager.RecoverPendingAsync()).Outcome == Outcome.Unknown, "pending canonical path also blocks relink and absence recovery");
                Check(fixture.Processes.Starts == 0 && fixture.Processes.Controls == 0, "unknown recovery/readd performs zero process effects");
                await manager.DisposeAsync();
                var recoveredOwner = new FakeOwner(Path.GetDirectoryName(fixture.Path)!); var recoveredProcesses = new FakeProcesses(recoveredOwner);
                await using var recovered = new ManagerService(manager.DataRoot, recoveredOwner, recoveredProcesses, new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe")));
                await recovered.LoadAsync();
                Check((await recovered.RecoverPendingAsync()).Outcome == Outcome.Unknown && (await recovered.PrepareAsync(Select(recovered, newId), ScriptOperation.Start)).Reason == ReasonCode.PendingOperation && recoveredProcesses.Starts == 0 && recoveredProcesses.Controls == 0, "restart retains canonical recycle uncertainty without absence inference or replay");
            }
            else if (scenario == "journal-receipt-write")
            {
                File.Delete(fixture.Path); OperationResult completed;
                using (var locked = new FileStream(Path.Combine(manager.DataRoot, "operations.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) completed = await manager.CompleteRecycleAsync(plan.OperationId, Outcome.Confirmed, null);
                Check(completed.Outcome == Outcome.Unknown && manager.Snapshot.Scripts.Length == 1 && !File.Exists(fixture.Path), "receipt journal-write failure preserves catalog and durable uncertainty after injected native success");
            }
            else
            {
                if (scenario == "late-receipt") await manager.CompleteRecycleAsync(plan.OperationId, Outcome.Unknown, "deadline");
                File.Delete(fixture.Path); // Injected affirmative native effect: owned fixture only.
                if (scenario == "replacement") await File.WriteAllTextAsync(fixture.Path, "F24::return ; replacement must survive");
                if (scenario == "catalog-failure") await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(fixture.Path)!, "data", "settings.json"), " ");
                var completed = await manager.CompleteRecycleAsync(plan.OperationId, Outcome.Confirmed, null);
                Check(scenario is "catalog-failure" or "replacement" ? completed.Outcome == Outcome.Partial && manager.Snapshot.Scripts.Length == 1 : completed.Succeeded && manager.Snapshot.Scripts.IsEmpty, "affirmative receipt controls catalog persistence " + scenario);
                if (scenario == "replacement") Check((await File.ReadAllTextAsync(fixture.Path)).Contains("replacement must survive"), "replacement file after native receipt is preserved");
                var again = await manager.CompleteRecycleAsync(plan.OperationId, Outcome.Confirmed, null);
                Check(again.Outcome == completed.Outcome, "sameOperation completion idempotent " + scenario);
            }
            Check(fixture.Processes.Starts == 0 && fixture.Startup.Writes == 0, "file action has zero launch/startup writes " + scenario);
        }
        Check(targetedFailures.Count == 0, "targeted postStop reporting regressions: " + string.Join(", ", targetedFailures));
    }

    sealed class FakeOwner(string root) : IEffectOwner
    {
        public bool Held { get; set; } = true;
        public bool Trusted => true;
        public string Sid => "S-1-5-21-test-only";
        public string LoginKey => Sid + "|logon1";
        public bool Allows(string script, string interpreter) => Held && script.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        public void Dispose() { }
    }
    sealed class FakeStartup : IUserStartupAdapter
    {
        public string? Entry; public int Writes;
        public string? ReadEntry() => Entry;
        public bool CompareExchangeEntry(string? expected, string? replacement) { if (Entry != expected) return false; Entry = replacement; Writes++; return true; }
        public Task<StartupInspection> InspectAsync(CancellationToken ct) => Task.FromResult(new StartupInspection(true, [], []));
    }
    sealed class FakeProcesses(FakeOwner owner) : IProcessManager
    {
        public int Starts, Controls; public bool UnknownStart, UnknownWithIdentity, Administrator, LastElevated;
        public bool IsAdministrator => Administrator;
        public OperationResult NextControl = new(Outcome.Confirmed);
        // Test-only deterministic fault injection after the fake has confirmed a Stop.
        public Action? AfterControl;
        public ObservedProcesses Current = new([], true, []);
        public event EventHandler? Invalidated { add { } remove { } }
        public bool Supports(ScriptOperation operation) => true;
        public Task<ObservedProcesses> ObserveAsync(IReadOnlyCollection<string> known, CancellationToken ct = default) => Task.FromResult(Current);
        public void SetRunning(string script, string interpreter)
        {
            var identity = new ProcessIdentity(7100 + Starts, DateTime.UtcNow.ToFileTimeUtc(), interpreter, script, owner.Sid, 1, ScriptPaths.Fingerprint(script), ScriptPaths.Fingerprint(interpreter));
            Current = new([new(identity, ExecutionOrigin.External, "2.0", DateTimeOffset.UtcNow, Suspended: false, WindowVerified: true)], true, []);
        }
        public Task<LaunchResult> StartAsync(string interpreter, string script, bool elevated, CancellationToken ct = default)
        {
            Starts++; LastElevated = elevated; SetRunning(script, interpreter);
            return Task.FromResult(new LaunchResult(new(UnknownStart ? Outcome.Unknown : Outcome.Confirmed, UnknownStart ? ReasonCode.LaunchUncertain : ReasonCode.None), UnknownStart && !UnknownWithIdentity ? null : Current.Processes.Single()));
        }
        public Task<OperationResult> ControlAsync(ProcessIdentity expected, ScriptOperation operation, CancellationToken ct = default)
        {
            Controls++;
            if (!Current.Processes.Any(p => NativeProcessIdentity.Same(p.Identity, expected))) return Task.FromResult(new OperationResult(Outcome.Failed, ReasonCode.IdentityChanged));
            if (NextControl.Succeeded && operation == ScriptOperation.Stop) { Current = new([], true, []); AfterControl?.Invoke(); }
            return Task.FromResult(NextControl);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
