using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    static int checks;
    static string root = "";
    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);yield return child;
            foreach(var nested in VisualChildren(child)) yield return nested;
        }
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--brush-task-probe") { var p = new WindowsBrushTask().Probe(); Console.WriteLine($"Known={p.Known}; TaskEnabled={p.TaskEnabled}; AllowDemandStart={p.DemandStart}; LogonEnabled={p.LogonEnabled}; {p.Notice}"); return p.Ready ? 0 : 1; }
        if (args.Length == 3 && args[0] == "--brush-worker") return RunBrushWorker(args).GetAwaiter().GetResult();
        if (args.Length == 5 && args[0] == "--reopen-worker") return RunReopenWorker(args).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] is "--normal-cutover" or "--normal-state-guard")
        {
            root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
            try { if (args[0] == "--normal-cutover") RunNormalCutover().GetAwaiter().GetResult(); else RunNormalProfileGuard(); Console.WriteLine($"Normal checks={checks} PASS; all effects isolated, no native AHK/Startup/task calls"); return 0; }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "--brush-register-xml")
        {
            root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
            try { RunBrushRegisterXml().GetAwaiter().GetResult();Console.WriteLine($"Brush register XML checks={checks} PASS; pure/fake only, no COM/task/UAC/process calls");return 0; }
            catch(Exception ex){Console.WriteLine(ex);return 1;}
        }
        bool brushStoppedRecovery = args.Length == 2 && args[0] is "--brush-stopped-recovery" or "--brush-stopped-recovery-barriers";
        bool brushRegisterFormatting = args.Length == 2 && args[0] is "--brush-register-formatting" or "--brush-register-formatting-settlement";
        bool brushRegisterRecovery = args.Length == 2 && args[0] == "--brush-register-recovery";
        bool brushConfirmationFinalRead = args.Length == 2 && args[0] == "--brush-confirmation-final-read";
        bool brushConfirmationLegacy = args.Length == 2 && args[0] == "--brush-confirmation-legacy";
        bool brushConfirmation = args.Length == 2 && args[0] == "--brush-confirmation";
        bool brushReview = args.Length == 2 && args[0] == "--brush-review-fixes";
        bool brushUiFinal = args.Length == 2 && args[0] == "--brush-ui-final";
        bool brushFinal = args.Length == 2 && args[0] == "--brush-final";
        bool brush = args.Length == 2 && args[0] == "--brush-tasks";
        bool retired = args.Length == 2 && args[0] == "--retired-visibility";
        bool receipts = args.Length == 2 && args[0] == "--reopen-receipts";
        bool reopen = args.Length == 2 && args[0] == "--reopen-existing";
        bool details = args.Length == 2 && args[0] == "--details-simplification";
        bool assignedReview = args.Length == 2 && args[0] == "--integrated-review-fixes";
        bool integrated = args.Length == 2 && args[0] == "--integrated";
        bool bootSupplement = args.Length == 2 && args[0] == "--boot-supplement";
        bool bootRecovery = args.Length == 2 && args[0] == "--boot-recovery";
        bool fixtureBoundary = args.Length == 2 && args[0] == "--fixture-boundary-review";
        bool manualSafety = args.Length == 2 && args[0] == "--manual-safety";
        bool manualRender = args.Length == 2 && args[0] == "--manual-render";
        bool manualUac = args.Length == 2 && args[0] == "--manual-uac";
        bool ledgerSchema = args.Length == 2 && args[0] == "--ledger-schema";
        bool fingerprintReview = args.Length == 2 && args[0] == "--elevation-fingerprint-review";
        bool elevationSchemaReview = args.Length == 2 && args[0] == "--elevation-schema-review";
        bool elevationRender = args.Length == 2 && args[0] == "--elevation-render";
        bool elevation = args.Length == 2 && args[0] == "--elevation";
        bool exitReview = args.Length == 2 && args[0] == "--exit-review";
        bool reviewDelta = args.Length == 2 && args[0] == "--functional-review";
        bool trayLayout = args.Length == 2 && args[0] == "--tray-abi";
        bool functional = args.Length == 2 && args[0] == "--functional";
        bool focused = args.Length == 2 && args[0] == "--review-fixes";
        if (!brushStoppedRecovery && !brushRegisterFormatting && !brushRegisterRecovery && !brushConfirmationFinalRead && !brushConfirmationLegacy && !brushConfirmation && !brushReview && !brushUiFinal && !brushFinal && !brush && !retired && !receipts && !reopen && !details && !focused && !functional && !trayLayout && !reviewDelta && !exitReview && !elevation && !elevationRender && !elevationSchemaReview && !fingerprintReview && !manualUac && !ledgerSchema && !manualRender && !manualSafety && !fixtureBoundary && !bootRecovery && !bootSupplement && !integrated && !assignedReview && args.Length != 1) return 2;
        root = Path.GetFullPath(args[brushStoppedRecovery || brushRegisterFormatting || brushRegisterRecovery || brushConfirmationFinalRead || brushConfirmationLegacy || brushConfirmation || brushReview || brushUiFinal || brushFinal || brush || retired || receipts || reopen || details || focused || functional || trayLayout || reviewDelta || exitReview || elevation || elevationRender || elevationSchemaReview || fingerprintReview || manualUac || ledgerSchema || manualRender || manualSafety || fixtureBoundary || bootRecovery || bootSupplement || integrated || assignedReview ? 1 : 0]); Directory.CreateDirectory(root);
        var app = WpfProgram.CreateApplication(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            try { if (brushStoppedRecovery) await RunBrushStoppedRecovery(args[0] == "--brush-stopped-recovery-barriers"); else if (brushRegisterFormatting) await RunBrushRegisterFormatting(args[0] == "--brush-register-formatting-settlement"); else if (brushRegisterRecovery) await RunBrushRegisterRecovery(); else if (brushConfirmationFinalRead) await RunBrushConfirmationFinalRead(); else if (brushConfirmationLegacy) await RunBrushConfirmationLegacy(); else if (brushConfirmation) await RunBrushConfirmation(); else if (brushReview) await RunBrushReviewFixes(); else if (brushUiFinal) await RunBrushUiFinal(); else if (brushFinal) await RunBrushFinal(); else if (brush) await RunBrushTasks(); else if (retired) await RunRetiredVisibility(); else if (receipts) await RunReopenReceiptChecks(); else if (reopen) { await RunReopenExisting(); await RunExitReview(); } else if (details) { await RunDetailsSimplification(); await RunElevationPreference(); await RenderElevation(); } else if (assignedReview) await RunIntegratedReviewFixes(); else if (integrated) await RunIntegrated(); else if (bootSupplement) await RunBootSupplement(); else if (bootRecovery) await RunBootRecovery(); else if (fixtureBoundary) await RunFixtureBoundaryReview(); else if (manualSafety) await RunManualSafety(); else if (manualRender) await RunManualRender(); else if (manualUac) await RunManualUac(); else if (ledgerSchema) await RunLedgerSchema(); else if (fingerprintReview) await RunElevationFingerprintReview(); else if (elevationSchemaReview) await RunElevationSchemaReview(); else if (elevationRender) await RenderElevation(); else if (elevation) await RunElevation(); else if (exitReview) await RunExitReview(); else if (reviewDelta) await RunFunctionalReview(); else if (trayLayout) Check(TrayHost.NativeDataSize == (IntPtr.Size == 8 ? 976 : 956), "NOTIFYICONDATAW full Windows ABI layout"); else if (functional) await RunFunctional(); else if (focused) await RunReviewFixes(); else await Run(); Console.WriteLine($"UI checks={checks} PASS; native keyboard/monitor DPI not executed"); }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); exit = 1; }
            finally { app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    static async Task Run()
    {
        var dir = Path.Combine(root, "behavior"); using var store = new CatalogStore(dir);
        var fixture = FixtureFactory.Create(store, dir, "normal"); var session = fixture.Session;
        await session.Refresh(); var window = new MainWindow(session);
        var first = session.Entries[0]; var second = session.Entries[1]; var missing = session.Entries[2];
        Check(session.SelectedId == first.Id && window.ListControl.SelectedItem is ListBoxItem, "initial selected identity");
        Check(session.State(first).Contains("Rodando"), "verified current process text");
        Check(session.ExternalState(second) == "Tarefa elevada" && session.Snapshot.For(second.Path).Count == 0, "external elevation never invents process");
        window.SearchControl.Text = "brush";
        Check(session.Visible.Count == 1 && session.Selected is null, "search hides selection without choosing other script");
        window.SearchControl.Text = ""; Check(session.SelectedId == first.Id && session.Selected?.Id == first.Id, "selection restored by ID");
        window.SearchControl.Text = "harmless-scripts"; Check(session.Visible.Count == 3, "search full paths uses domain matches");
        window.SearchControl.Text = "none-found"; Check(window.ListControl.Items.Count == 0 && session.Selected is null, "empty search has no action target");
        window.SearchControl.Text = "";
        Check(AutomationProperties.GetName(window.SearchControl).Length > 0 && AutomationProperties.GetName(window.ListControl).Length > 0, "search and list accessibility names");
        Check(window.SearchControl.Focusable && window.ListControl.Focusable && window.DescriptionControl.Focusable && KeyboardNavigation.GetTabIndex(window.SearchControl) < KeyboardNavigation.GetTabIndex(window.ListControl), "focus capability and explicit initial tab order");
        Check(window.FocusControls.Select(c => c.TabIndex).SequenceEqual(Enumerable.Range(0,window.FocusControls.Count)), "complete typed control tab order");
        Check(window.FocusControls.All(c => AutomationProperties.GetName(c).Length > 0), "all main focus controls have accessibility names");
        Check(!window.ActionButton(ScriptAction.Start).IsEnabled && !window.ActionButton(ScriptAction.Recycle).IsEnabled && window.ActionButton(ScriptAction.Stop).IsEnabled && window.PolicyControl.Text.Contains("Iniciar:"), "policy disables duplicate start and recycle with visible reason");
        Check(((SolidColorBrush)window.ActionButton(ScriptAction.Start).Foreground).Color == ((SolidColorBrush)window.ActionButton(ScriptAction.Recycle).Foreground).Color, "all disabled action labels share muted style");
        WpfProgram.Render(window,Path.Combine(root,"disabled-style-regression.png"),1000,720,1);
        Check(VisualChildren(window.ActionButton(ScriptAction.Recycle)).OfType<TextBlock>().Any(t => ((SolidColorBrush)t.Foreground).Color == ((SolidColorBrush)window.ActionButton(ScriptAction.Start).Foreground).Color), "rendered plain-text disabled label inherits muted foreground");
        await session.Act(ScriptAction.Stop);
        Check(fixture.Processes.Stops == 0 && !session.Busy && session.Status.StartsWith("Cancelado"), "stop confirmation cancellation prevents effect");
        var dialog = new ConfirmationWindow("Confirmar ação", "Fixture");
        Check(dialog.CancelButton.IsDefault && dialog.CancelButton.IsCancel && !dialog.ConfirmButton.IsDefault, "confirmation Enter/Esc default Cancel"); dialog.Close();
        session.Select(second.Id); session.SetBlocked(false);
        fixture.Processes.Current = new([], true, "Fixture"); await session.Refresh();
        Check(window.ActionButton(ScriptAction.Start).IsEnabled, "selected idle script enables start via same policy");
        fixture.Processes.HoldStart = new TaskCompletionSource();
        Click(window.ActionButton(ScriptAction.Start));
        Check(session.Busy && fixture.Processes.Starts == 1, "actual start click enters shared async command");
        Check(!window.SearchControl.IsEnabled && !window.ListControl.IsEnabled && !window.DescriptionControl.IsEnabled && !window.ActionButton(ScriptAction.Restart).IsEnabled, "busy blocks search selection metadata and commands");
        session.Select(first.Id); session.SetSearch("desktop"); session.SaveDescription("should not save"); session.Edit(); session.Register();
        Click(window.ActionButton(ScriptAction.Start));
        Check(session.SelectedId == second.Id && session.Search == "" && fixture.Processes.Starts == 1 && fixture.Interaction.Edited is null, "repeated/concurrent actions and selection changes refused while busy");
        Check(!session.CanClose(), "close refused while busy");
        fixture.Processes.HoldStart.SetResult(); await Dispatcher.Yield(DispatcherPriority.Background);
        Check(!session.Busy && session.CanClose() && Paths.Equal(fixture.Processes.LastStarted, second.Path), "busy released and captured target retained after completion");
        fixture.Interaction.Confirmed = true; fixture.Interaction.HoldConfirmation = new TaskCompletionSource<bool>();
        var heldStop = session.Act(ScriptAction.Stop); session.Select(first.Id); session.SetSearch("desktop");
        Check(session.Busy && session.SelectedId == second.Id && session.Search == "", "modal await captures identity and freezes selection");
        fixture.Interaction.HoldConfirmation.SetResult(false); await heldStop; fixture.Interaction.HoldConfirmation = null;
        Check(fixture.Processes.Stops == 0 && !session.Busy, "cancel/X/Esc result does not call controller");
        var scriptBytes = File.ReadAllBytes(second.Path);
        session.SaveDescription("Descrição informada pelo usuário");
        Check(session.Selected?.DescriptionSource == "Informada" && store.Load().Scripts.Single(e => e.Id == second.Id).Description.Contains("usuário"), "explicit description source persisted");
        Check(scriptBytes.SequenceEqual(File.ReadAllBytes(second.Path)), "description save never touches script");
        session.SetBlocked(true); Check(session.Selected!.LaunchBlocked && !session.Availability(ScriptAction.Start).Success, "local block stored independently of external task");
        Check(session.Discovery.Sources.Any(s => Paths.Equal(s.Path, second.Path) && s.Elevated), "local block keeps external source metadata");
        session.SetBlocked(false); fixture.Processes.Current = new([], true, "Fixture"); await session.Refresh();
        fixture.Interaction.ScriptChoice = second.Path; session.Register(); Check(session.Entries.Count == 3 && session.SelectedId == second.Id, "duplicate manual registration selects existing ID");
        var addedPath = Path.Combine(dir, "new-script.ahk"); File.WriteAllText(addedPath, "; harmless fixture\n");
        fixture.Interaction.ScriptChoice = addedPath; session.Register();
        Check(session.Entries.Count == 4 && session.Selected?.DescriptionSource == "Inferida" && fixture.Processes.Starts == 1, "manual registration infers description without launch");
        session.Edit(); session.CopyPath();
        Check(Paths.Equal(fixture.Interaction.Edited, addedPath) && Paths.Equal(fixture.Interaction.Copied, addedPath), "edit and copy use injected UI boundary with captured canonical path");
        fixture.Interaction.InterpreterChoice = "wrong.exe"; session.ChooseInterpreter(); Check(session.Error, "invalid interpreter rejected");
        fixture.Interaction.InterpreterChoice = Path.Combine(dir, "AutoHotkey64.exe"); session.ChooseInterpreter(); Check(session.Selected!.Interpreter.EndsWith("AutoHotkey64.exe"), "explicit interpreter metadata selection");
        fixture.Interaction.Confirmed = false; await session.Act(ScriptAction.Recycle);
        Check(fixture.Recycler.Calls == 0 && File.Exists(addedPath), "recycle cancellation preserves file and metadata");
        fixture.Interaction.Confirmed = true; await session.Act(ScriptAction.Recycle);
        Check(fixture.Recycler.Calls == 1 && session.Entries.Any(e => Paths.Equal(e.Path, addedPath)) && File.Exists(addedPath), "recycle refusal preserves metadata and file");
        fixture.Processes.ThrowSnapshot = true; await session.Refresh();
        Check(!session.Busy && session.Error && session.Entries.Count == 4, "unexpected refresh error releases busy preserves catalog");
        await session.Act(ScriptAction.Start); Check(!session.Busy && session.Error, "unexpected action error releases busy");
        fixture.Processes.ThrowSnapshot = false;
        session.Select(missing.Id); await session.Refresh(); Check(session.State(missing) == "Arquivo ausente" && !window.ActionButton(ScriptAction.Start).IsEnabled, "missing file state and disabled action");
        fixture.Processes.Current = new([new(999, 0, "", null)], false, "Inacessível"); await session.Refresh();
        session.Select(first.Id); Check(session.Snapshot.Unknown && !window.ActionButton(ScriptAction.Start).IsEnabled && window.PolicyControl.Text.Contains("inacessível"), "unknown process visible with fail-safe hint");
        var supervisorPath = Path.Combine(dir, "startup.ahk"); var childPath = Path.Combine(dir, "scripts", "startup", "child.ahk"); Directory.CreateDirectory(Path.GetDirectoryName(childPath)!); File.WriteAllText(childPath, "; fixture");
        fixture.Interaction.ScriptChoice = childPath; session.Register(); var child = session.Selected!;
        fixture.Processes.Current = new([new(21, DateTime.UtcNow.ToFileTimeUtc(), first.Interpreter, supervisorPath), new(22, DateTime.UtcNow.ToFileTimeUtc(), first.Interpreter, childPath)], true, "Fixture", supervisorPath); await session.Refresh();
        Check(!window.ActionButton(ScriptAction.Restart).IsEnabled && session.SupervisorWarning(child).Contains("60 segundos"), "supervisor blocks child restart with relaunch warning");
        fixture.Interaction.Confirmed = false; await session.Act(ScriptAction.Stop); Check(fixture.Interaction.LastConfirmation.Contains("60 segundos"), "stop confirmation includes supervisor warning");
        window.Close();
        using(var readonlyStore = new CatalogStore(Path.Combine(root,"readonly")))
        {
            var readonlyOwner = new FixtureOwner(); var readonlyAuthority = RequestLedger.OpenFixture(Path.Combine(readonlyStore.DirectoryPath,"fixture-authority"),readonlyOwner);
            readonlyStore.Save(new() { Authority = readonlyAuthority.Binding, Scripts = [first.Copy()] }); readonlyStore.Save(new() { Authority = readonlyAuthority.Binding, Scripts = [first.Copy()] }); File.WriteAllText(readonlyStore.FilePath, "{");
            var readonlySession = new UiSession(readonlyStore, new FixtureProcesses(), new FixtureRecycler(), new FixtureInteraction(), () => Task.FromResult(new DiscoveryResult([], [], [])), new FixtureOwner(), new FixtureClock());
            var readonlyWindow = new MainWindow(readonlySession); readonlySession.SaveDescription("no write");
            Check(readonlySession.ReadOnly && !readonlyWindow.DescriptionControl.IsEnabled && readonlySession.Selected?.Description != "no write", "readonly catalog disables metadata and preserves backup"); readonlyWindow.Close();
        }
        await RenderScenarios();
    }
    static async Task RunReviewFixes()
    {
        var directory=Path.Combine(root,"drafts-rows");using var store=new CatalogStore(directory);
        var fixture=FixtureFactory.Create(store,directory,"normal");var session=fixture.Session;await session.Refresh();var window=new MainWindow(session);
        var first=session.Entries[0];var second=session.Entries[1];
        var firstRow=(ListBoxItem)window.ListControl.Items[0];var secondRow=(ListBoxItem)window.ListControl.Items[1];
        string draft="Rascunho digitado, ainda não salvo.";
        window.DescriptionControl.Text=draft;session.CopyPath();
        Check(window.DescriptionControl.Text==draft && session.Selected!.Description!=draft,"typed draft survives CopyPath without persistence");
        Check(ReferenceEquals(firstRow,window.ListControl.Items[0]),"status repaint retains row instance");
        FocusManager.SetFocusedElement(window,firstRow);
        session.CopyPath();
        Check(ReferenceEquals(FocusManager.GetFocusedElement(window),firstRow),"logical focus survives status repaint");
        await session.Act(ScriptAction.Stop);
        Check(window.DescriptionControl.Text==draft && fixture.Processes.Stops==0,"cancelled confirmation preserves draft and process");
        Check(ReferenceEquals(firstRow,window.ListControl.SelectedItem),"cancel and busy repaints preserve selected row instance");
        window.ListControl.SelectedItem=secondRow;window.DescriptionControl.Text="Segundo rascunho independente";
        window.ListControl.SelectedItem=firstRow;
        Check(session.SelectedId==first.Id && window.DescriptionControl.Text==draft,"selection away/back restores draft by ID");
        window.ListControl.SelectedItem=secondRow;
        Check(window.DescriptionControl.Text=="Segundo rascunho independente" && ReferenceEquals(secondRow,window.ListControl.SelectedItem),"second draft and selected control instance retained");
        window.ListControl.SelectedItem=firstRow;
        using(var denied=new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            session.SaveDescription(window.DescriptionControl.Text);
            Check(session.Error && window.DescriptionControl.Text==draft,"persist failure preserves typed draft");
            Check(store.Load().Scripts.Single(e=>e.Id==first.Id).Description!=draft,"persist failure does not claim saved text");
        }
        session.CopyPath();Check(window.DescriptionControl.Text==draft,"draft survives subsequent repaint after failure");
        string scriptBefore=File.ReadAllText(first.Path);session.SaveDescription(window.DescriptionControl.Text);
        Check(!session.Error && session.Selected!.Description==draft && session.Selected.DescriptionSource=="Informada","confirmed save commits only chosen draft");
        Check(File.ReadAllText(first.Path)==scriptBefore,"confirmed description save leaves script unchanged");
        window.ListControl.SelectedItem=secondRow;
        Check(window.DescriptionControl.Text=="Segundo rascunho independente","saving one ID preserves other ID draft");
        session.SetSearch("desktop");session.SetSearch("");
        Check(ReferenceEquals(firstRow,window.ListControl.Items[0]) && ReferenceEquals(secondRow,window.ListControl.Items[1]),"filter reconciliation reuses cached controls by ID");
        session.Select(second.Id);session.SetBlocked(false);fixture.Processes.Current=new([],true,"Fixture");await session.Refresh();
        fixture.Processes.HoldStart=new();var pending=session.Act(ScriptAction.Start);
        Check(session.Busy && ReferenceEquals(firstRow,window.ListControl.Items[0]) && ReferenceEquals(secondRow,window.ListControl.Items[1]),"busy repaint keeps all row instances");
        Check(window.DescriptionControl.Text=="Segundo rascunho independente","busy repaint preserves draft");
        await Render(window,"draft-busy-min",true,1);
        fixture.Processes.HoldStart.SetResult();await pending;
        Check(!session.Busy && ReferenceEquals(secondRow,window.ListControl.SelectedItem),"completion updates row data without rebuilding selection");
        window.DescriptionControl.Text="Rascunho final para conferência visual";session.CopyPath();await Render(window,"draft-min",true,1);window.Close();
        foreach(var action in new[]{ScriptAction.Start,ScriptAction.Stop,ScriptAction.Recycle})
        {
            string name=action.ToString().ToLowerInvariant();string actionDir=Path.Combine(root,"postquery-"+name);using var actionStore=new CatalogStore(actionDir);
            var current=FixtureFactory.Create(actionStore,actionDir,"normal");await current.Session.Refresh();
            if(action is ScriptAction.Start or ScriptAction.Recycle)current.Session.Select(current.Session.Entries[1].Id);
            if(action==ScriptAction.Start){current.Session.SetBlocked(false);current.Processes.Current=new([],true,"Fixture");await current.Session.Refresh();}
            current.Interaction.Confirmed=true;current.Recycler.Outcome=Result.Ok("Fixture: reciclagem simulada concluída.");
            current.Processes.ThrowSnapshotAt=current.Processes.SnapshotCalls+2;
            var actionWindow=new MainWindow(current.Session);await current.Session.Act(action);
            string resultText=action switch{ScriptAction.Start=>"Fixture: início simulado",ScriptAction.Stop=>"Fixture: parada simulada",_=>"Fixture: reciclagem simulada concluída"};
            Check(current.Session.Status.Contains(resultText) && current.Session.Status.Contains("Atualização falhou"),"effect result preserved after query failure "+name);
            Check(current.Session.ObservationStale && !current.Session.Snapshot.Complete && current.Session.State(current.Session.Selected!).Contains("Estado anterior"),"observation explicitly stale after effect "+name);
            Check(!current.Session.Busy && current.Session.Error && !current.Session.Status.Contains("Nenhuma"),"post-effect error releases busy without false neutrality "+name);
            Check(action switch{ScriptAction.Start=>current.Processes.Starts==1,ScriptAction.Stop=>current.Processes.Stops==1,_=>current.Recycler.Calls==1},"exactly one fake effect "+name);
            Check(!current.Session.Availability(ScriptAction.Start).Success,"stale snapshot does not permit duplicate start "+name);
            if(action==ScriptAction.Stop)await Render(actionWindow,"postquery-stale-min",true,1);
            current.Processes.ThrowSnapshotAt=null;await current.Session.Refresh();
            Check(!current.Session.ObservationStale && current.Session.Snapshot.Complete,"explicit refresh recovers observation "+name);actionWindow.Close();
        }
    }
    static async Task RenderScenarios()
    {
        foreach(double scale in new[] {1.0,1.5,2.0}) foreach(bool small in new[]{false,true})
        {
            string name=$"normal-{(small?"1000x720":"1220x800")}-{scale*100:0}";
            using var store=new CatalogStore(Path.Combine(root,name));var fixture=FixtureFactory.Create(store,Path.Combine(root,name),"normal"); await fixture.Session.Refresh();var window=new MainWindow(fixture.Session);
            await Render(window,name,small,scale);window.Close();
        }
        foreach(string scenario in new[]{"stress","unknown","empty","error","busy"})
        {
            using var store=new CatalogStore(Path.Combine(root,scenario));var fixture=FixtureFactory.Create(store,Path.Combine(root,scenario),scenario);await fixture.Session.Refresh();var window=new MainWindow(fixture.Session);Task? pending=null;
            if(scenario=="error"){fixture.Interaction.ScriptChoice="wrong-path.ahk";fixture.Session.Register();}
            if(scenario=="busy"){fixture.Session.Select(fixture.Session.Entries[1].Id);fixture.Session.SetBlocked(false);fixture.Processes.Current=new([],true,"Fixture");await fixture.Session.Refresh();fixture.Processes.HoldStart=new();pending=fixture.Session.Act(ScriptAction.Start);}
            await Render(window,scenario+"-1000x720-100",true,1);
            if(pending is not null){fixture.Processes.HoldStart!.SetResult();await pending;}window.Close();
        }
    }
    static Task Render(MainWindow window,string name,bool small,double scale)
    {
        double width=small?1000:1220,height=small?720:800;
        WpfProgram.Render(window,Path.Combine(root,"renders",name+".png"),width,height,scale);
        var content=(FrameworkElement)window.Content;var actions=window.FixedActions;var bounds=actions.TransformToAncestor(content).TransformBounds(new Rect(actions.RenderSize));
        Check(actions.ActualWidth>=320 && bounds.Bottom<=height && bounds.Top>0,"fixed actions fit "+name);
        Check(ScrollViewer.GetHorizontalScrollBarVisibility(window.ListControl)==ScrollBarVisibility.Disabled,"no horizontal list scroll "+name);
        Check(window.ActionButton(ScriptAction.Recycle).ActualHeight>=34 && window.ActionButton(ScriptAction.Start).ActualWidth>70,"action sizes "+name);
        Console.WriteLine($"RENDER {name} pixels={(int)(width*scale)}x{(int)(height*scale)} actionsBottom={bounds.Bottom:0.0}");return Task.CompletedTask;
    }
}
