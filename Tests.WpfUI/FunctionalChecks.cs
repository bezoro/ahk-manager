using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

[JsonSerializable(typeof(Catalog))]
[JsonSerializable(typeof(LoginRuntime))]
internal partial class TestJson : JsonSerializerContext { }

static partial class Tests
{
    sealed class FunctionalRig : IDisposable
    {
        public string Directory { get; }
        public CatalogStore Store { get; }
        public FixtureProcesses Processes { get; }
        public FixtureRecycler Recycler { get; }
        public FixtureInteraction Interaction { get; }
        public FixtureOwner Owner { get; } = new();
        public FixtureClock Clock { get; } = new();
        public UiSession Session { get; private set; }
        public ScriptEntry Entry => Session.Entries[0];
        public FunctionalRig(string name)
        {
            Directory = Path.Combine(root, name); Store = new(Directory);
            var fixture = FixtureFactory.Create(Store, Directory, "normal");
            Processes = fixture.Processes; Recycler = fixture.Recycler; Interaction = fixture.Interaction;
            Session = Create();
        }
        UiSession Create() => new(Store, Processes, Recycler, Interaction, () => Task.FromResult(new DiscoveryResult([], [new(Path.Combine(Directory, "harmless-scripts", "desktop.ahk"), "Atalho externo ficticio: legado ainda pode relancar", false)], [])), Owner, Clock);
        public void Reload() => Session = Create();
        public void Arm(bool enabled = true)
        {
            var catalog = Store.Load(); var entry = catalog.Scripts[0]; entry.ManagementMode = ManagementMode.Normal; entry.RequestedEnabled = enabled;
            var journal = new MigrationJournal { Fixture = true, ManifestHash = new('A', 64), OwnerSid = Owner.Sid, State = MigrationState.Completed, AppliedSteps = [.. JournalStore.Steps] };
            catalog.Migration = new() { State = MigrationState.Completed, ManifestHash = journal.ManifestHash, OwnerSid = Owner.Sid, JournalDigest = JournalStore.Save(Directory, journal), CompletedUtcTicks = Clock.UtcNow.Ticks };
            catalog.Runtime = new() { LoginKey = Owner.LoginKey, Decided = true }; catalog.Runtime.Set(entry.Id, enabled);
            Store.Save(catalog); Reload();
        }
        public void EmptyProcesses() => Processes.Current = new([], true, "Owned fixture snapshot");
        public void Dispose() { Owner.Dispose(); Store.Dispose(); }
    }
    sealed class MigrationEffects(CatalogStore store) : IMigrationEffects
    {
        public List<string> Calls { get; } = [];
        public string FailAt { get; set; } = "";
        public int Rollbacks { get; private set; }
        public int TaskCalls { get; private set; }
        public int NormalStartupCalls { get; private set; }
        public ProcessIdentity? Stopped { get; private set; }
        public bool IncompleteAtEveryStep { get; private set; } = true;
        public Result Apply(string step, CutoverManifest manifest, ProcessIdentity? supervisor)
        {
            Calls.Add(step); IncompleteAtEveryStep &= store.Load().Migration.State == MigrationState.Incomplete;
            if (step == "SupervisorStopped") Stopped = supervisor;
            if (step == "NormalLoginPrepared") NormalStartupCalls++;
            return step == FailAt ? Result.Fail("Fake refusal") : Result.Ok("Fake only");
        }
        public Result Rollback(CutoverManifest manifest) { Rollbacks++; return Result.Ok("Fake rollback"); }
    }
    static CutoverManifest ManifestFor(FunctionalRig rig) => new()
    {
        Root = Path.Combine(rig.Directory, "harmless-scripts"), LegacyShortcut = Path.Combine(rig.Directory, "Startup", "legacy.lnk"), LegacyShortcutSHA256 = new('B', 64), ProposedArchive = Path.Combine(rig.Directory, "future-backup", "legacy.lnk"), NormalLoginExecutable = Path.Combine(rig.Directory, "future-app.exe"), CatalogExpectedSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rig.Store.FilePath))),
        Choices = [new() { RelativePath = "desktop.ahk", ExpectedSHA256 = new('C', 64), Mode = ManagementMode.Normal, RequestedEnabled = true }, new() { RelativePath = "brush.ahk", ExpectedSHA256 = new('D', 64), Mode = ManagementMode.ExternalPending, RequestedEnabled = true }]
    };
    static async Task RunFunctional()
    {
        using (var r = new FunctionalRig("schema"))
        {
            var legacy = r.Store.Load(); legacy.Schema = 1; foreach (var e in legacy.Scripts) { e.LaunchBlocked = false; e.RequestedEnabled = true; e.ManagementMode = ManagementMode.Normal; }
            LegacyPayload(r.Store, legacy, 1); r.Reload();
            var converted = r.Session.CurrentCatalog;
            Check(r.Session.NeedsSchemaConversion, "schema1 remains explicit conversion pending");
            Check(converted.Scripts.All(e => e.RequestedEnabled is null && e.ManagementMode == ManagementMode.Unmanaged), "legacy false blocks never infer Enabled");
            Check(converted.Scripts[0].Id == legacy.Scripts[0].Id && converted.Scripts[0].Description == legacy.Scripts[0].Description && converted.Scripts[0].Interpreter == legacy.Scripts[0].Interpreter, "legacy identities and metadata preserved");
            await r.Session.Refresh(); Check(r.Store.LoadedSchema == 1 && r.Processes.Starts == 0, "refresh does not silently convert schema1");
            await r.Session.ConvertSchema(); Check(r.Store.LoadedSchema == 1, "conversion Cancel preserves schema1");
            r.Interaction.Confirmed = true; await r.Session.ConvertSchema(); Check(r.Store.LoadedSchema == 5 && r.Store.Load().Scripts.All(e => e.RequestedEnabled is null), "explicit conversion persisted Unmanaged");
        }
        using (var r = new FunctionalRig("checkbox"))
        {
            r.Arm(); await r.Session.Refresh(); var runtime = JsonSerializer.Serialize(r.Session.CurrentCatalog.Runtime, TestJson.Default.LoginRuntime);
            await r.Session.SetIntent(false);
            Check(r.Entry.StartWithWindows == false && r.Processes.Stops == 0 && r.Processes.Starts == 0, "checkbox only saves next-login choice");
            Check(runtime == JsonSerializer.Serialize(r.Session.CurrentCatalog.Runtime, TestJson.Default.LoginRuntime), "checkbox preserves current runtime decision");
            r.Interaction.Confirmed = true; await r.Session.Act(ScriptAction.Stop);
            Check(r.Processes.Stops == 1 && r.Entry.StartWithWindows == false && !r.Session.CurrentCatalog.Runtime.Wants(r.Entry.Id), "single Stop changes runtime only");
            await r.Session.Act(ScriptAction.Start);
            Check(r.Processes.Starts == 1 && r.Entry.StartWithWindows == false && r.Session.CurrentCatalog.Runtime.Wants(r.Entry.Id), "single Start works with autostart false and preserves it");
        }
        using (var r = new FunctionalRig("stop-refused"))
        {
            r.Arm(); await r.Session.Refresh(); r.Interaction.Confirmed = true; r.Processes.FailStop = true;
            await r.Session.Act(ScriptAction.Stop);
            Check(r.Entry.StartWithWindows == true && !r.Session.CurrentCatalog.Runtime.Wants(r.Entry.Id) && r.Processes.Current.For(r.Entry.Path).Count == 1, "refused single Stop durable runtime false; autostart preserved");
            await r.Session.Reconcile(); Check(r.Processes.Starts == 0 && r.Processes.Stops == 1, "false runtime never implicitly stops or relaunches");
        }
        using (var r = new FunctionalRig("adopt"))
        {
            r.Arm(); await r.Session.Reconcile(); Check(r.Processes.Starts == 0 && r.Processes.Stops == 0, "running Enabled adopted without duplicate start");
            r.EmptyProcesses(); bool reservationDurable = false;
            r.Processes.BeforeStart = () => reservationDurable = r.Store.Load().Recovery[0].AttemptsUtcTicks.Count == 1;
            await r.Session.Reconcile(); Check(reservationDurable && r.Processes.Starts == 1, "retry reservation durable before Start");
            await r.Session.Reconcile(); Check(r.Processes.Starts == 1, "next tick adopts newly running identity");
        }
        using (var r = new FunctionalRig("retry"))
        {
            r.Arm(); r.EmptyProcesses(); r.Processes.FailStart = true;
            await r.Session.Reconcile(); Check(r.Processes.Starts == 1 && r.Store.Load().Recovery[0].NextTryUtcTicks == r.Clock.UtcNow.AddSeconds(60).Ticks, "first failure cooldown60");
            r.Reload(); await r.Session.Reconcile(); Check(r.Processes.Starts == 1, "same login reopen preserves cooldown");
            r.Session.RetryRecovery(); Check(r.Processes.Starts == 1 && r.Store.Load().Recovery[0].Failures == 1, "manual retry cannot bypass cooldown");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(60); await r.Session.Reconcile(); Check(r.Processes.Starts == 2 && r.Store.Load().Recovery[0].Failures == 2, "second failure counted");
            Check(r.Store.Load().Recovery[0].NextTryUtcTicks == r.Clock.UtcNow.AddSeconds(120).Ticks, "second cooldown120");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(120); await r.Session.Reconcile(); Check(r.Processes.Starts == 3 && r.Store.Load().Recovery[0].Suspended, "third failure suspends recovery");
            Check(r.Store.Load().Recovery[0].NextTryUtcTicks == r.Clock.UtcNow.AddSeconds(240).Ticks, "third cooldown240");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(240); r.Session.RetryRecovery(); await r.Session.Reconcile(); Check(r.Processes.Starts == 3, "fourth Start in10min forbidden even Retry");
            r.Clock.UtcNow = r.Clock.UtcNow.AddMinutes(10); r.Session.RetryRecovery(); await r.Session.Reconcile(); Check(r.Processes.Starts == 4, "explicit retry after window permits one attempt");
            r.Owner.LoginKey += "-new-login"; r.Reload(); await r.Session.PrepareLogin(true); await r.Session.Reconcile(); Check(r.Processes.Starts == 5 && r.Store.Load().Recovery[0].AttemptsUtcTicks.Count == 1, "new login resets budget with new reliable identity");
        }
        foreach (var problem in new[] { "unknown", "partial", "owner", "untrusted", "journal", "journal-mode", "migration", "primary", "readonly" })
        {
            using var r = new FunctionalRig("gate-" + problem); r.Arm(false);
            if (problem == "unknown") r.Processes.Current = new([new(99, 0, "", null)], true, "unknown");
            if (problem == "partial") r.Processes.Current = r.Processes.Current with { Complete = false };
            if (problem == "owner") r.Owner.Held = false;
            if (problem == "untrusted") r.Owner.Trusted = false;
            if (problem == "journal") File.WriteAllText(JournalStore.PathFor(r.Directory), "{bad}");
            if (problem == "journal-mode")
            {
                var c = r.Store.Load(); c.Migration.JournalDigest = JournalStore.Save(r.Directory, new() { Fixture = false, State = MigrationState.Completed, ManifestHash = c.Migration.ManifestHash, OwnerSid = r.Owner.Sid, AppliedSteps = [.. JournalStore.Steps] }); r.Store.Save(c); r.Reload();
            }
            if (problem == "migration") { var c = r.Store.Load(); c.Migration.State = MigrationState.Incomplete; r.Store.Save(c); r.Reload(); }
            if (problem is "primary" or "readonly") { File.WriteAllText(r.Store.FilePath, "{bad}"); if (problem == "readonly") r.Reload(); }
            await r.Session.Reconcile(); Check(r.Processes.Starts == 0 && r.Processes.Stops == 0, "automatic zero effects gate:" + problem);
            if (problem == "readonly") { r.Interaction.Confirmed = true; await r.Session.SetIntent(false); Check(r.Processes.Stops == 0 && r.Interaction.Confirmations == 0, "backup consultation readonly refuses Disable effects"); }
        }
        using (var r = new FunctionalRig("concurrent"))
        {
            r.Arm(); r.EmptyProcesses(); r.Processes.HoldStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = r.Session.Reconcile(); Check(r.Session.Busy, "tick owns same UI lane");
            await r.Session.SetIntent(false); await r.Session.Act(ScriptAction.Start); await r.Session.Reconcile();
            Check(r.Processes.Starts == 1 && r.Processes.Stops == 0 && r.Interaction.Confirmations == 0, "concurrent UI/tick/disable rejected without parallel effects");
            var window = new MainWindow(r.Session) { CloseToTray = true }; bool hidden = false; window.HiddenToTray += () => hidden = true; window.Close();
            Check(hidden && r.Session.Busy && r.Owner.Held, "X while busy hides and retains owner/lane");
            var exiting = r.Session.PrepareExit(TimeSpan.FromMilliseconds(1)); Check(!await exiting && r.Owner.Held, "bounded exit does not release owner while action pending");
            await r.Session.Reconcile(); Check(r.Processes.Starts == 1, "shutdown refuses new ticks");
            r.Processes.HoldStart.SetResult(); await start; Check(!r.Session.Busy && await r.Session.PrepareExit(TimeSpan.FromMilliseconds(1)), "action finishes then exit becomes safe");
            Check(r.Processes.Stops == 0, "exit leaves existing scripts alive"); window.CloseToTray = false; window.Close();
        }
        using (var r = new FunctionalRig("external"))
        {
            var c = r.Store.Load(); c.Scripts[0].ManagementMode = ManagementMode.ExternalPending; c.Scripts[0].RequestedEnabled = true; r.Store.Save(c); r.Reload();
            await r.Session.SetIntent(false); await r.Session.Act(ScriptAction.Stop); await r.Session.Reconcile();
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Entry.RequestedEnabled == true, "elevated external pending implements no task/process effect");
            Check(r.Session.IntentState(r.Entry).Contains("pendente", StringComparison.Ordinal), "external pending explicit UI state");
        }
        using (var r = new FunctionalRig("unmanaged"))
        {
            await r.Session.Reconcile(); Check(r.Processes.Starts == 0 && r.Processes.Stops == 0, "new default unmanaged never automatically launches");
            r.Interaction.Confirmed = true; await r.Session.SetIntent(false);
            Check(r.Entry.StartWithWindows is null && r.Processes.Stops == 0, "unmanaged startup checkbox cannot invent management authority");
        }
        foreach (var fail in new[] { "", "LegacyShortcutArchived", "SupervisorStopped", "NormalLoginPrepared" })
        {
            using var r = new FunctionalRig("migration-" + (fail.Length == 0 ? "success" : fail)); var manifest = ManifestFor(r); var effects = new MigrationEffects(r.Store) { FailAt = fail };
            var supervisor = new ProcessIdentity(8123, DateTime.UtcNow.ToFileTimeUtc(), r.Entry.Interpreter, Path.Combine(manifest.Root, "startup.ahk"));
            var snapshot = new Snapshot([supervisor, .. r.Processes.Current.Processes], true, "Only fake processes");
            var before = File.ReadAllBytes(r.Store.FilePath);
            var cancelled = MigrationSimulation.Execute(r.Store, r.Store.Load(), manifest, snapshot, r.Owner, r.Clock, effects, false);
            Check(!cancelled.Success && effects.Calls.Count == 0 && before.AsSpan().SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), "migration Cancel no writes/effects:" + fail);
            var result = MigrationSimulation.Execute(r.Store, r.Store.Load(), manifest, snapshot, r.Owner, r.Clock, effects, true);
            Check(effects.IncompleteAtEveryStep && effects.TaskCalls == 0, "cutover never arms early or invokes task:" + fail);
            if (fail.Length == 0)
            {
                var c = r.Store.Load(); Check(result.Success && effects.Calls.SequenceEqual(JournalStore.Steps[..^1]), "fake cutover order exact");
                Check(effects.NormalStartupCalls == 1 && effects.Stopped == supervisor, "one normal startup and sole supervisor identity passed to fake");
                Check(c.Migration.State == MigrationState.Completed && JournalStore.Compatible(r.Directory, c.Migration, true), "durable completion plus compatible journal before ticks");
                Check(c.Scripts[0].RequestedEnabled == true && c.Scripts[1].ManagementMode == ManagementMode.ExternalPending && c.Scripts[1].Id == r.Session.Entries[1].Id, "approved choices preserve IDs and keep brush external");
            }
            else Check(!result.Success && effects.Rollbacks == 1 && r.Store.Load().Migration.State == MigrationState.Incomplete, "fake failure rollback remains disarmed:" + fail);
        }
        using (var r = new FunctionalRig("migration-gates"))
        {
            var m = ManifestFor(r); var effects = new MigrationEffects(r.Store); m.Pending.Add("Unknown AHK identity requires approval");
            Check(!MigrationSimulation.Execute(r.Store, r.Store.Load(), m, r.Processes.Current, r.Owner, r.Clock, effects, true).Success && effects.Calls.Count == 0, "real manifest pending identity prevents cutover even fixture");
            m.Pending.Clear(); var supervisor = new ProcessIdentity(1, 1, r.Entry.Interpreter, Path.Combine(m.Root, "startup.ahk"));
            var duplicate = new Snapshot([supervisor, supervisor with { Pid = 2 }], true, "conflict");
            Check(!MigrationSimulation.Execute(r.Store, r.Store.Load(), m, duplicate, r.Owner, r.Clock, effects, true).Success && effects.Calls.Count == 0, "legacy duplicate supervisor conflict blocks fake cutover");
            var reuse = new Snapshot([supervisor with { Created = 0 }], true, "stale");
            Check(!MigrationSimulation.Execute(r.Store, r.Store.Load(), m, reuse, r.Owner, r.Clock, effects, true).Success && effects.Calls.Count == 0, "unknown creation identity blocks supervisor effect");
        }
        Check(UserEffectOwner.MutexName("SID-A") == UserEffectOwner.MutexName("SID-A") && UserEffectOwner.MutexName("SID-A") != UserEffectOwner.MutexName("SID-B"), "exclusion keyed only by user SID not data directory");
        using (var first = new UserEffectOwner())
        {
            Check(first.Trusted && first.Held && first.LoginKey.StartsWith(first.Sid + "|", StringComparison.Ordinal), "native token SID/loginLUID and nonblocking owner obtained");
            using var second = new UserEffectOwner(); Check(!second.Held, "same user second data directory cannot become second effects owner");
        }
        foreach (var state in new[] { "unmanaged", "enabled", "disabled", "suspended", "pending", "readonly" })
        {
            using var r = new FunctionalRig("render-" + state);
            if (state is "enabled" or "disabled" or "suspended") r.Arm(state != "disabled");
            if (state == "suspended") { r.EmptyProcesses(); r.Processes.FailStart = true; await r.Session.Reconcile(); r.Clock.UtcNow = r.Clock.UtcNow.AddMinutes(1); await r.Session.Reconcile(); r.Clock.UtcNow = r.Clock.UtcNow.AddMinutes(2); await r.Session.Reconcile(); }
            if (state == "pending") { var c = r.Store.Load(); c.Scripts[0].ManagementMode = ManagementMode.ExternalPending; c.Scripts[0].RequestedEnabled = true; r.Store.Save(c); r.Reload(); }
            if (state == "readonly") { File.WriteAllText(r.Store.FilePath, "{bad}"); r.Reload(); }
            var window = new MainWindow(r.Session);
            WpfProgram.Render(window, Path.Combine(root, "renders", state + "-1000x720.png"), 1000, 720, 1);
            WpfProgram.Render(window, Path.Combine(root, "renders", state + "-1220x800-150.png"), 1220, 800, 1.5);
            Check(window.FixedActions.DesiredSize.Height > 0, "new intent controls preserve fixed actions:" + state); window.Close();
        }
    }
}
