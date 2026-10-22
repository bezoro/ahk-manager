using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    static CheckBox RowPreference(MainWindow window, Guid id, string content)
    {
        var row = window.ListControl.Items.Cast<ListBoxItem>().Single(item => item.Tag is Guid value && value == id);
        row.ApplyTemplate();
        // The cached row owns its checkbox; no detail accessor or direct session setter.
        return VisualChildren(row.Content as DependencyObject ?? row).OfType<CheckBox>().Single(box => Equals(box.Content, content));
    }
    static void ToggleRowPreference(CheckBox box, bool value)
    {
        box.IsChecked = value;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    static byte[] CatalogBytes(UiSession session) => JsonSerializer.SerializeToUtf8Bytes(session.CurrentCatalog, TestJson.Default.Catalog);
    static async Task ReadyRetry(FunctionalRig r)
    {
        r.Arm(); r.EmptyProcesses(); File.WriteAllText(r.Entry.Interpreter, "isolated fixture; never executed");
        var c = r.Store.Load();
        c.Recovery.Add(new() { ScriptId = r.Entry.Id, LoginKey = r.Owner.LoginKey, Failures = 3, Suspended = true, NextTryUtcTicks = r.Clock.UtcNow.Ticks,
            AttemptsUtcTicks = [r.Clock.UtcNow.AddMinutes(-11).Ticks, r.Clock.UtcNow.AddMinutes(-1).Ticks] });
        r.Store.Save(c); r.Reload(); await r.Session.Refresh();
    }
    static async Task RunDetailsSimplification()
    {
        using (var r = new FunctionalRig("details-pure"))
        {
            await ReadyRetry(r);
            var window = new MainWindow(r.Session);
            var retry = (Button)window.FindName("RetryButton");
            var before = CatalogBytes(r.Session); var primary = File.ReadAllBytes(r.Store.FilePath); var backup = File.ReadAllBytes(r.Store.FilePath + ".bak");
            var ledger = File.ReadAllBytes(r.Session.Authority.FilePath); int snapshots = r.Processes.SnapshotCalls;
            for (int i = 0; i < 20; i++) { Check(r.Session.RetryAvailability().Success, "existing suspended absent script can be released " + i); r.Session.CopyPath(); }
            Check(before.SequenceEqual(CatalogBytes(r.Session)) && primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && backup.SequenceEqual(File.ReadAllBytes(r.Store.FilePath + ".bak")) && ledger.SequenceEqual(File.ReadAllBytes(r.Session.Authority.FilePath)), "eligibility and Paint preserve catalog, recovery attempts, primary, backup and ledger bytes");
            Check(snapshots == r.Processes.SnapshotCalls && r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Recycler.Calls == 0 && r.Interaction.Confirmations == 0, "eligibility and Paint never query processes or cause effects");
            Check(window.FindName("EnableButton") is null && window.FindName("DisableButton") is null && window.FindName("AdministratorBox") is null, "duplicate detail controls absent from actual XAML tree");
            Check(retry.Visibility == Visibility.Visible && retry.IsEnabled, "useful recovery displays enabled Retry");
            Check(window.FocusControls.Select(c => c.TabIndex).SequenceEqual(Enumerable.Range(0, window.FocusControls.Count)) && window.FocusControls.All(c => AutomationProperties.GetName(c).Length > 0), "remaining static tab order and accessible names complete");
            window.DescriptionControl.Text = "rascunho preservado"; var selectedRow = window.ListControl.SelectedItem;
            r.Session.CopyPath();
            Check(window.DescriptionControl.Text == "rascunho preservado" && ReferenceEquals(selectedRow, window.ListControl.SelectedItem), "Retry repaint preserves selected cached row and description draft");
            WpfProgram.Render(window, Path.Combine(root, "details-retry-min-100.png"), 1000, 720, 1);
            WpfProgram.Render(window, Path.Combine(root, "details-retry-min-200.png"), 1000, 720, 2);
            Check(retry.ActualWidth > 0 && retry.ActualHeight > 0 && retry.TransformToAncestor((Visual)window.Content).Transform(new Point()).Y < 450, "useful Retry visible in minimum detail pane");
            var prefs = r.Session.CurrentCatalog;
            Click(retry);
            var after = r.Store.Load(); var budget = after.Recovery.Single();
            Check(!budget.Suspended && budget.Failures == 0 && budget.NextTryUtcTicks == prefs.Recovery.Single().NextTryUtcTicks && budget.AttemptsUtcTicks.SequenceEqual(prefs.Recovery.Single().AttemptsUtcTicks.Where(t => t >= r.Clock.UtcNow.AddMinutes(-10).Ticks)), "actual Retry click releases existing budget using unchanged cooldown and ten-minute policy");
            Check(JsonSerializer.Serialize(after.Runtime, TestJson.Default.LoginRuntime) == JsonSerializer.Serialize(prefs.Runtime, TestJson.Default.LoginRuntime) && after.Scripts.Zip(prefs.Scripts).All(pair => pair.First.StartWithWindows == pair.Second.StartWithWindows && pair.First.RunAsAdministrator == pair.Second.RunAsAdministrator), "Retry preserves runtime and next-login/admin preferences");
            Check(retry.Visibility == Visibility.Collapsed && r.Processes.Starts == 0 && r.Processes.Stops == 0, "healthy budget hides Retry and command has no immediate process effects");
            window.Close();
        }
        foreach (string reason in new[] { "missing-budget", "healthy", "stale-login", "cooldown", "three-at-boundary", "paused", "desired-false", "runtime-stale", "admin", "blocked", "external", "retired", "owner", "untrusted", "owner-login", "running", "duplicates", "unknown", "partial", "missing-file", "missing-interpreter", "invalid-interpreter", "absent-interpreter", "incomplete-migration", "owner-migration", "journal", "invalid-primary", "readonly", "authority", "binding", "selection", "shutdown", "stale-observation", "schema" })
        {
            using var r = new FunctionalRig("details-refuse-" + reason); await ReadyRetry(r);
            var c = r.Store.Load(); var e = c.Scripts[0]; var b = c.Recovery.Single();
            switch (reason)
            {
                case "missing-budget": c.Recovery.Clear(); break;
                case "healthy": b.Failures = 0; b.Suspended = false; break;
                case "stale-login": b.LoginKey += "other"; break;
                case "cooldown": b.NextTryUtcTicks = r.Clock.UtcNow.AddSeconds(1).Ticks; break;
                case "three-at-boundary": b.AttemptsUtcTicks = Enumerable.Repeat(r.Clock.UtcNow.AddMinutes(-10).Ticks, 3).ToList(); break;
                case "paused": c.Runtime.AutomaticPaused = true; break;
                case "desired-false": c.Runtime.Set(e.Id, false); break;
                case "runtime-stale": c.Runtime.LoginKey += "other"; break;
                case "admin": e.RunAsAdministrator = true; break;
                case "blocked": e.LaunchBlocked = true; break;
                case "external": e.ManagementMode = ManagementMode.ExternalPending; break;
                case "retired": e.ManagementMode = ManagementMode.Retired; e.StartWithWindows = false; break;
                case "missing-file": File.Delete(e.Path); break;
                case "missing-interpreter": e.Interpreter = ""; break;
                case "invalid-interpreter": e.Interpreter = Path.Combine(r.Directory, "wrong.exe"); break;
                case "absent-interpreter": File.Delete(e.Interpreter); break;
                case "incomplete-migration": c.Migration.State = MigrationState.Incomplete; break;
                case "owner-migration": c.Migration.OwnerSid += "other"; break;
                case "binding": c.Authority!.LedgerId = Guid.NewGuid(); break;
            }
            r.Store.Save(c); r.Reload();
            if (reason == "running" || reason == "duplicates") r.Processes.Current = new(Enumerable.Range(1, reason == "running" ? 1 : 2).Select(i => new ProcessIdentity(9000 + i, r.Clock.UtcNow.ToFileTimeUtc(), r.Entry.Interpreter, r.Entry.Path, false)).ToList(), true, "isolated");
            if (reason == "unknown") r.Processes.Current = new([new(9000, 0, "", null)], true, "isolated unknown");
            if (reason == "partial") r.Processes.Current = new([], false, "isolated partial");
            await r.Session.Refresh();
            switch (reason)
            {
                case "owner": r.Owner.Held = false; break;
                case "untrusted": r.Owner.Trusted = false; break;
                case "owner-login": r.Owner.LoginKey = ""; break;
                case "journal": File.AppendAllText(Path.Combine(r.Directory, "migration-journal.json"), " "); break;
                case "invalid-primary": File.AppendAllText(r.Store.FilePath, " "); _ = r.Store.VerifyPrimary(r.Session.CurrentCatalog); break;
                case "readonly": File.WriteAllText(r.Store.FilePath, "{"); r.Reload(); await r.Session.Refresh(); break;
                case "authority": File.AppendAllText(r.Session.Authority.FilePath, " "); _ = r.Session.Authority.Verify(r.Session.CurrentCatalog.Authority); break;
                case "selection": r.Session.Select(null); break;
                case "shutdown": await r.Session.PrepareExit(TimeSpan.FromMilliseconds(10)); break;
                case "stale-observation": r.Processes.ThrowSnapshot = true; await r.Session.Refresh(); break;
                case "schema": LegacyPayload(r.Store, r.Session.CurrentCatalog, 4); r.Reload(); await r.Session.Refresh(); break;
            }
            var before = CatalogBytes(r.Session); var primary = File.ReadAllBytes(r.Store.FilePath); int snapshots = r.Processes.SnapshotCalls;
            var window = new MainWindow(r.Session); var retry = (Button)window.FindName("RetryButton");
            Check(!r.Session.RetryAvailability().Success && retry.Visibility == Visibility.Collapsed, reason + " hides Retry");
            r.Session.RetryRecovery();
            Check(before.SequenceEqual(CatalogBytes(r.Session)) && primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && snapshots == r.Processes.SnapshotCalls && r.Processes.Starts == 0 && r.Processes.Stops == 0, reason + " direct command does not create/reset/save budget or cause effects");
            if (reason == "paused")
            {
                WpfProgram.Render(window, Path.Combine(root, "details-paused-min-150.png"), 1000, 720, 1.5);
                Check(((TextBlock)window.FindName("IntentText")).Text.Contains("suspensa") && ((TextBlock)window.FindName("PauseText")).Text.Length > 0, "hidden Retry retains suspension and automatic pause information");
            }
            window.Close();
        }
        foreach (string change in new[] { "owner", "admin", "pause", "selection" })
        {
            using var r = new FunctionalRig("details-inner-" + change); await ReadyRetry(r); var before = File.ReadAllBytes(r.Store.FilePath);
            r.Session.Changed += () => { if (!r.Session.Busy) return; if (change == "owner") r.Owner.Held = false; else if (change == "admin") r.Entry.RunAsAdministrator = true; else if (change == "pause") r.Owner.LoginKey += "changed"; else r.Session.SetSearch("cannot-match-while-busy"); };
            if (change == "selection") { r.Session.Select(null); }
            r.Session.RetryRecovery();
            Check(before.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && r.Processes.Starts == 0 && r.Processes.Stops == 0, change + " rechecked before/inside operation lane without saving");
        }
        using (var r = new FunctionalRig("details-busy"))
        {
            await ReadyRetry(r); r.Interaction.HoldConfirmation = new(); var pending = r.Session.RunBatch(BatchAction.Stop);
            Check(r.Session.Busy && !r.Session.RetryAvailability().Success, "shared lane hides Retry while batch confirmation pending");
            var before = File.ReadAllBytes(r.Store.FilePath); r.Session.RetryRecovery();
            Check(before.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), "Retry during occupied lane preserves existing budget");
            r.Interaction.HoldConfirmation.SetResult(false); await pending;
        }
        using (var r = new FunctionalRig("details-supervisor"))
        {
            await ReadyRetry(r); var supervisor = Path.Combine(r.Directory, "startup.ahk");
            var child = Path.Combine(r.Directory, "scripts", "startup", "child.ahk"); Directory.CreateDirectory(Path.GetDirectoryName(child)!); File.WriteAllText(child, "; never executed");
            var c = r.Store.Load(); c.Scripts[0].Path = child; r.Store.Save(c); r.Reload();
            r.Processes.Current = new([new(9911, r.Clock.UtcNow.ToFileTimeUtc(), r.Entry.Interpreter, supervisor)], true, "isolated", supervisor); await r.Session.Refresh();
            var before = File.ReadAllBytes(r.Store.FilePath); r.Session.RetryRecovery();
            Check(!r.Session.RetryAvailability().Success && before.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && r.Processes.Starts == 0, "active supervisor conflict refuses child recovery without retargeting");
        }
        using (var r = new ManualRig("details-pending-ledger"))
        {
            await r.Pending(); var c = r.Store.Load(); c.Scripts[0].RunAsAdministrator = false; c.Scripts[0].ManagementMode = ManagementMode.Normal;
            c.Runtime = new() { LoginKey = r.Owner.LoginKey, Decided = true }; c.Runtime.Set(c.Scripts[0].Id, true);
            var journal = new MigrationJournal { Fixture = true, ManifestHash = new('A',64), OwnerSid = r.Owner.Sid, State = MigrationState.Completed, AppliedSteps = [.. JournalStore.Steps] };
            c.Migration = new() { State = MigrationState.Completed, ManifestHash = journal.ManifestHash, OwnerSid = r.Owner.Sid, JournalDigest = JournalStore.Save(r.Directory,journal), CompletedUtcTicks = r.Clock.UtcNow.Ticks };
            c.Recovery.Add(new() { ScriptId = c.Scripts[0].Id, LoginKey = r.Owner.LoginKey, Failures = 1 }); File.WriteAllText(c.Scripts[0].Interpreter, "never executed");
            r.Store.Save(c); r.Reopen(); await r.Session.Refresh(); var before = File.ReadAllBytes(r.Store.FilePath); var ledger = File.ReadAllBytes(r.Ledger.FilePath);
            r.Session.RetryRecovery();
            Check(r.Ledger.HasPending && !r.Session.RetryAvailability().Success && before.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && ledger.SequenceEqual(File.ReadAllBytes(r.Ledger.FilePath)) && r.Processes.Starts == 0 && r.Processes.Stops == 0, "pending durable request hides Retry and preserves ledger without effects");
        }
        using (var r = new FunctionalRig("details-atomic-failure"))
        {
            await ReadyRetry(r); var window = new MainWindow(r.Session); var before = CatalogBytes(r.Session); var primary = File.ReadAllBytes(r.Store.FilePath);
            using (var locked = new FileStream(r.Store.FilePath + ".tmp", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) r.Session.RetryRecovery();
            Check(r.Session.Error && before.SequenceEqual(CatalogBytes(r.Session)) && primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), "Retry atomic save refusal retains committed recovery and truthful error");
            Check(((Button)window.FindName("RetryButton")).Visibility == Visibility.Collapsed && r.Processes.Starts == 0 && r.Processes.Stops == 0, "failed durable save hides Retry and starts no script"); window.Close();
        }
        using (var r = new FunctionalRig("details-late-primary"))
        {
            await ReadyRetry(r); File.AppendAllText(r.Store.FilePath, " "); var late = File.ReadAllBytes(r.Store.FilePath);
            r.Session.RetryRecovery();
            Check(late.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && r.Session.CurrentCatalog.Recovery.Single().Suspended && !r.Store.PrimaryValid, "fresh authoritative Gate refuses late primary bytes before Retry copy/save");
        }
        using (var r = new FunctionalRig("details-recovery-policy"))
        {
            r.Arm(); r.EmptyProcesses(); File.WriteAllText(r.Entry.Interpreter, "isolated fixture; never executed"); r.Processes.FailStart = true;
            await r.Session.Reconcile(); r.Session.RetryRecovery();
            Check(r.Processes.Starts == 1 && r.Store.Load().Recovery.Single().Failures == 1 && !r.Session.RetryAvailability().Success, "affected functional recovery cannot bypass sixty-second cooldown");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(60); await r.Session.Reconcile();
            Check(r.Processes.Starts == 2 && r.Store.Load().Recovery.Single().NextTryUtcTicks == r.Clock.UtcNow.AddSeconds(120).Ticks, "second recovery failure keeps 120-second cooldown");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(120); await r.Session.Reconcile();
            Check(r.Processes.Starts == 3 && r.Store.Load().Recovery.Single().Suspended, "third failure suspends recovery");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(240); r.Session.RetryRecovery(); await r.Session.Reconcile();
            Check(r.Processes.Starts == 3 && !r.Session.RetryAvailability().Success, "three attempts within ten minutes forbid fourth start including Retry");
            r.Clock.UtcNow = r.Clock.UtcNow.AddMinutes(10); Check(r.Session.RetryAvailability().Success, "expired attempt window makes existing suspended recovery useful");
            r.Session.RetryRecovery(); await r.Session.Reconcile(); Check(r.Processes.Starts == 4, "explicit release after window permits one later timer attempt");
        }
        using (var r = new FunctionalRig("details-autostart-row"))
        {
            r.Arm(); r.EmptyProcesses(); await r.Session.Refresh(); var window = new MainWindow(r.Session); var runtime = JsonSerializer.Serialize(r.Session.CurrentCatalog.Runtime, TestJson.Default.LoginRuntime);
            var row = window.ListControl.SelectedItem; var box = RowPreference(window, r.Entry.Id, "Iniciar com o Windows"); ToggleRowPreference(box, false);
            Check(r.Store.Load().Scripts[0].StartWithWindows == false && box.IsChecked == false && runtime == JsonSerializer.Serialize(r.Session.CurrentCatalog.Runtime, TestJson.Default.LoginRuntime), "actual row autostart Click changes next-login choice only");
            Check(ReferenceEquals(row, window.ListControl.SelectedItem) && r.Processes.Starts == 0 && r.Processes.Stops == 0, "row identity retained and autostart Click has zero process effects"); window.Close();
        }
    }
}

