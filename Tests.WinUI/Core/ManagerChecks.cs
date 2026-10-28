using System.Collections.Immutable;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.WinUI.Tests.Core;

public static partial class CoreSuite
{
    static string InterpreterFixture = "";
    public static async Task RunPassiveRecoveryChecksAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        string interpreter = (await new AutoHotkeyDiscovery().DiscoverAsync())[0].Path;
        string script = Path.Combine(root, "external.ahk");
        await File.WriteAllTextAsync(script, "#Requires AutoHotkey v2.0\n");
        string data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
        var record = new ScriptRecord { Path = script, FriendlyName = "External script", RunAsAdministrator = true, LegacyGuard = true, InterpreterPath = interpreter };
        await File.WriteAllBytesAsync(Path.Combine(data, "settings.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new SettingsDocument { Scripts = [record] }, ManagerJson.Default.SettingsDocument));
        var owner = new FakeOwner(root); var processes = new FakeProcesses(owner);
        processes.SetRunning(script, interpreter);
        await using var manager = new ManagerService(data, owner, processes, new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe")));
        await manager.LoadAsync();
        check((await manager.RecoverPendingAsync()).Succeeded, "external legacy protection is not an unresolved process request");
        check(manager.Snapshot.Scripts.Single().Status == ScriptStatus.Running && manager.Snapshot.Scripts.Single().Issues.Any(i => i.Code == ReasonCode.LegacyGuard), "external script remains observed and protected");
        check(processes.Starts == 0 && processes.Controls == 0, "legacy inspection never dispatches effects");

        string orphanData = Path.Combine(root, "orphan"); Directory.CreateDirectory(orphanData);
        var journal = new OperationJournal(Path.Combine(orphanData, "operations.json")); await journal.LoadAsync();
        await journal.PutAsync(new(Guid.NewGuid(), Guid.NewGuid(), ScriptOperation.Stop, Path.Combine(root, "gone.ahk"), "", "", "", owner.Sid, owner.LoginKey, DateTimeOffset.UtcNow, Outcome.Unknown, Code: ReasonCode.ExitNotObserved));
        var orphanOwner = new FakeOwner(root); var orphanProcesses = new FakeProcesses(orphanOwner) { Current = new([], false, []) };
        await using var orphan = new ManagerService(orphanData, orphanOwner, orphanProcesses, new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe")));
        await orphan.LoadAsync(); await orphan.RefreshAsync();
        check(orphan.Snapshot.Issues.Any(i => i.Code == ReasonCode.PendingOperation), "normal refresh retains a genuinely unobservable result");
        orphanProcesses.Current = new([], true, []); await orphan.RefreshAsync();
        check(!orphan.Snapshot.Issues.Any(i => i.Code == ReasonCode.PendingOperation), "normal refresh settles proven absence without a dedicated recovery action");
        check(orphanProcesses.Starts == 0 && orphanProcesses.Controls == 0, "automatic reconciliation never replays commands");

        foreach (var scenario in new[] { "expired-safe", "live-commit", "expired-entering", "expired-uncertain", "incomplete" })
        {
            string continuationData = Path.Combine(root, scenario); Directory.CreateDirectory(continuationData);
            var continuationOwner = new FakeOwner(root); var continuationProcesses = new FakeProcesses(continuationOwner) { Current = new([], scenario != "incomplete", []) };
            var item = new StartupContinuationItem(Guid.NewGuid(), "Normal script", script, interpreter, new string('0', 64), new string('0', 64));
            var ticket = new StartupContinuationTicket(1, Guid.NewGuid(), continuationData, new string('0', 64), continuationOwner.Sid, 1, continuationOwner.LoginKey, DateTimeOffset.UtcNow.AddMinutes(scenario == "live-commit" ? 5 : -1), [item]);
            var state = scenario == "expired-entering" ? StartupContinuationItemState.EnteringExecution : scenario == "expired-uncertain" ? StartupContinuationItemState.Uncertain : StartupContinuationItemState.NotDispatched;
            var continuationStore = new StartupContinuationStore(continuationData);
            await continuationStore.WriteAsync(new(ticket, StartupContinuationStore.Hash(ticket), StartupContinuationPhase.Committed, [state]), null);
            await using var continuationManager = new ManagerService(continuationData, continuationOwner, continuationProcesses, new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe")));
            await continuationManager.LoadAsync(); await continuationManager.RefreshAsync();
            bool safelyExpired = scenario == "expired-safe";
            check((await continuationStore.ReadAsync(ticket.OperationId))!.Phase == (safelyExpired ? StartupContinuationPhase.Recovered : StartupContinuationPhase.Committed)
                && continuationManager.Snapshot.EffectsAvailable == safelyExpired,
                "automatic continuation expiry respects proof and uncertainty: " + scenario);
            check(continuationProcesses.Starts == 0 && continuationProcesses.Controls == 0, "continuation maintenance never dispatches: " + scenario);
        }
    }
    public static async Task RunOrphanPendingChecksAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var owner = new FakeOwner(root);
        var processes = new FakeProcesses(owner) { Current = new([], false, []) };
        string data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
        var journal = new OperationJournal(Path.Combine(data, "operations.json"));
        await journal.LoadAsync();
        var record = new OperationRecord(Guid.NewGuid(), Guid.NewGuid(), ScriptOperation.Stop,
            Path.Combine(root, "removed-test.ahk"), "", "", "", owner.Sid, owner.LoginKey,
            DateTimeOffset.UtcNow, Outcome.Unknown, Code: ReasonCode.ExitNotObserved);
        check((await journal.PutAsync(record)).Succeeded, "orphan fixture preserves unresolved stop");
        await using var manager = new ManagerService(data, owner, processes,
            new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe")), Path.Combine(root, "legacy-absent"));
        await manager.LoadAsync();
        check(manager.Snapshot.Scripts.IsEmpty && manager.Snapshot.Issues.Any(i => i.Code == ReasonCode.PendingOperation),
            "orphan pending stop exposes global recovery without catalog row");
        check((await manager.RecoverPendingAsync()).Outcome == Outcome.Unknown
            && manager.Snapshot.Issues.Any(i => i.Code == ReasonCode.PendingOperation),
            "incomplete observation keeps orphan recovery visible and unresolved");
        processes.Current = new([], true, []);
        check((await manager.RecoverPendingAsync()).Succeeded
            && !manager.Snapshot.Issues.Any(i => i.Code == ReasonCode.PendingOperation),
            "proved absence clears orphan notice through explicit recovery");
        check(processes.Starts == 0 && processes.Controls == 0, "orphan recovery never repeats process operations");
    }
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
        await RunPassiveRecoveryChecksAsync(Path.Combine(FixtureRoot, "passive-recovery"), Check);
        await RunOrphanPendingChecksAsync(Path.Combine(FixtureRoot, "orphan-pending"), Check);
        await LoginDiagnosticsAsync();
        await LoginStartupAsync(interpreter);
        await LoginContinuationAsync(interpreter);
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
        Check(manager.Snapshot.Settings.LaunchManagerAtLogin && manager.Snapshot.Settings.LaunchManagerAsAdministrator && registration.Writes == 0, "loading exposes requested defaults without performing startup registration");
        await UseNormalStartupFixtureAsync(manager);
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
    static async Task LoginDiagnosticsAsync()
    {
        var failures = new List<string>();
        void Verify(bool value, string name) { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures.Add(name); }
        foreach (string scenario in new[] { "registration", "authority", "inspection", "reservation", "cancel-inspection", "cancel-after-inspection" })
        {
            string root = Path.Combine(FixtureRoot, "login-diagnostic-" + scenario); Directory.CreateDirectory(root);
            string path = Path.Combine(root, "fixture.ahk"); await File.WriteAllTextAsync(path, "#Requires AutoHotkey v2\n");
            var owner = new FakeOwner(root); var processes = new FakeProcesses(owner); var registration = new FakeStartup();
            var startup = new UserStartupService(registration, Path.Combine(root, "Manager.exe"));
            using var cancellation = new CancellationTokenSource();
            string dataRoot = Path.Combine(root, "data");
            await using var service = new ManagerService(dataRoot, owner, processes, startup, Path.Combine(root, "legacy-absent"));
            await service.LoadAsync(); await UseNormalStartupFixtureAsync(service); var added = await service.AddFilesAsync([path]);
            await service.SaveMetadataAsync(added.Added.Single(), new("Fixture", "", null, true, false, false, []), true);
            if (scenario == "registration") registration.Entry = null;
            if (scenario == "authority") owner.Held = false;
            if (scenario == "inspection") registration.Inspection = new(false, [], [new(ReasonCode.ExternalStartupConflict, "Tarefas agendadas: consulta indisponível. " + new string('x', 5000))]);
            if (scenario == "cancel-inspection") registration.CancelInspection = true;
            if (scenario == "cancel-after-inspection") registration.AfterInspection = cancellation.Cancel;
            if (scenario == "reservation")
            {
                var external = new SettingsStore(Path.Combine(dataRoot, "settings.json")); var loaded = await external.LoadAsync();
                await external.SaveAsync(loaded.Document with { Settings = loaded.Document.Settings with { ShowWindowAtLogin = true } });
            }
            var result = await service.RunLoginAsync(cancellation.Token);
            var records = (await service.GetActivityAsync(new())).Where(a => a.ScriptId is null && a.Operation == ScriptOperation.Start).ToArray();
            Verify(!result.Succeeded && processes.Starts == 0 && records.Length == 1 && records[0].Code == result.Code && records[0].Detail == result.Detail && result.Detail?.Contains("Início com Windows", StringComparison.Ordinal) == true, "login diagnostic " + scenario + " persists one explanatory abort without dispatch");
            if (scenario == "inspection") Verify(result.Detail?.Contains("Tarefas agendadas", StringComparison.Ordinal) == true && result.Detail.Length <= 2400, "login diagnostic inspection carries bounded origin and reason");
            var persisted = await new SettingsStore(Path.Combine(dataRoot, "settings.json")).LoadAsync();
            Verify(persisted.Document.LastLoginKey.Length == 0, "login diagnostic " + scenario + " never reserves a refused attempt");
            await using var reopened = new ManagerService(dataRoot, new FakeOwner(root), processes, startup, Path.Combine(root, "legacy-absent"));
            await reopened.LoadAsync();
            Verify(reopened.Snapshot.Activity.Count(a => a.ScriptId is null && a.Operation == ScriptOperation.Start && a.Detail == result.Detail) == 1, "login diagnostic " + scenario + " survives reopening");
        }
        var unsupported = RegistryStartupAdapter.InspectCommand("AutoHotkey.exe %PRIVATE_SCRIPT%");
        Verify(!unsupported.Complete && unsupported.Issues.Length == 1 && unsupported.Issues[0].Detail?.Contains("PRIVATE_SCRIPT", StringComparison.Ordinal) == false, "startup inspection explains unresolved command without recording its contents");
        Check(failures.Count == 0, "login diagnostic regressions: " + string.Join("; ", failures));
    }
    static async Task LoginContinuationAsync(string interpreter)
    {
        var failures = new List<string>();
        void Verify(bool value, string name) { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures.Add(name); }
        foreach (string scenario in new[] { "two-normal", "failed-first", "uncertain-first", "partial-before-offer", "changed-bytes", "changed-profile", "missing-normal-token", "replay", "manual-completed", "crash-prepared", "crash-accepted", "crash-entering" })
        {
            string root = Path.Combine(FixtureRoot, "continuation-" + scenario); Directory.CreateDirectory(root);
            string first = Path.Combine(root, "first.ahk"), second = Path.Combine(root, "second.ahk"), admin = Path.Combine(root, "admin.ahk");
            foreach (var path in new[] { first, second, admin }) await File.WriteAllTextAsync(path, "#Requires AutoHotkey v2\n");
            var owner = new FakeOwner(root); var boundary = new FakeProcesses(owner); var registration = new FakeStartup();
            var startup = new UserStartupService(registration, Path.Combine(root, "Manager.exe")); string data = Path.Combine(root, "data");
            await using var service = new ManagerService(data, owner, boundary, startup, Path.Combine(root, "legacy-absent"));
            await service.LoadAsync(); await UseNormalStartupFixtureAsync(service); await service.AddFilesAsync([first, second, admin]);
            foreach (var row in service.Snapshot.Scripts) await service.SaveMetadataAsync(row.Id, new(row.FriendlyName, "", null, true, row.Path == admin, false, []), true);
            if (scenario == "partial-before-offer") boundary.AfterStart = () => boundary.Current = boundary.Current with { Complete = false };
            else boundary.Current = new([], false, [new(ReasonCode.ObservationIncomplete)]);
            var initial = await service.RunLoginAsync();
            var offer = await service.GetStartupContinuationAsync();
            Verify(initial.Outcome == Outcome.Partial && offer?.CanResume == true && offer.ScriptNames.Length == (scenario == "partial-before-offer" ? 1 : 2), "continuation " + scenario + " offers only normal items never sent to ExecuteProcess");
            if (offer is null) continue;
            var ticket = await service.GetPreparedStartupContinuationAsync(offer.OperationId);
            Verify(ticket is not null && ticket.Items.All(i => i.ScriptPath != admin), "continuation " + scenario + " excludes administrative script");
            if (ticket is null) continue;
            int alreadyStarted = boundary.Starts; boundary.AfterStart = null; boundary.Current = new([], true, []);
            if (scenario == "manual-completed")
            {
                await service.RefreshAsync();
                var selection = Select(service, service.Snapshot.Scripts.Single(s => s.Path == first).Id);
                var plan = await service.PrepareAsync(selection, ScriptOperation.Start);
                Verify(plan.Allowed && (await service.PerformAsync(plan, plan.Selection, true)).Succeeded, "continuation declined offer allows a separately authorized manual start");
                boundary.Current = new([], true, []); await service.RefreshAsync();
                if (await service.CommitStartupContinuationAsync(ticket))
                {
                    boundary.Administrator = true;
                    var accepted = await service.AcceptStartupContinuationAsync(ticket);
                    if (accepted is not null) await service.ResumeNormalLoginAsync(accepted, CancellationToken.None);
                }
                Verify(boundary.Launches.Count(launch => launch.Path == first) == 1 && (await service.GetStartupContinuationAsync())?.CanResume == false, "continuation never repeats a manual start that completed after its offer");
                continue;
            }
            if (scenario == "crash-prepared")
            {
                await service.DisposeAsync();
                await using var reopened = new ManagerService(data, new FakeOwner(root), boundary, startup, Path.Combine(root, "legacy-absent")); await reopened.LoadAsync();
                Verify((await reopened.GetStartupContinuationAsync())?.CanResume == false && await reopened.GetPreparedStartupContinuationAsync(ticket.OperationId) is null && !reopened.Snapshot.EffectsAvailable && boundary.Starts == alreadyStarted, "continuation persisted offer never reconstructs dispatch authority after normal-host crash");
                continue;
            }
            Verify(await service.CommitStartupContinuationAsync(ticket), "continuation " + scenario + " durably commits one immutable ticket");
            boundary.Administrator = true;
            if (scenario == "changed-bytes") await File.AppendAllTextAsync(second, "; changed after consent\n");
            if (scenario == "changed-profile") await service.SaveMetadataAsync(service.Snapshot.Scripts.First(s => s.Path == second).Id, new("Changed", "", null, true, true, false, []));
            var accepts = await Task.WhenAll(service.AcceptStartupContinuationAsync(ticket), service.AcceptStartupContinuationAsync(ticket));
            var authority = accepts.SingleOrDefault(a => a is not null);
            if (scenario is "changed-bytes" or "changed-profile")
            {
                Verify(authority is null && boundary.Starts == alreadyStarted, "continuation " + scenario + " refuses changed scope before acceptance"); continue;
            }
            Verify(authority is not null && accepts.Count(a => a is not null) == 1, "continuation " + scenario + " accepts once under concurrent delivery");
            if (authority is null) continue;
            if (scenario is "crash-accepted" or "crash-entering")
            {
                if (scenario == "crash-entering")
                {
                    var store = new StartupContinuationStore(data); var record = (await store.ReadAsync(ticket.OperationId))!;
                    await store.WriteAsync(record with { Items = record.Items.SetItem(0, StartupContinuationItemState.EnteringExecution) }, record);
                }
                await service.DisposeAsync();
                await using var reopened = new ManagerService(data, new FakeOwner(root), boundary, startup, Path.Combine(root, "legacy-absent")); await reopened.LoadAsync();
                var plan = await reopened.PrepareAsync(Select(reopened, reopened.Snapshot.Scripts.First().Id), ScriptOperation.Start);
                Verify(!plan.Allowed && plan.Reason == ReasonCode.PendingOperation && await reopened.GetPreparedStartupContinuationAsync(ticket.OperationId) is null && boundary.Starts == alreadyStarted, "continuation " + scenario + " blocks effects and never rebuilds offer after crash");
                Verify((await reopened.RecoverPendingAsync()).Succeeded && boundary.Starts == alreadyStarted && await reopened.AcceptStartupContinuationAsync(ticket) is null, "continuation " + scenario + " explicit recovery closes evidence without replay");
                continue;
            }
            if (scenario == "failed-first") boundary.NextStart = new(Outcome.Failed, ReasonCode.AccessDenied);
            if (scenario == "uncertain-first") boundary.UnknownStart = true;
            if (scenario == "missing-normal-token") boundary.NormalLaunchDenied = ReasonCode.CapabilityUnverified;
            var results = await Task.WhenAll(service.ResumeNormalLoginAsync(authority, CancellationToken.None), service.ResumeNormalLoginAsync(authority, CancellationToken.None));
            var result = results.Single(r => r.Code != ReasonCode.PendingOperation || r.Outcome != Outcome.Failed);
            int expected = scenario == "missing-normal-token" ? 0 : scenario is "uncertain-first" or "partial-before-offer" ? 1 : 2;
            Verify(boundary.Starts == alreadyStarted + expected && boundary.Launches.Skip(alreadyStarted).All(l => !l.Elevated), "continuation " + scenario + " dispatches eligible normal items once with elevation false");
            Verify(await service.AcceptStartupContinuationAsync(ticket) is null && (await service.GetStartupContinuationAsync())?.CanResume == false, "continuation " + scenario + " refuses replay and lost acknowledgement never authorizes another run");
            if (scenario == "uncertain-first")
            {
                Verify(result.Outcome == Outcome.Partial && !service.Snapshot.EffectsAvailable && service.Snapshot.Activity.Any(a => a.Code == ReasonCode.PendingOperation), "continuation uncertainty blocks later script with durable reason");
                boundary.Current = new([], true, []); await service.RefreshAsync();
                Verify((await service.RecoverPendingAsync()).Succeeded && boundary.Starts == alreadyStarted + 1 && await service.AcceptStartupContinuationAsync(ticket) is null, "completed uncertain continuation recovers explicitly after observation without replay");
            }
            if (scenario == "failed-first") Verify(result.Outcome == Outcome.Partial && boundary.Launches.Last().Path == second, "continuation known first failure allows normal second script");
        }
        string historyRoot = Path.Combine(FixtureRoot, "continuation-history"); var history = new StartupContinuationStore(historyRoot);
        var historyItem = new StartupContinuationItem(Guid.NewGuid(), "History", Path.Combine(historyRoot, "script.ahk"), interpreter, new string('a',64), new string('b',64));
        var preserved = new List<Guid>();
        for (int i = 0; i < 129; i++)
        {
            var past = new StartupContinuationTicket(1, Guid.NewGuid(), historyRoot, new string('c',64), "S-1-fixture", 1, "history-login", DateTimeOffset.UtcNow.AddSeconds(i), [historyItem]);
            preserved.Add(past.OperationId); await history.WriteAsync(new(past, StartupContinuationStore.Hash(past), StartupContinuationPhase.Completed, [StartupContinuationItemState.KnownStarted]), null);
        }
        var current = new StartupContinuationTicket(1, Guid.NewGuid(), historyRoot, new string('c',64), "S-1-fixture", 1, "history-login", DateTimeOffset.UtcNow.AddMinutes(10), [historyItem]);
        await history.WriteAsync(new(current, StartupContinuationStore.Hash(current), StartupContinuationPhase.Prepared, [StartupContinuationItemState.NotDispatched]), null);
        var uncertain = current with { OperationId = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        await history.WriteAsync(new(uncertain, StartupContinuationStore.Hash(uncertain), StartupContinuationPhase.Partial, [StartupContinuationItemState.Uncertain]), null);
        bool found = false, pending = false;
        try { found = (await history.FindAsync("history-login"))?.Ticket.OperationId == current.OperationId; } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        try { pending = (await history.PendingAsync()).Select(record => record.Ticket.OperationId).ToHashSet().SetEquals([current.OperationId, uncertain.OperationId]); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        Verify(found && pending, "continuation 129 terminal records neither block current offer nor hide pending recovery");
        Verify((await Task.WhenAll(preserved.Select(id => history.ReadAsync(id)))).All(record => record?.Phase == StartupContinuationPhase.Completed), "continuation history lookup preserves terminal records and their replay barriers");
        Verify((await history.ReadAsync(uncertain.OperationId))?.Items.Single() == StartupContinuationItemState.Uncertain, "continuation history lookup preserves uncertain evidence for explicit recovery");
        Check(failures.Count == 0, "startup continuation regressions: " + string.Join("; ", failures));
    }
    static async Task LoginStartupAsync(string interpreter)
    {
        var failures = new List<string>();
        void Verify(bool value, string name) { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures.Add(name); }
        foreach (string scenario in new[] { "administrator", "standard", "failed-first", "inventory", "uncertain", "already-running", "cancel-after-first", "incompatible-first", "cancel-during-observe" })
        {
            string root = Path.Combine(FixtureRoot, "login-" + scenario); Directory.CreateDirectory(root);
            string first = Path.Combine(root, "first.ahk"), second = Path.Combine(root, "second.ahk");
            await File.WriteAllTextAsync(first, "#Requires AutoHotkey v2\n"); await File.WriteAllTextAsync(second, "#Requires AutoHotkey v2\n");
            var owner = new FakeOwner(root); var boundary = new FakeProcesses(owner) { Administrator = scenario == "administrator" };
            var registration = new FakeStartup(); var startup = new UserStartupService(registration, Path.Combine(root, "Manager.exe"));
            await using var service = new ManagerService(Path.Combine(root, "data"), owner, boundary, startup, Path.Combine(root, "legacy-absent"));
            await service.LoadAsync(); await UseNormalStartupFixtureAsync(service); await service.AddFilesAsync([first, second]);
            var ids = service.Snapshot.Scripts.Select(s => s.Id).ToArray();
            await service.SaveMetadataAsync(ids[0], new("Primeiro", "", null, true, scenario is "administrator" or "standard", false, []), true);
            await service.SaveMetadataAsync(ids[1], new("Segundo", "", null, true, false, false, []));
            if (scenario == "failed-first") boundary.NextStart = new(Outcome.Failed, ReasonCode.AccessDenied);
            if (scenario == "inventory") boundary.Current = new([], false, [new(ReasonCode.ObservationIncomplete)]);
            if (scenario == "uncertain") boundary.UnknownStart = true;
            if (scenario == "already-running") boundary.SetRunning(first, interpreter);
            using var cancellation = new CancellationTokenSource();
            if (scenario == "cancel-after-first") boundary.AfterStart = cancellation.Cancel;
            if (scenario == "incompatible-first") await File.WriteAllTextAsync(first, "#Requires AutoHotkey v1\n");
            boundary.Observations = 0;
            if (scenario == "cancel-during-observe") boundary.AfterObserve = count => { if (count == 3) cancellation.Cancel(); };
            var result = await service.RunLoginAsync(cancellation.Token);
            if (scenario == "administrator") Verify(result.Succeeded && boundary.Launches.Select(p => p.Elevated).SequenceEqual([true, false]), "login administrator starts administrative and ordinary scripts with original flags");
            else if (scenario is "standard" or "failed-first")
            {
                Verify(result.Outcome == Outcome.Partial && boundary.Launches.Last().Path == second && !boundary.Launches.Last().Elevated, "login " + scenario + " continues eligible ordinary second script");
                Verify(result.Detail?.Contains("Primeiro", StringComparison.Ordinal) == true && result.Detail.Contains(scenario == "standard" ? "administrador" : "acesso", StringComparison.OrdinalIgnoreCase), "login " + scenario + " names first failure and readable reason");
                Verify(service.Snapshot.Activity.Any(a => a.ScriptId == ids[0] && a.Code == (scenario == "standard" ? ReasonCode.ManualElevationRequired : ReasonCode.AccessDenied)), "login " + scenario + " persists per-script reason");
            }
            else if (scenario == "already-running") Verify(result.Succeeded && boundary.Starts == 1 && boundary.Launches.Single().Path == second && result.Detail?.Contains("Já em execução: 1", StringComparison.Ordinal) == true, "login never duplicates already-running first script while starting second");
            else if (scenario == "cancel-after-first") Verify(result.Outcome == Outcome.Partial && boundary.Starts == 1 && result.Detail?.Contains("Segundo", StringComparison.Ordinal) == true && service.Snapshot.Activity.Any(a => a.ScriptId == ids[1] && a.Code == ReasonCode.Cancelled), "login cancellation preserves first effect and records unstarted second without replay");
            else if (scenario == "incompatible-first")
            {
                Verify(result.Outcome == Outcome.Partial && boundary.Starts == 1 && boundary.Launches.Single().Path == second, "login incompatible first never dispatches and later ordinary script starts");
                var records = service.Snapshot.Activity.Where(a => a.ScriptId == ids[0] && a.Operation == ScriptOperation.Start && a.Code == ReasonCode.UnsupportedInterpreter).ToArray();
                Verify(records.Length == 1 && records[0].Detail?.Contains("Primeiro", StringComparison.Ordinal) == true, "login incompatible preflight failure is persisted once with script name");
            }
            else if (scenario == "cancel-during-observe") Verify(result.Outcome == Outcome.Partial && boundary.Starts == 1 && result.Detail?.Contains("Segundo", StringComparison.Ordinal) == true && service.Snapshot.Activity.Count(a => a.ScriptId == ids[1] && a.Operation == ScriptOperation.Start && a.Code == ReasonCode.Cancelled) == 1, "login cancellation during second observation reports partial prior effect and persists second cancellation once");
            else Verify(boundary.Starts == (scenario == "uncertain" ? 1 : 0) && result.Outcome == Outcome.Partial && result.Detail?.Contains(scenario == "uncertain" ? "não repetirá" : "inventário", StringComparison.OrdinalIgnoreCase) == true, "login " + scenario + " preserves shared safety barrier with visible reason");
            int starts = boundary.Starts;
            Verify(service.Snapshot.Activity.Count(a => a.ScriptId is null && a.Operation == ScriptOperation.Start && a.Detail == result.Detail) == 1, "login " + scenario + " persists one batch summary");
            Verify((await service.RunLoginAsync()).Code == ReasonCode.StartupAlreadyProcessed && boundary.Starts == starts, "login " + scenario + " is durably dispatched once");
        }
        Check(failures.Count == 0, "login startup regressions: " + string.Join("; ", failures));
    }
    static async Task PreserveInactiveStartupAsync(string interpreter)
    {
        string root = Path.Combine(FixtureRoot, "inactive-startup"), path = Path.Combine(root, "fixture.ahk"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(root); Directory.CreateDirectory(data); await File.WriteAllTextAsync(path, "#Requires AutoHotkey v2\n");
        Guid id = Guid.NewGuid(); var store = new SettingsStore(Path.Combine(data, "settings.json")); var original = await store.LoadAsync();
        Check((await store.SaveAsync(original.Document with { Scripts = [new() { Id = id, Path = path, FriendlyName = "Fixture", RunAtLogin = true }], Settings = new() { InterpreterPath = interpreter, LaunchManagerAtLogin = false, LaunchManagerAsAdministrator = false } })).Succeeded, "seed existing true row startup while global startup inactive");
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
    // These fixtures exercise the normal HKCU registration path. Administrative
    // registration and the new profile defaults have separate contract checks.
    static async Task UseNormalStartupFixtureAsync(ManagerService manager)
    {
        Check((await manager.SaveSettingsAsync(manager.Snapshot.Settings with
        {
            LaunchManagerAtLogin = false,
            LaunchManagerAsAdministrator = false
        })).Succeeded, "normal-startup fixture explicitly disables new administrative login defaults");
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
            await manager.LoadAsync(); await UseNormalStartupFixtureAsync(manager); await manager.AddFilesAsync([path]);
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
        public StartupInspection Inspection = new(true, [], []);
        public bool CancelInspection;
        public Action? AfterInspection;
        public string? ReadEntry() => Entry;
        public bool CompareExchangeEntry(string? expected, string? replacement) { if (Entry != expected) return false; Entry = replacement; Writes++; return true; }
        public Task<StartupInspection> InspectAsync(CancellationToken ct)
        {
            if (CancelInspection) return Task.FromException<StartupInspection>(new OperationCanceledException(ct));
            AfterInspection?.Invoke();
            return Task.FromResult(Inspection);
        }
    }
    sealed class FakeProcesses(FakeOwner owner) : IProcessManager
    {
        public int Starts, Controls; public bool UnknownStart, UnknownWithIdentity, Administrator, LastElevated;
        public OperationResult? NextStart;
        public ReasonCode? NormalLaunchDenied;
        public List<(string Path, bool Elevated)> Launches = [];
        public bool IsAdministrator => Administrator;
        public ReasonCode? GetNormalLaunchUnavailableReason() => NormalLaunchDenied;
        public OperationResult NextControl = new(Outcome.Confirmed);
        // Test-only deterministic fault injection after the fake has confirmed a Stop.
        public Action? AfterControl;
        public Action? AfterStart;
        public int Observations;
        public Action<int>? AfterObserve;
        public ObservedProcesses Current = new([], true, []);
        public event EventHandler? Invalidated { add { } remove { } }
        public bool Supports(ScriptOperation operation) => true;
        public Task<ObservedProcesses> ObserveAsync(IReadOnlyCollection<string> known, CancellationToken ct = default) { AfterObserve?.Invoke(++Observations); return Task.FromResult(Current); }
        public void SetRunning(string script, string interpreter)
        {
            var identity = new ProcessIdentity(7100 + Starts, DateTime.UtcNow.ToFileTimeUtc(), interpreter, script, owner.Sid, 1, ScriptPaths.Fingerprint(script), ScriptPaths.Fingerprint(interpreter));
            Current = new([new(identity, ExecutionOrigin.External, "2.0", DateTimeOffset.UtcNow, Suspended: false, WindowVerified: true)], true, []);
        }
        public Task<LaunchResult> StartAsync(string interpreter, string script, bool elevated, CancellationToken ct = default)
        {
            Starts++; LastElevated = elevated; Launches.Add((script, elevated));
            if (NextStart is { } failure) { NextStart = null; return Task.FromResult(new LaunchResult(failure, null)); }
            SetRunning(script, interpreter);
            AfterStart?.Invoke();
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
