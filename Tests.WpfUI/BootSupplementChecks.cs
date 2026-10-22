using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;

static partial class Tests
{
    static bool WithinViewport(Button button)
    {
        DependencyObject? ancestor=button;
        while(ancestor is not null && ancestor is not ScrollViewer)ancestor=VisualTreeHelper.GetParent(ancestor);
        if(ancestor is not ScrollViewer viewer)return false;
        var bounds=button.TransformToAncestor(viewer).TransformBounds(new Rect(button.RenderSize));
        return bounds.Width>0&&bounds.Height>0&&bounds.Left>=0&&bounds.Top>=0&&bounds.Right<=viewer.ActualWidth+1&&bounds.Bottom<=viewer.ActualHeight+1;
    }
    static async Task RunBootSupplement()
    {
        foreach(int raceAt in new[]{2,3})
        {
            using var r=new ManualRig("P02-step-race-"+raceAt);await r.Pending();MakeLegacy(r);var ledger=Bytes(r.Ledger.FilePath);var anchor=Bytes(r.Ledger.AnchorPath);int calls=0;
            var outcome=r.Ledger.Upgrade(r.Session.CurrentCatalog.Authority,()=>++calls!=raceAt);
            Check(!outcome.Success&&anchor.SequenceEqual(Bytes(r.Ledger.AnchorPath))&&(raceAt!=2||ledger.SequenceEqual(Bytes(r.Ledger.FilePath))),"P02 current primary revalidated before each authority write step "+raceAt);
            r.Reopen();Check(r.Ledger.HasPending&&(raceAt!=3||!r.Ledger.Ready),"P02 interrupted step remains pending and partial blocks after reopen "+raceAt);
        }
        using(var r=new ManualRig("C04-input-bound"))
        {
            await r.Pending();var request=r.Ledger.Requests.Single();request.LoginKey=new string('x',BootContextService.PlainLimit);int protects=r.Protector.ProtectCalls;
            bool refused=false;try{r.Context.Capture(request,r.Ledger.Binding!);}catch(InvalidDataException){refused=true;}
            Check(refused&&r.Protector.ProtectCalls==protects,"C04 plaintext over256KiB rejected before fake DPAPI");
        }
        using(var r=new ManualRig("R05-profile-race"))
        {
            await r.Pending();using var other=new ManualRig("R05-profile-other",r.Namespace,r.Entry.Path);r.Counter.Value=other.Counter.Value=10;r.Interaction.HoldConfirmation=new();var waiting=r.Session.RecoverAfterRestart();other.Interaction.Confirmed=true;await other.Session.RecoverAfterRestart();r.Interaction.HoldConfirmation.SetResult(true);await waiting;
            Check(!other.Ledger.HasPending&&!r.Ledger.Ready&&r.Dispatcher.Calls==1&&other.Dispatcher.Calls==0,"R05 competing profile clear refuses stale fingerprint no replay");
        }
        foreach(var state in new[]{"eligible","inconclusive","legacy"})
        {
            using var r=new ManualRig("render-"+state);await r.Pending();if(state=="legacy"){MakeLegacy(r);r.Interaction.Confirmed=true;await r.Session.ConvertSchema();}r.Counter.Value=state=="inconclusive"?1001UL:10UL;await r.Session.VerifyRequest();var window=new MainWindow(r.Session);
            foreach(var scale in new[]{1d,1.5d,2d})
            {
                string name="recovery-"+state+"-"+(scale*100).ToString(System.Globalization.CultureInfo.InvariantCulture)+".png";int width=scale==1.5?1220:1000,height=scale==1.5?800:720;
                Bezoro.AhkManager.Presentation.Program.Render(window,Path.Combine(root,"layout-"+name),width,height,scale);window.ShowRequestDetails();
                Bezoro.AhkManager.Presentation.Program.Render(window,Path.Combine(root,name),width,height,scale);
                Check(WithinViewport(window.VerifyRequestControl)&&WithinViewport(window.RecoverRequestControl),"D01 both request commands fully inside scroll viewport "+state+" "+scale);
            }
            Check(state!="legacy"||window.RequestControl.Text.Contains("Pedido legado sem captura original"),"D01 "+state+" proof explanation visible in request panel");
            Check(state!="inconclusive"||window.RequestControl.Text.Contains("não prova reinicialização"),"D01 "+state+" last verification reason displayed without reusable authorization");window.Close();
        }
    }
}
