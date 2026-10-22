using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    static string RuntimeJson(Catalog c) => JsonSerializer.Serialize(c.Runtime, TestJson.Default.LoginRuntime);
    static void NormalSnapshot(FunctionalRig r)
    {
        r.Processes.Current = r.Processes.Current with { Processes = r.Processes.Current.Processes.Select(p => p with { Elevated = false }).ToList() };
    }
    static async Task RunIntegrated()
    {
        foreach (int schema in new[] { 1, 2, 3, 4 })
        {
            using var r = new FunctionalRig("legacy5-" + schema); r.Arm(false);
            var before = r.Store.Load(); before.Scripts[1].StartWithWindows = true; before.Scripts[2].StartWithWindows = null;
            LegacyPayload(r.Store, before, schema); r.Reload();
            Check(r.Store.LoadedSchema == schema && r.Session.CurrentCatalog.Schema == 5 && r.Session.NeedsSchemaConversion, "legacy explicit schema5 pending " + schema);
            Check(r.Entry.StartWithWindows == (schema == 1 ? null : false) && r.Session.Entries[1].StartWithWindows == (schema == 1 ? null : true) && r.Session.Entries[2].StartWithWindows is null, "legacy true false unresolved preserved " + schema);
            Check(r.Session.Entries.Zip(before.Scripts).All(p => p.First.Id == p.Second.Id && p.First.Description == p.Second.Description && p.First.Interpreter == p.Second.Interpreter), "legacy metadata exact " + schema);
            r.Interaction.Confirmed = true; await r.Session.ConvertSchema();
            Check(r.Store.LoadedSchema == 5 && r.Processes.Starts == 0 && r.Processes.Stops == 0, "conversion has zero process effects " + schema);
            var payload = File.ReadAllText(r.Store.FilePath); Check(payload.Contains("StartWithWindows") && !payload.Contains("RequestedEnabled"), "one serialized preference " + schema);
        }
        foreach (string malformed in new[] { "duplicate", "type", "oldfield", "runtime", "legacy-null" })
        {
            using var r = new FunctionalRig("invalid5-" + malformed); r.Arm();
            var text = File.ReadAllText(r.Store.FilePath);
            text = malformed switch { "duplicate" => text.Replace("\"StartWithWindows\":true", "\"StartWithWindows\":true,\"StartWithWindows\":false"), "type" => text.Replace("\"StartWithWindows\":true", "\"StartWithWindows\":\"yes\""), "oldfield" => text.Replace("\"StartWithWindows\":true", "\"StartWithWindows\":true,\"RequestedEnabled\":true"), _ => text.Replace("\"LoginKey\":\"login-1\"", "\"LoginKey\":\"\"") };
            if (malformed == "runtime") { var n = JsonNode.Parse(text)!; n["Runtime"]!["LoginKey"] = ""; text = n.ToJsonString(); }
            if (malformed == "legacy-null") { var n = JsonNode.Parse(text)!; n["Schema"] = 4; n["Scripts"] = null; n.AsObject().Remove("Runtime"); text = n.ToJsonString(); }
            File.WriteAllText(r.Store.FilePath, text); r.Reload(); await r.Session.PrepareLogin(true); await r.Session.Reconcile();
            Check(r.Store.ReadOnly && r.Processes.Starts == 0 && r.Processes.Stops == 0, "malformed schema5 failclosed " + malformed);
        }
        using (var r = new FunctionalRig("checkbox5"))
        {
            r.Arm(); NormalSnapshot(r); await r.Session.Refresh(); var before = r.Session.CurrentCatalog; var ledger = File.ReadAllBytes(r.Session.Authority.FilePath);
            r.Session.SetStartWithWindows(r.Entry.Id, false);
            Check(r.Entry.StartWithWindows == false && r.Processes.Starts == 0 && r.Processes.Stops == 0, "uncheck leaves running instance");
            Check(RuntimeJson(before) == RuntimeJson(r.Session.CurrentCatalog) && ledger.SequenceEqual(File.ReadAllBytes(r.Session.Authority.FilePath)), "checkbox preserves runtime and ledger exact");
            await r.Session.Reconcile(); Check(r.Processes.Stops == 0, "autostart false never stops existing process");
            r.Session.SetStartWithWindows(r.Entry.Id, true); Check(r.Processes.Starts == 0, "checking never starts now");
        }
        foreach (bool login in new[] { false, true })
        {
            using var r = new FunctionalRig("login5-" + login); r.Arm(); var c = r.Store.Load(); c.Runtime = new(); r.Store.Save(c); r.Reload(); r.EmptyProcesses();
            bool decisionBeforeStart = false; r.Processes.BeforeStart = () => decisionBeforeStart = r.Store.Load().Runtime.Decided && r.Store.Load().Recovery.Count == 1;
            await r.Session.PrepareLogin(login); await r.Session.Reconcile();
            Check(r.Store.Load().Runtime.Decided && r.Processes.Starts == (login ? 1 : 0), "explicit login only seeds saved choice " + login);
            Check(!login || decisionBeforeStart, "decision and reservation durable before launch");
            r.Session.SetStartWithWindows(r.Entry.Id, !login); r.EmptyProcesses(); r.Reload(); await r.Session.PrepareLogin(true); await r.Session.Reconcile();
            Check(r.Processes.Starts == (login ? 2 : 0), "same-login entry never reseeds checkbox changes " + login);
            r.Owner.LoginKey += "next"; r.Reload(); await r.Session.PrepareLogin(true); await r.Session.Reconcile();
            Check(r.Store.Load().Runtime.LoginKey == r.Owner.LoginKey && r.Processes.Starts == (login ? 2 : 1), "new login applies saved choice " + login);
        }
        using (var r = new FunctionalRig("seed-write-fail"))
        {
            r.Arm(); var c = r.Store.Load(); c.Runtime = new(); r.Store.Save(c); r.Reload(); r.EmptyProcesses();
            Directory.CreateDirectory(r.Store.FilePath + ".tmp"); await r.Session.PrepareLogin(true); await r.Session.Reconcile();
            Check(r.Processes.Starts == 0 && !r.Store.Load().Runtime.Decided, "seed write failure cannot launch");
        }
        foreach (BatchAction action in Enum.GetValues<BatchAction>())
        {
            using var r = new FunctionalRig("batch-cancel-" + action); r.Arm(); NormalSnapshot(r); var primary = File.ReadAllBytes(r.Store.FilePath); var ledger = File.ReadAllBytes(r.Session.Authority.FilePath);
            var result = await r.Session.RunBatch(action);
            Check(result.Cancelled && r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Interaction.Confirmations == 1, "batch cancellation zero effects " + action);
            Check(primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && ledger.SequenceEqual(File.ReadAllBytes(r.Session.Authority.FilePath)) && !r.Session.AutomaticPaused && r.Owner.Held, "cancel zero writes restores owner pause " + action);
        }
        using (var r = new FunctionalRig("batch-stop-all"))
        {
            r.Arm(false); NormalSnapshot(r); r.Interaction.Confirmed = true;
            var c = r.Store.Load(); c.Scripts[0].RunAsAdministrator = true; r.Store.Save(c); r.Reload();
            var saved = r.Session.CurrentCatalog; var ledger = File.ReadAllBytes(r.Session.Authority.FilePath); bool beforeStop = false;
            r.Processes.BeforeStop = () => beforeStop = !r.Store.Load().Runtime.Wants(r.Entry.Id) && r.Store.Load().Runtime.AutomaticPaused;
            var result = await r.Session.RunBatch(BatchAction.Stop, acknowledge: async (report, token) => { Check(r.Session.Busy && report.Items.Any(i => i.Stopped), "result owns lane"); await r.Session.Reconcile(); await r.Session.Act(ScriptAction.Start); Check(r.Processes.Starts == 0, "no launch producer during result"); await Task.CompletedTask; });
            Check(beforeStop && r.Processes.Stops == 1 && r.Processes.Starts == 0 && result.Complete && result.AutomaticPaused, "Stop known Normal despite false autostart and future admin preference");
            Check(r.Entry.StartWithWindows == false && r.Entry.RunAsAdministrator && r.Session.AutomaticPaused, "Stop preserves future settings and pauses this login");
            Check(saved.Recovery.Count == r.Session.CurrentCatalog.Recovery.Count && ledger.SequenceEqual(File.ReadAllBytes(r.Session.Authority.FilePath)), "Stop preserves budget and authority");
            await r.Session.Reconcile(); r.Reload(); await r.Session.PrepareLogin(false); await r.Session.Reconcile(); Check(r.Processes.Starts == 0 && r.Session.AutomaticPaused, "timer and same-login reopen keep stop pause");
        }
        foreach (string skip in new[] { "elevated", "unknown", "duplicate", "external", "retired", "unmanaged", "blocked", "denied", "partial", "owner", "primary", "changed" })
        {
            using var r = new FunctionalRig("batch-skip-" + skip); r.Arm(); NormalSnapshot(r); r.Interaction.Confirmed = true;
            if (skip == "elevated" || skip == "unknown") r.Processes.Current = r.Processes.Current with { Processes = r.Processes.Current.Processes.Select(p => p with { Elevated = skip == "elevated" ? true : null }).ToList() };
            if (skip == "duplicate") r.Processes.Current.Processes.Add(r.Processes.Current.Processes[0] with { Pid = 6002 });
            if (skip is "external" or "retired" or "unmanaged" or "blocked") { var c = r.Store.Load(); c.Scripts[0].ManagementMode = skip == "external" ? ManagementMode.ExternalPending : skip == "retired" ? ManagementMode.Retired : skip == "unmanaged" ? ManagementMode.Unmanaged : ManagementMode.Normal; c.Scripts[0].LaunchBlocked = skip == "blocked"; if (skip == "retired") c.Scripts[0].StartWithWindows = false; r.Store.Save(c); r.Reload(); }

            if (skip == "denied") r.Processes.FailStop = true;
            if (skip == "partial") r.Processes.Current = r.Processes.Current with { Complete = false };
            if (skip == "owner") r.Owner.Held = false;
            if (skip == "primary") File.WriteAllText(r.Store.FilePath, "{invalid}");
            if (skip == "changed") { int queries = 0; r.Processes.BeforeSnapshot = () => { if (++queries == 2) r.Processes.Current = r.Processes.Current with { Processes = r.Processes.Current.Processes.Select(p => p with { Created = p.Created + 1 }).ToList() }; }; }
            var result = await r.Session.RunBatch(BatchAction.Stop);
            Check(r.Processes.Starts == 0 && r.Processes.Stops == (skip == "denied" ? 1 : 0) && !result.Complete && (skip == "denied" || !result.AutomaticPaused), "safe survivor no retarget " + skip);
            Check(r.Entry.StartWithWindows == (skip == "retired" ? false : true), "skip preserves autostart " + skip);
        }
        using (var r = new FunctionalRig("batch-start-disabled"))
        {
            r.Arm(false); r.EmptyProcesses(); r.Interaction.Confirmed = true; bool reserve = false;
            r.Processes.BeforeStart = () => reserve = r.Store.Load().Recovery[0].AttemptsUtcTicks.Count == 1;
            var result = await r.Session.RunBatch(BatchAction.Start);
            Check(reserve && r.Processes.Starts == 1 && r.Entry.StartWithWindows == false && r.Session.CurrentCatalog.Runtime.Wants(r.Entry.Id), "StartAll saved false reserves and preserves future flag");
            await r.Session.Reconcile(); Check(r.Processes.Starts == 1 && r.Session.AutomaticPaused, "automatic pause never duplicates manual StartAll");
            await r.Session.RunBatch(BatchAction.Stop); Check(r.Processes.Stops == 1, "explicit later action available while automatically paused");
        }
        foreach (string failure in new[] { "none", "stop", "start", "idle", "admin" })
        {
            using var r = new FunctionalRig("batch-restart-" + failure); r.Arm(false); NormalSnapshot(r); r.Interaction.Confirmed = true;
            if (failure == "stop") r.Processes.FailStop = true;
            if (failure == "start") r.Processes.FailStart = true;
            if (failure == "idle") r.EmptyProcesses();
            if (failure == "admin") { var c = r.Store.Load(); c.Scripts[0].RunAsAdministrator = true; r.Store.Save(c); r.Reload(); }
            bool reserved = false; r.Processes.BeforeStop = () => reserved = r.Store.Load().Recovery[0].AttemptsUtcTicks.Count == 1;
            var result = await r.Session.RunBatch(BatchAction.RestartRunning);
            Check(r.Processes.Stops == (failure is "idle" or "admin" ? 0 : 1) && r.Processes.Starts == (failure is "none" or "start" ? 1 : 0), "restart eligible running only " + failure);
            Check(r.Entry.StartWithWindows == false && (failure is "idle" or "admin" || reserved), "restart reserve-before-stop and saved flag preserved " + failure);
            if (failure == "start") Check(result.Items.Any(i => i.Stopped && i.Outcome.StartsWith("Parado; reinício não concluído")), "post-stop launch failure distinguished");
        }
        using (var r = new FunctionalRig("batch-prompt-lane"))
        {
            r.Arm(); NormalSnapshot(r); r.Interaction.HoldConfirmation = new(); var first = r.Session.RunBatch(BatchAction.Stop); var second = await r.Session.RunBatch(BatchAction.Stop);
            await r.Session.Reconcile(); await r.Session.Act(ScriptAction.Start); await r.Session.SetIntent(false); r.Session.RetryRecovery();
            Check(second.Cancelled && r.Interaction.Confirmations == 1 && r.Processes.Stops == 0 && r.Entry.StartWithWindows == true, "double click and all producers refused during prompt");
            var exit = r.Session.PrepareExit(TimeSpan.Zero); await first; Check(r.Session.ShuttingDown && !r.Session.Busy && r.Processes.Stops == 0, "logout cancels unresolved prompt and drains lane");
        }
        foreach (bool keep in new[] { false, true })
        {
            using var r = new FunctionalRig("stop-exit-" + keep); r.Arm(); NormalSnapshot(r); r.Interaction.Confirmed = true; r.Processes.FailStop = keep;
            bool finished = false; int suspends = 0, resumes = 0, choices = 0;
            var ordinary = new ExitCoordinator(r.Session, () => Task.FromResult(true), () => { }, () => finished = true, TimeSpan.Zero);
            var stopExit = new StopAllExitCoordinator(r.Session, ordinary, () => suspends++, () => resumes++, (result, token) => { choices++; return Task.FromResult(false); });
            await stopExit.Request();
            Check(finished == !keep && choices == (keep ? 1 : 0) && r.Processes.Starts == 0 && r.Processes.Stops == 1, "stop exit full success vs partial keep-open " + keep);
            Check(suspends == 1 && resumes == (keep ? 1 : 0) && r.Interaction.LastConfirmation == UiSession.StopExitConfirmation, "exact exit confirmation and reversible producer pause " + keep);
        }
        foreach (var fault in new[] { "none", "second-denied", "late-primary", "late-owner", "new-instance", "new-outside" })
        {
            using var r = new FunctionalRig("multi-stop-" + fault); r.Arm();
            var c = r.Store.Load(); c.Scripts[1].ManagementMode = ManagementMode.Normal; c.Scripts[1].LaunchBlocked = false; c.Scripts[1].StartWithWindows = false; c.Runtime.Set(c.Scripts[1].Id, true); r.Store.Save(c); r.Reload(); NormalSnapshot(r);
            r.Processes.Current.Processes.Add(new(9902, DateTime.UtcNow.ToFileTimeUtc(), c.Scripts[1].Interpreter, c.Scripts[1].Path, false)); r.Interaction.Confirmed = true;
            if (fault == "second-denied") r.Processes.BeforeStop = () => r.Processes.FailStop = r.Processes.Stops == 1;
            if (fault is "late-primary" or "late-owner") r.Processes.BeforeSnapshot = () => { if (r.Processes.Stops != 1) return; if (fault == "late-owner") r.Owner.Held = false; else { var n = JsonNode.Parse(File.ReadAllBytes(r.Store.FilePath))!; n["Scripts"]![0]!["Description"] = "late external edit preserved"; File.WriteAllText(r.Store.FilePath, n.ToJsonString()); } };
            if (fault is "new-instance" or "new-outside") { bool injected = false; r.Processes.BeforeSnapshot = () => { if (!injected && r.Processes.Stops == 2) { injected = true; r.Processes.Current.Processes.Add(new(9988, DateTime.UtcNow.ToFileTimeUtc(), r.Entry.Interpreter, fault == "new-instance" ? r.Entry.Path : Path.Combine(r.Directory, "unmanaged-new.ahk"), false)); } }; }
            var report = await r.Session.RunBatch(BatchAction.Stop);
            Check(r.Processes.Stops == (fault is "late-primary" or "late-owner" ? 1 : 2) && r.Processes.Starts == 0, "multi-target safe independent attempts " + fault);
            Check(report.Complete == (fault == "none") && r.Session.Entries[1].StartWithWindows == false && r.Entry.StartWithWindows == true, "multi-target final completeness and future settings " + fault);
            if (fault == "second-denied") Check(report.Items.Any(i => i.Stopped) && report.Items.Any(i => !i.Successful), "partial lists verified stops separately from survivors");
            if (fault == "late-primary") Check(File.ReadAllText(r.Store.FilePath).Contains("late external edit preserved"), "late valid primary bytes are not overwritten");
        }
        using (var r = new ManualRig("batch-pending-ledger"))
        {
            await r.Ready(true); await r.Session.Act(ScriptAction.Start); var ledger = Bytes(r.Ledger.FilePath);
            r.Processes.Current = new([new(9930, DateTime.UtcNow.ToFileTimeUtc(), r.Entry.Interpreter, r.Entry.Path, false)], true, "pending fixture");
            var report = await r.Session.RunBatch(BatchAction.Stop);
            Check(r.Ledger.HasPending && ledger.SequenceEqual(Bytes(r.Ledger.FilePath)) && r.Processes.Stops == 0 && !report.Complete, "pending request remains intact and its normal process survives");
        }
        foreach (string phase in new[] { "stop", "result" })
        {
            using var r = new FunctionalRig("logout-" + phase); r.Arm(); NormalSnapshot(r); r.Interaction.Confirmed = true;
            var resultHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (phase == "stop") r.Processes.HoldStop = new();
            var batch = r.Session.RunBatch(BatchAction.Stop, acknowledge: (report, token) => resultHold.Task);
            Check(r.Session.Busy && r.Processes.Stops == 1, "logout phase owns lane " + phase);
            bool finished = false; var ordinary = new ExitCoordinator(r.Session, () => Task.FromResult(true), () => { }, () => finished = true, TimeSpan.Zero);
            await ordinary.FinishConfirmed(); var report = await batch;
            Check(finished && r.Session.ShuttingDown && !r.Session.Busy && r.Processes.Starts == 0 && r.Interaction.Confirmations == 1, "logout cancels interaction/attempt and drains without second prompt " + phase);
            Check(r.Session.AutomaticPaused && report.Cancelled, "logout retains durable pause and actual outcome " + phase);
        }
        using (var r = new FunctionalRig("row-signals"))
        {
            r.Arm(false); NormalSnapshot(r); await r.Session.Refresh(); var c = r.Store.Load(); c.Scripts[1].ManagementMode = ManagementMode.ExternalPending; c.Scripts[1].RunAsAdministrator = true; r.Store.Save(c); r.Reload(); await r.Session.Refresh();
            var window = new MainWindow(r.Session); window.DescriptionControl.Text = "Retained unsaved draft"; var selected = r.Session.SelectedId;
            Check(r.Session.State(r.Entry) == "Rodando" && r.Session.PrivilegeState(r.Entry) == "Normal" && !r.Entry.RunAsAdministrator, "actual privilege independent of future preference");
            Check(window.BatchButtons.Count == 3 && window.BatchButtons.All(b => AutomationProperties.GetName(b).Length > 0), "three accessible bulk buttons once");
            foreach (double scale in new[] { 1d, 1.5, 2 })
            {
                WpfProgram.Render(window, Path.Combine(root, "renders", "integrated-min-" + scale + ".png"), 1000, 720, scale);
                Check(window.BatchButtons.All(b => b.ActualWidth > 40 && b.ActualHeight > 20) && window.FixedActions.ActualHeight > 90, "minimum batch and single controls visible DPI " + scale);
                var controls = VisualChildren(window.ListControl).OfType<CheckBox>().ToList();
                Check(controls.Count == r.Session.Entries.Count * 2 && controls.Count(b => b.Content?.ToString() == "Iniciar com o Windows") == r.Session.Entries.Count, "visible next-login and separate admin control per row " + scale);
                Check(VisualChildren(window.ListControl).OfType<TextBlock>().Any(t => t.Text.Contains("Privilégio: Normal")), "actual privilege text visible " + scale);
            }
            var longCatalog = r.Store.Load(); longCatalog.Scripts[0].Path = Path.Combine(r.Directory, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("long-directory-name", 12)), "long-script-name-with-visible-preferences.ahk"); r.Store.Save(longCatalog); r.Reload(); await r.Session.Refresh();
            var longWindow = new MainWindow(r.Session); WpfProgram.Render(longWindow, Path.Combine(root,"renders","integrated-long-min.png"),1000,720,1.5); Check(longWindow.BatchButtons.All(b => b.ActualWidth > 40), "long paths retain bulk row and fixed actions"); longWindow.Close();
            Check(window.DescriptionControl.Text == "Retained unsaved draft" && r.Session.SelectedId == selected, "new signals retain draft and selection"); window.Close();
        }
        Check(!NormalMigrationEffects.KnownSystemShell("ShellExperienceHost", @"C:\Games\ShellExperienceHost.exe", @"C:\Windows") && NormalMigrationEffects.KnownSystemShell("ShellExperienceHost", @"C:\Windows\SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe", @"C:\Windows"), "exact shell classifier refuses same-name game");
        Check(!WpfProgram.ValidLoginOrigin(true, false, false, false) && !WpfProgram.ValidLoginOrigin(true, true, true, false) && !WpfProgram.ValidLoginOrigin(true, true, false, true) && WpfProgram.ValidLoginOrigin(true, true, false, false), "login origin validates before host creation");
        Check(WpfProgram.RunNormalCutover(["--normal-cutover", "x", "--result", "x", "--login"]) == 2, "one-time migration rejects login mixture before adapters");
    }
    static async Task RunIntegratedReviewFixes()
    {
        foreach (bool forExit in new[] { false, true })
        {
            using var r = new FunctionalRig("review-stop-absent-" + forExit); r.Arm(); r.EmptyProcesses(); r.Interaction.Confirmed = true;
            var c = r.Store.Load(); c.Recovery.Add(new() { ScriptId = r.Entry.Id, LoginKey = r.Owner.LoginKey, AttemptsUtcTicks = [r.Clock.UtcNow.Ticks], Failures = 1, NextTryUtcTicks = r.Clock.UtcNow.AddMinutes(1).Ticks }); r.Store.Save(c); r.Reload();
            var before = r.Session.CurrentCatalog; var ledger = Bytes(r.Session.Authority.FilePath); bool finished = false;
            if (forExit)
            {
                var ordinary = new ExitCoordinator(r.Session, () => Task.FromResult(true), () => { }, () => finished = true, TimeSpan.Zero);
                var exit = new StopAllExitCoordinator(r.Session, ordinary, () => { }, () => { }, (_, _) => throw new InvalidOperationException("All absent must not need a partial-result decision"));
                Check(await exit.Request() && finished, "confirmed all-absent stop-exit completes gracefully");
            }
            else
            {
                var result = await r.Session.RunBatch(BatchAction.Stop);
                Check(result.Complete && result.AutomaticPaused && result.Items.Any(i => i.Outcome.Contains("persistida")), "confirmed all-absent Stop persists result intent and pause");
            }
            Check(!r.Session.CurrentCatalog.Runtime.Wants(r.Entry.Id) && r.Session.AutomaticPaused && r.Processes.Stops == 0 && r.Processes.Starts == 0, "verified-absent Stop durable false with zero process calls " + forExit);
            Check(r.Entry.StartWithWindows == true && before.Scripts.Zip(r.Session.Entries).All(p => p.First.Id == p.Second.Id && p.First.Description == p.Second.Description && p.First.RunAsAdministrator == p.Second.RunAsAdministrator && p.First.StartWithWindows == p.Second.StartWithWindows), "absent Stop preserves all future preferences and metadata " + forExit);
            var after = r.Session.CurrentCatalog.Recovery.Single(); var budget = before.Recovery.Single();
            Check(after.AttemptsUtcTicks.SequenceEqual(budget.AttemptsUtcTicks) && after.Failures == budget.Failures && after.NextTryUtcTicks == budget.NextTryUtcTicks && ledger.SequenceEqual(Bytes(r.Session.Authority.FilePath)), "absent Stop preserves recovery and ledger " + forExit);
            r.Reload(); await r.Session.PrepareLogin(false); await r.Session.Reconcile();
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Session.AutomaticPaused && !r.Session.CurrentCatalog.Runtime.Wants(r.Entry.Id), "same-login reopening/timer cannot undo absent stop " + forExit);
        }
        using (var r = new FunctionalRig("review-mixed-running-absent"))
        {
            r.Arm(); NormalSnapshot(r); r.Interaction.Confirmed = true;
            var c = r.Store.Load(); c.Scripts[1].ManagementMode = ManagementMode.Normal; c.Scripts[1].LaunchBlocked = false; c.Scripts[1].StartWithWindows = false; c.Runtime.Set(c.Scripts[1].Id, true); r.Store.Save(c); r.Reload();
            var before = r.Session.CurrentCatalog; var ledger = Bytes(r.Session.Authority.FilePath);
            var result = await r.Session.RunBatch(BatchAction.Stop);
            Check(result.Complete && r.Processes.Stops == 1 && r.Processes.Starts == 0, "mixed running/absent Stop makes exactly one stop attempt");
            Check(r.Session.Entries.Take(2).All(e => !r.Session.CurrentCatalog.Runtime.Wants(e.Id)) && r.Session.AutomaticPaused, "mixed running/absent Stop persists both runtime intents");
            Check(r.Entry.StartWithWindows == true && r.Session.Entries[1].StartWithWindows == false && before.Recovery.Count == r.Session.CurrentCatalog.Recovery.Count && ledger.SequenceEqual(Bytes(r.Session.Authority.FilePath)), "mixed Stop preserves flags budgets ledger");
            r.Reload(); await r.Session.PrepareLogin(false); await r.Session.Reconcile(); Check(r.Processes.Starts == 0, "mixed Stop cannot restart an originally absent desired-running row");
        }
        using (var r = new FunctionalRig("review-actual-row-toggle"))
        {
            r.Arm(); NormalSnapshot(r); await r.Session.Refresh(); var window = new MainWindow(r.Session);
            WpfProgram.Render(window, Path.Combine(root, "actual-row-toggle-headless.png"), 1000, 720, 1);
            var row = window.ListControl.Items.Cast<ListBoxItem>().Single(i => (Guid)i.Tag == r.Entry.Id);
            var checkbox = VisualChildren(row).OfType<CheckBox>().Single(b => b.Content?.ToString() == "Iniciar com o Windows");
            var runtime = RuntimeJson(r.Session.CurrentCatalog); var ledger = Bytes(r.Session.Authority.FilePath);
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(checkbox)!;
            var toggle = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
            Check(checkbox.IsChecked == true && !checkbox.IsThreeState, "actual row uses boolean activation with checked preference");
            // Automation invokes the actual WPF OnToggle; routed Click invokes the row's real persistence handler.
            toggle.Toggle(); checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(checkbox.IsChecked == false && r.Entry.StartWithWindows == false, "one actual row toggle true-to-false persists explicit false");
            toggle.Toggle(); checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(checkbox.IsChecked == true && r.Entry.StartWithWindows == true, "one actual row toggle false-to-true persists explicit true");
            r.Session.SetStartWithWindows(r.Entry.Id, null); Check(checkbox.IsChecked is null && r.Entry.StartWithWindows is null, "unresolved legacy null stays representable in actual row");
            toggle.Toggle(); checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(checkbox.IsChecked == false && r.Entry.StartWithWindows == false, "actual activation of unresolved row chooses explicit boolean false");
            Check(RuntimeJson(r.Session.CurrentCatalog) == runtime && ledger.SequenceEqual(Bytes(r.Session.Authority.FilePath)) && r.Processes.Starts == 0 && r.Processes.Stops == 0, "actual row activation preserves runtime ledger and execution"); window.Close();
        }
        using (var r = new FunctionalRig("review-ordinary-first-admission"))
        {
            r.Arm(); NormalSnapshot(r); var primary = Bytes(r.Store.FilePath); var ledger = Bytes(r.Session.Authority.FilePath);
            var hold = new TaskCompletionSource<bool>(); int ordinaryPrompts = 0, suspends = 0, finishes = 0;
            var ordinary = new ExitCoordinator(r.Session, () => { ordinaryPrompts++; return hold.Task; }, () => { }, () => finishes++, TimeSpan.Zero);
            var stopExit = new StopAllExitCoordinator(r.Session, ordinary, () => suspends++, () => { }, (_, _) => Task.FromResult(true));
            var pending = ordinary.Request(); Check(ordinary.ExitPending && ordinaryPrompts == 1, "ordinary leave-alive confirmation owns exit admission");
            Check(!await stopExit.Request() && !await ordinary.Request(), "ordinary-first blocks repeated and stop-exit admissions");
            Check(ordinaryPrompts == 1 && r.Interaction.Confirmations == 0 && suspends == 0 && finishes == 0 && !r.Session.ShuttingDown && r.Processes.Stops == 0 && r.Processes.Starts == 0, "ordinary-first creates only one prompt with zero effects or premature shutdown");
            Check(primary.SequenceEqual(Bytes(r.Store.FilePath)) && ledger.SequenceEqual(Bytes(r.Session.Authority.FilePath)) && r.Owner.Held, "pending ordinary exit leaves catalog authority owner unchanged");
            hold.SetResult(false); Check(!await pending && !ordinary.ExitPending && !r.Session.ShuttingDown, "ordinary cancellation releases shared exit admission");
        }
        foreach (bool reverse in new[] { false, true })
        {
            string script = Path.Combine(root, "adapter-" + reverse, "startup.ahk");
            var saved = new ProcessIdentity(7890, DateTime.UtcNow.ToFileTimeUtc(), Path.Combine(root, "AutoHotkey64.exe"), script, reverse ? null : false);
            var observed = saved with { Elevated = reverse ? false : null };
            var fake = new FixtureProcesses { Current = new([observed], true, "isolated held-stop adapter fixture") };
            var adapter = new NormalMigrationEffects(fake); var manifest = new NormalCutoverManifest { Fixture = true, Root = Path.GetDirectoryName(script)! }; var journal = new MigrationJournal { Supervisor = saved };
            var result = await adapter.Apply("SupervisorStopped", manifest, journal, CancellationToken.None);
            Check(result.Success && fake.Stops == 1 && fake.Starts == 0 && fake.Current.Processes.Count == 0, "actual migration adapter same core tolerates ancillary privilege change " + reverse);
        }
        foreach (var mismatch in new[] { "pid", "created", "exe", "script" })
        {
            string script = Path.Combine(root, "adapter-mismatch-" + mismatch, "startup.ahk"); var saved = new ProcessIdentity(7890, DateTime.UtcNow.ToFileTimeUtc(), Path.Combine(root, "AutoHotkey64.exe"), script, false);
            var observed = mismatch switch { "pid" => saved with { Pid = 7891 }, "created" => saved with { Created = saved.Created + 1 }, "exe" => saved with { Executable = Path.Combine(root, "other", "AutoHotkey64.exe") }, _ => saved with { Script = Path.Combine(root, "other", "startup.ahk") } };
            var fake = new FixtureProcesses { Current = new([observed], true, "isolated changed core") }; var adapter = new NormalMigrationEffects(fake);
            var result = await adapter.Apply("SupervisorStopped", new() { Fixture = true, Root = Path.GetDirectoryName(script)! }, new() { Supervisor = saved }, CancellationToken.None);
            // A different script is absent from this exact scoped role and remains untouched; other core mismatches refuse.
            Check((mismatch == "script" || !result.Success) && fake.Stops == 0 && fake.Starts == 0 && fake.Current.Processes.Single() == observed, "actual migration adapter never retargets changed core " + mismatch);
        }
    }

}
