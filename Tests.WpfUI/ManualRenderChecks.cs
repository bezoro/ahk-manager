using System.IO;
using System.Windows;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;
static partial class Tests
{
    static async Task RunManualRender()
    {
        using(var r=new ManualRig("render-null"))
        {
            await r.Pending();var window=new MainWindow(r.Session);
            foreach(var scale in new[]{1d,1.5d,2d})
            {
                string name="request-null-"+(scale*100).ToString("0",System.Globalization.CultureInfo.InvariantCulture)+".png";
                WpfProgram.Render(window,Path.Combine(root,name),scale==1.5?1220:1000,scale==1.5?800:720,scale);
                window.ShowRequestDetails();WpfProgram.Render(window,Path.Combine(root,name),scale==1.5?1220:1000,scale==1.5?800:720,scale);
            }
            Check(window.RequestControl.Text.Contains(r.Entry.Path)&&window.RequestControl.Text.Contains("recuperação desse caso ainda não está disponível")&&window.VerifyRequestControl.IsEnabled&&!window.ActionButton(ScriptAction.Start).IsEnabled,"pending panel exposes path limitation and read-only verification");
            window.Close();var c=r.Store.Load();c.Scripts.Clear();r.Store.Save(c);r.Reopen();var orphan=new MainWindow(r.Session);
            WpfProgram.Render(orphan,Path.Combine(root,"request-orphan-min.png"),1000,720,1);orphan.ShowRequestDetails();WpfProgram.Render(orphan,Path.Combine(root,"request-orphan-min.png"),1000,720,1);
            WpfProgram.Render(orphan,Path.Combine(root,"request-orphan-200.png"),1000,720,2);
            Check(r.Session.Selected is null&&orphan.RequestControl.Text.Contains(r.Ledger.Requests.Single().ScriptPath)&&orphan.VerifyRequestControl.IsEnabled,"orphan pending remains visible/queryable without original row");
            Check(r.Session.Status.Contains("Pedido registrado"),"orphan initial footer explains global barrier");
            await r.Session.VerifyRequest();Check(r.Ledger.HasPending&&r.Dispatcher.Calls==1,"orphan verify never clears no-candidate barrier or dispatches");orphan.Close();
        }
        using(var r=new ManualRig("render-positive"))
        {
            await r.Ready();var identity=new ProcessIdentity(8400,132000000000000000,r.Entry.Interpreter,r.Entry.Path);r.Dispatcher.Outcome=new(ElevationDisposition.Accepted,"Fake accepted",new(identity.Pid,identity.Created,identity.Executable));r.Dispatcher.AfterConsume=_=>r.Processes.Current=new([identity],true,"Fake verified");await r.Session.Act(ScriptAction.Start);var w=new MainWindow(r.Session);WpfProgram.Render(w,Path.Combine(root,"request-positive-150.png"),1220,800,1.5);Check(!r.Ledger.HasPending&&r.Session.State(r.Entry).StartsWith("Executando"),"positive render represents observed identity without privilege claim");w.Close();
        }
        using(var r=new ManualRig("render-cancel"))
        {
            await r.Ready();r.Dispatcher.Outcome=new(ElevationDisposition.Cancelled,"Fake UAC cancelado pelo usuário (1223).");await r.Session.Act(ScriptAction.Start);var w=new MainWindow(r.Session);WpfProgram.Render(w,Path.Combine(root,"request-cancel-min.png"),1000,720,1);Check(!r.Ledger.HasPending&&r.Session.Status.Contains("cancelado"),"cancel render leaves preference with no invented execution");w.Close();
        }
        using(var r=new ManualRig("render-cleanup-error"))
        {
            await r.Ready();r.Dispatcher.Outcome=new(ElevationDisposition.Cancelled,"Fake UAC1223");r.Dispatcher.AfterConsume=_=>File.SetAttributes(r.Ledger.FilePath,FileAttributes.ReadOnly);try{await r.Session.Act(ScriptAction.Start);}finally{File.SetAttributes(r.Ledger.FilePath,FileAttributes.Normal);}var w=new MainWindow(r.Session);WpfProgram.Render(w,Path.Combine(root,"request-cleanup-failed-min.png"),1000,720,1);w.ShowRequestDetails();WpfProgram.Render(w,Path.Combine(root,"request-cleanup-failed-min.png"),1000,720,1);Check(r.Ledger.HasPending&&!w.ActionButton(ScriptAction.Start).IsEnabled,"cleanup error render preserves visible barrier");w.Close();
        }
        using(var r=new ManualRig("render-partial"))
        {
            File.Delete(r.Ledger.AnchorPath);r.Reopen();var w=new MainWindow(r.Session);WpfProgram.Render(w,Path.Combine(root,"authority-partial-min.png"),1000,720,1);w.ShowRequestDetails();WpfProgram.Render(w,Path.Combine(root,"authority-partial-min.png"),1000,720,1);Check(w.RequestControl.Text.Contains(r.Ledger.FilePath)&&w.RequestControl.Text.Contains("Autoridade"),"partial authority render shows file and failure without deletion advice");
            Check(r.Session.Error&&r.Session.Status.Contains("Autoridade"),"partial initial footer reports authority failure");w.Close();
        }
    }
}
