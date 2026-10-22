using System.IO;
using System.Windows.Threading;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;

static partial class Tests
{
    static async Task RunExitReview()
    {
        using(var r=new FunctionalRig("missing-tray-cancel"))
        {
            r.Arm();await r.Session.Refresh();var window=new MainWindow(r.Session){CloseToTray=true,TrayAvailable=false};
            var prompt=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int prompts=0,suspended=0,finished=0;bool visibility=true;
            var exit=new ExitCoordinator(r.Session,()=>{prompts++;return prompt.Task;},()=>suspended++,()=>{finished++;visibility=false;r.Owner.Dispose();},TimeSpan.Zero);
            Task<bool>? request=null;r.Session.ExitRequested+=()=>request=exit.Request();
            bool hidden=false;window.HiddenToTray+=()=>{hidden=true;visibility=false;};
            window.Close();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            Check(prompts==1&&!hidden&&visibility,"missing tray X requests confirmed exit after Closing without hide");
            window.Close();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            Check(prompts==1,"repeated X while prompt pending cannot stack confirmations");
            // The second request is refused; capture the pending first completion separately through coordinator.
            prompt.SetResult(false);await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            Check(suspended==0&&finished==0&&r.Owner.Held&&!r.Session.ShuttingDown&&visibility,"Cancel keeps visibility supervision and owner");
            Check(r.Processes.Starts==0&&r.Processes.Stops==0&&r.Recycler.Calls==0,"Cancel authorizes zero script effects");
            window.CloseToTray=false;window.Close();
        }
        using(var r=new FunctionalRig("missing-tray-confirm-busy"))
        {
            await r.Session.Refresh();r.EmptyProcesses();await r.Session.Refresh();r.Processes.HoldStart=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var action=r.Session.Act(ScriptAction.Start);Check(r.Session.Busy&&r.Processes.Starts==1,"already authorized action holds lane before exit");
            var window=new MainWindow(r.Session){CloseToTray=true,TrayAvailable=false};
            var prompt=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int prompts=0,suspended=0,finished=0;bool ownerHeldAtFinish=false;
            var exit=new ExitCoordinator(r.Session,()=>{prompts++;return prompt.Task;},()=>suspended++,()=>{ownerHeldAtFinish=r.Owner.Held;finished++;r.Owner.Dispose();},TimeSpan.Zero);
            Task<bool>? request=null;r.Session.ExitRequested+=()=>request=exit.Request();
            window.Close();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            Check(prompts==1&&request is not null,"missing tray uses same typed exit coordinator");
            prompt.SetResult(true);await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            Check(suspended==1&&r.Session.ShuttingDown&&finished==0&&r.Owner.Held,"Confirm suspends producer but retains owner until lane finishes");
            await r.Session.Reconcile();Check(r.Processes.Starts==1,"confirmed exit prevents new producer effects");
            r.Processes.HoldStart.SetResult();await action;Check(await request!&&finished==1&&ownerHeldAtFinish&&!r.Owner.Held,"finish releases owner only after authorized action completed");
            Check(r.Processes.Stops==0&&r.Recycler.Calls==0,"confirmed manager exit never stops children");
            Check(!await exit.Request()&&prompts==1,"completed exit cannot create another confirmation");
            window.CloseToTray=false;window.Close();
        }
        using(var r=new FunctionalRig("available-tray-hide"))
        {
            var window=new MainWindow(r.Session){CloseToTray=true,TrayAvailable=true};int hidden=0,requests=0;
            window.HiddenToTray+=()=>hidden++;r.Session.ExitRequested+=()=>requests++;
            window.Close();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            Check(hidden==1&&requests==0&&r.Owner.Held&&!r.Session.ShuttingDown,"available tray X still hides and keeps supervision owner");
            window.CloseToTray=false;window.Close();
        }
    }
}
