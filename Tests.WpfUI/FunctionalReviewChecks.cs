using System.IO;
using System.Text.Json;
using System.Windows;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    static async Task RunFunctionalReview()
    {
        using (var r = new FunctionalRig("manual-budget"))
        {
            r.Arm(); r.EmptyProcesses(); await r.Session.Refresh(); r.Processes.FailStart = true;
            bool durable = false; r.Processes.BeforeStart = () => durable = r.Store.Load().Recovery[0].AttemptsUtcTicks.Count == 1;
            await r.Session.Act(ScriptAction.Start);
            Check(durable && r.Processes.Starts == 1, "managed manual Start reserves durably before effect");
            Check(r.Store.Load().Recovery[0].Failures == 1 && r.Store.Load().Recovery[0].NextTryUtcTicks == r.Clock.UtcNow.AddSeconds(60).Ticks, "manual failure records common cooldown60");
            await r.Session.Act(ScriptAction.Start); await r.Session.Reconcile();
            Check(r.Processes.Starts == 1, "manual and automatic both respect persisted cooldown");
            r.Reload(); await r.Session.Refresh(); await r.Session.Act(ScriptAction.Start);
            Check(r.Processes.Starts == 1, "manual reopen same login cannot reset budget");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(60); await r.Session.Act(ScriptAction.Start);
            Check(r.Processes.Starts == 2 && r.Store.Load().Recovery[0].Failures == 2, "manual second attempt counted");
            r.Clock.UtcNow = r.Clock.UtcNow.AddSeconds(120); await r.Session.Act(ScriptAction.Start);
            Check(r.Processes.Starts == 3 && r.Store.Load().Recovery[0].Suspended, "manual third failure suspends common recovery");
            r.Clock.UtcNow = r.Clock.UtcNow.AddMinutes(10); await r.Session.Act(ScriptAction.Start);
            Check(r.Processes.Starts == 3, "manual Start cannot bypass suspension after cooldown elapsed");
            r.Processes.Current = new([new(4500, DateTime.UtcNow.ToFileTimeUtc(), r.Entry.Interpreter, r.Entry.Path)], true, "Owned fixture"); await r.Session.Refresh();
            r.Interaction.Confirmed = true; await r.Session.Act(ScriptAction.Restart);
            Check(r.Processes.Stops == 0 && r.Processes.Starts == 3 && r.Interaction.Confirmations == 0, "suspended Restart preserves healthy process before confirmation/Stop");
            var window = new MainWindow(r.Session);
            Check(!window.ActionButton(ScriptAction.Restart).IsEnabled && window.PolicyControl.Text.Contains("suspensa", StringComparison.Ordinal), "managed manual suspension visible in UI policy");
            WpfProgram.Render(window, Path.Combine(root,"renders","manual-suspended-min.png"),1000,720,1);
            WpfProgram.Render(window, Path.Combine(root,"renders","manual-suspended-large.png"),1220,800,1.5); window.Close();
        }
        using (var r = new FunctionalRig("restart-reserve"))
        {
            r.Arm(); await r.Session.Refresh(); r.Interaction.Confirmed = true;
            bool beforeStop = false; r.Processes.BeforeStop = () => beforeStop = r.Store.Load().Recovery[0].AttemptsUtcTicks.Count == 1;
            await r.Session.Act(ScriptAction.Restart);
            Check(beforeStop && r.Processes.Stops == 1 && r.Processes.Starts == 1, "managed Restart reserves before stopping healthy instance");
            Check(r.Store.Load().Recovery[0].AttemptsUtcTicks.Count == 1 && r.Store.Load().Recovery[0].Failures == 0, "managed Restart records successful common outcome");
        }
        foreach (var gate in new[] {"journal", "migration", "unknown", "owner", "primary", "cooldown"})
        {
            using var r = new FunctionalRig("manual-gate-"+gate); r.Arm(); await r.Session.Refresh(); r.Interaction.Confirmed = true;
            if (gate == "journal") File.WriteAllText(JournalStore.PathFor(r.Directory),"{bad}");
            if (gate == "migration") { var c=r.Store.Load(); c.Migration.State=MigrationState.Incomplete;r.Store.Save(c);r.Reload();await r.Session.Refresh(); }
            if (gate == "unknown") {r.Processes.Current=new([new(99,0,"",null)],true,"unknown");await r.Session.Refresh();}
            if (gate == "owner") r.Owner.Held=false;
            if (gate == "primary") File.WriteAllText(r.Store.FilePath,"{bad}");
            if (gate == "cooldown") {var c=r.Store.Load();c.Recovery.Add(new(){ScriptId=r.Entry.Id,LoginKey=r.Owner.LoginKey,NextTryUtcTicks=r.Clock.UtcNow.AddSeconds(60).Ticks,Failures=1,AttemptsUtcTicks=[r.Clock.UtcNow.Ticks]});r.Store.Save(c);r.Reload();await r.Session.Refresh();}
            await r.Session.Act(ScriptAction.Restart);
            Check(r.Processes.Starts==0&&r.Processes.Stops==0,"managed Restart gate preserves instance:"+gate);
        }
        foreach (var action in new[]{ScriptAction.Stop,ScriptAction.Restart,ScriptAction.Recycle})
        {
            using var r=new FunctionalRig("confirm-shutdown-"+action); if(action==ScriptAction.Recycle)r.EmptyProcesses();await r.Session.Refresh();
            var before=File.ReadAllBytes(r.Store.FilePath);r.Interaction.HoldConfirmation=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var task=r.Session.Act(action);Check(r.Session.Busy&&r.Interaction.Confirmations==1,"pending confirmation owns lane:"+action);
            Check(!await r.Session.PrepareExit(TimeSpan.Zero),"shutdown preserves busy owner:"+action);
            r.Interaction.HoldConfirmation.SetResult(true);await task;
            Check(r.Processes.Starts==0&&r.Processes.Stops==0&&r.Recycler.Calls==0,"confirmation after shutdown authorizes ZEROeffects:"+action);
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(r.Store.FilePath))&&!r.Session.Busy,"shutdown confirmation preserves catalog and releases lane:"+action);
        }
        using (var r=new FunctionalRig("checkbox-shutdown"))
        {
            r.Arm(); await r.Session.PrepareExit(TimeSpan.Zero); var before=File.ReadAllBytes(r.Store.FilePath);
            await r.Session.SetIntent(false);
            Check(before.AsSpan().SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && r.Processes.Stops==0, "checkbox after shutdown neither writes nor stops");
        }
        using (var r=new FunctionalRig("owner-after-confirm"))
        {
            await r.Session.Refresh();r.Interaction.HoldConfirmation=new(TaskCreationOptions.RunContinuationsAsynchronously);var task=r.Session.Act(ScriptAction.Stop);
            r.Owner.Held=false;r.Interaction.HoldConfirmation.SetResult(true);await task;
            Check(r.Processes.Stops==0&&!r.Session.Busy,"owner lost during confirmation refuses effect");
        }
        using (var r=new FunctionalRig("cancel-during-preflight"))
        {
            await r.Session.Refresh();r.Interaction.Confirmed=true;
            r.Processes.BeforePreflight=()=> { _=r.Session.PrepareExit(TimeSpan.Zero); };
            await r.Session.Act(ScriptAction.Restart);
            Check(r.Processes.Stops==0&&r.Processes.Starts==0&&r.Session.ShuttingDown&&!r.Session.Busy,"shutdown token reaches controller preflight before Stop");
        }
        using (var r=new FunctionalRig("managed-cancel-preflight"))
        {
            r.Arm();await r.Session.Refresh();r.Interaction.Confirmed=true;var before=File.ReadAllBytes(r.Store.FilePath);
            r.Processes.BeforePreflight=()=>{_=r.Session.PrepareExit(TimeSpan.Zero);};await r.Session.Act(ScriptAction.Restart);
            Check(r.Processes.Stops==0&&r.Processes.Starts==0&&before.AsSpan().SequenceEqual(File.ReadAllBytes(r.Store.FilePath)),"managed shutdown during preflight preserves instance and primary before reservation");
        }
        foreach (var change in new[]{"corrupt","valid-changed","shutdown","unchanged"})
        {
            using var r=new FunctionalRig("schema1-prompt-"+change);var legacy=r.Store.Load();r.Store.Save(legacy);legacy.Schema=1;
            LegacyPayload(r.Store,legacy,1);r.Reload();
            r.Interaction.HoldConfirmation=new(TaskCreationOptions.RunContinuationsAsynchronously);var task=r.Session.ConvertSchema();
            if(change=="corrupt")File.WriteAllText(r.Store.FilePath,"{corrupt while prompt open}");
            if(change=="valid-changed"){legacy.Scripts[0].Description="Changed externally";LegacyPayload(r.Store,legacy,1);}
            if(change=="shutdown")await r.Session.PrepareExit(TimeSpan.Zero);
            var primary=File.ReadAllBytes(r.Store.FilePath);var backup=File.ReadAllBytes(r.Store.FilePath+".bak");
            r.Interaction.HoldConfirmation.SetResult(true);await task;
            if(change!="unchanged")
            {
                Check(primary.AsSpan().SequenceEqual(File.ReadAllBytes(r.Store.FilePath))&&backup.AsSpan().SequenceEqual(File.ReadAllBytes(r.Store.FilePath+".bak")),"schema1 pending conversion preserves both files:"+change);
                Check(r.Store.LoadedSchema==1&&!r.Session.Busy,"schema1 refused conversion leaves old schema/lane:"+change);
            }
            else Check(r.Store.LoadedSchema==5&&r.Store.Load().Scripts.All(e=>e.RequestedEnabled is null),"unchanged primary permits explicit safe conversion");
        }
        using (var r=new FunctionalRig("taskbar-message"))
        {
            int adds=0;bool next=true;var observed=new List<bool>();
            var recovery=new TrayRecovery(0xC123,()=>{adds++;return next;},available=>{observed.Add(available);if(!available)r.Session.ReportTrayFailure();});
            Check(!recovery.HandleMessage(1)&&adds==0,"unrelated message does not reinscribe tray");
            Check(recovery.HandleMessage(0xC123)&&adds==1&&recovery.Available,"fake TaskbarCreated invokes NIM_ADD boundary");
            next=false;Check(recovery.HandleMessage(0xC123)&&adds==2&&!recovery.Available&&r.Session.Error&&r.Session.Status.Contains("bandeja",StringComparison.Ordinal),"failed tray re-add visibly reports recoverable failure");
            var window=new MainWindow(r.Session){CloseToTray=true,TrayAvailable=false};bool hidden=false;window.HiddenToTray+=()=>hidden=true;window.Close();
            Check(!hidden,"missing tray refuses hiding window into inaccessible icon");
            WpfProgram.Render(window,Path.Combine(root,"renders","tray-recovery-failed-min.png"),1000,720,1);
            next=true;Check(recovery.Restore()&&adds==3&&observed.SequenceEqual(new[]{true,false,true}),"later fake retry restores tray availability");
            window.CloseToTray=false;window.Close();
            var parameters=TrayHost.SourceParameters();Check(parameters.ParentWindow==0&&(parameters.WindowStyle&0x40000000)==0,"tray hook HWND descriptor is top-level not message-only/child");
        }
    }
}
