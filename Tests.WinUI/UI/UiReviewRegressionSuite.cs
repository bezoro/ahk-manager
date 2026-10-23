using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.ViewModels;
using Bezoro.AhkManager.WinUI.Views;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

internal static class UiReviewRegressionSuite
{
    public static async Task RunAsync(string output)
    {
        var failures=new List<string>();int checks=0;
        void Check(bool value,string name){checks++;Console.WriteLine((value?"PASS ":"FAIL ")+name);if(!value)failures.Add(name);}
        var alpha=Guid.NewGuid();var beta=Guid.NewGuid();var collection=Guid.NewGuid();var observed=DateTimeOffset.UtcNow;
        ScriptSnapshot Row(Guid id,string name,ScriptStatus status,bool admin=false)=>new(id,Path.Combine(output,name+".ahk"),name,"",collection,false,admin,false,status,status==ScriptStatus.Running?new(new(123,1,"fixture.exe",Path.Combine(output,name+".ahk"),"fixture",1),ExecutionOrigin.Manager,"2.0.30",observed.AddMinutes(-2),null,null,true):null,[],AnalysisCoverage.Partial,[]);
        var state=new ManagerSnapshot(1,[Row(alpha,"Alpha",ScriptStatus.Stopped),Row(beta,"Beta",ScriptStatus.Running,true)],[new(collection,"Original")],new(),[],[],false,false,observed);
        await using var service=new FakeManagerService(state);var window=new MainWindow(service);window.Activate();await Task.Delay(150);
        T Find<T>(string name) where T:class=>window.VisualRoot.FindName(name) as T??throw new InvalidOperationException("Native control missing: "+name);
        var list=Find<ListView>("ScriptList");
        async Task Select(Guid id){list.SelectedItem=window.ViewModel.Rows.Single(row=>row.Id==id);await Task.Delay(40);}
        try
        {
            await Select(alpha);Check(!Find<Button>("StartAction").IsEnabled,"UI03 start disabled when effect owner unavailable");
            service.Publish(state with {EffectsAvailable=true,Revision=2});await Task.Delay(50);await Select(beta);
            Check(!Find<Button>("RestartAction").IsEnabled,"UI03 administrator restart disabled before confirmation");
            Check(!Find<Button>("SuspendAction").IsEnabled,"UI03 unobservable suspension disabled");
            Check(Find<Button>("PauseAction").IsEnabled,"UI03 honest deliberate pause toggle remains available");
            Check(Find<TextBlock>("SelectedProcess").Text.Contains("00:02:00",StringComparison.Ordinal),"UI08 process panel includes elapsed duration at observation");
            await Select(alpha);var menu=window.CreateScriptMenu();
            Check(menu.Items.OfType<MenuFlyoutItem>().Take(3).Select(item=>item.IsEnabled).SequenceEqual(new[]{true,false,false}),"UI03 context menu matches stopped toolbar eligibility");
            Find<Expander>("MetadataExpander").IsExpanded=true;Find<TextBox>("NameDraft").Text="Uncommitted draft";
            service.Publish(service.Snapshot with {Revision=3,Collections=[new(collection,"Renamed")]});await Task.Delay(70);
            var choices=Find<ComboBox>("CollectionDraft");Check(((IEnumerable<CollectionChoice>)choices.ItemsSource).Any(item=>item.Name=="Renamed"),"UI04 same-script collection rename refreshes choices");
            service.Publish(service.Snapshot with {Revision=4,Collections=[],Scripts=[..service.Snapshot.Scripts.Select(row=>row with {CollectionId=null})]});await Task.Delay(70);
            Check(((IEnumerable<CollectionChoice>)choices.ItemsSource).Count()==1&&window.ViewModel.GetDraft()?.CollectionId is null&&Find<TextBox>("NameDraft").Text=="Uncommitted draft","UI04 collection removal reconciles only membership and preserves draft");
            service.Publish(service.Snapshot with {Revision=5,Activity=[new(Guid.NewGuid(),observed,alpha,ScriptOperation.Start,Outcome.Confirmed,ReasonCode.None),new(Guid.NewGuid(),observed,beta,ScriptOperation.Stop,Outcome.Unknown,ReasonCode.ExitNotObserved)]});await Task.Delay(50);
            var nav=Find<NavigationView>("Navigation");nav.SelectedItem=nav.MenuItems.OfType<NavigationViewItem>().Single(item=>item.Tag?.ToString()=="activity");await Task.Delay(40);
            Find<TextBox>("OtherFilter").Text="Alpha";await Task.Delay(40);
            var activity=(IEnumerable<DetailRow>)Find<ListView>("OtherList").ItemsSource;
            Check(activity.Count()==1&&(activity.Single().Title+activity.Single().Detail).Contains("Alpha",StringComparison.Ordinal),"UI06 activity preserves and filters script association");
            nav.SelectedItem=nav.MenuItems.OfType<NavigationViewItem>().Single(item=>item.Tag?.ToString()=="scripts");window.AppWindow.Resize(new SizeInt32(700,720));await Task.Delay(100);
            var compact=window.VisualRoot.FindName("CompactInspector") as Expander;
            Check(compact?.Visibility==Visibility.Visible&&Find<ListView>("ScriptList").ActualWidth>=400,"UI02 narrow effective width reflows inspector into expandable panel");
            if(compact is not null){compact.IsExpanded=true;await Task.Delay(40);Check(Find<Button>("FolderAction").Focus(FocusState.Keyboard),"UI02 narrow inspector action reachable by keyboard focus");}
            await TrayCases();await TrayDuringLoginCases();await EarlyActivationCase();
        }
        finally {window.Close();File.WriteAllLines(Path.Combine(output,"regression-failures.txt"),failures);}
        Console.WriteLine("UI REVIEW checks="+checks);if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        async Task TrayCases()
        {
            using var instance=new SingleInstanceService();var fake=new FakeManagerService(ManagerSnapshot.Empty);int exits=0;
            var lifecycle=new AppLifecycle(fake,instance,Application.Current,()=>exits++,_=>true);
            try
            {
                await lifecycle.StartAsync([]);await Task.Delay(70);var target=lifecycle.Window!;target.AppWindow.Hide();fake.ReleaseRefresh=new(TaskCreationOptions.RunContinuationsAsynchronously);
                SendMessage(lifecycle.Tray!.WindowHandle,0x111,2,0);await Task.Delay(120);
                Check(target.AppWindow.IsVisible,"UI01 tray preferences shows hidden native window before dialog");
                fake.ReleaseRefresh.TrySetResult();await Task.Delay(100);Check(VisualTreeHelper.GetOpenPopupsForXamlRoot(target.VisualRoot.XamlRoot).Count>0,"UI01 tray preferences waits for activation refresh then opens owned dialog");
                foreach(var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(target.VisualRoot.XamlRoot))HideDialog(popup.Child);
                await Task.Delay(40);lifecycle.Show();await Task.Delay(40);
                SendMessage(lifecycle.Tray.WindowHandle,0x111,1,0);await Task.Delay(40);Check(!target.AppWindow.IsVisible,"UI07 tray visibility action hides visible manager");
                SendMessage(lifecycle.Tray.WindowHandle,0x111,1,0);await Task.Delay(40);Check(target.AppWindow.IsVisible,"UI07 tray visibility action restores hidden manager");
            }
            finally {await lifecycle.ExitAsync();Check(fake.Effects==0&&exits==1,"tray regression fixture performs no script effects");}
        }
        async Task EarlyActivationCase()
        {
            var scope=Guid.NewGuid();using var instance=new SingleInstanceService();Check(instance.Register(scope),"UI05 isolated primary registered");
            var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);instance.Activated+=(_,_)=>received.TrySetResult();
            var fake=new FakeManagerService(ManagerSnapshot.Empty){LoadEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseLoad=new(TaskCreationOptions.RunContinuationsAsynchronously),LoginEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseLogin=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            var lifecycle=new AppLifecycle(fake,instance,Application.Current,()=>{},_=>true);var starting=lifecycle.StartAsync(["--login"]);
            try
            {
                await fake.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                string childRoot=Path.Combine(output,"second-instance-"+scope.ToString("N"));
                var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--instance-redirect");info.ArgumentList.Add(childRoot);
                using var child=Process.Start(info)??throw new InvalidOperationException("Redirect fixture failed to start");var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                File.WriteAllText(Path.Combine(output,"early-instance.stdout.log"),await stdout);File.WriteAllText(Path.Combine(output,"early-instance.stderr.log"),await stderr);
                Check(child.ExitCode==0,"UI05 secondary actual process redirects while primary load awaits");await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
                fake.ReleaseLoad.TrySetResult();await fake.LoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                // The same real registered primary handles both startup phases. AppInstance
                // maintains one process registration, so a second key here is not a valid fixture.
                received=new(TaskCreationOptions.RunContinuationsAsynchronously);
                string loginParent=Path.Combine(output,"login-phase");Directory.CreateDirectory(loginParent);
                info.ArgumentList[1]=Path.Combine(loginParent,"second-instance-"+scope.ToString("N"));
                using var loginChild=Process.Start(info)??throw new InvalidOperationException("Login redirect fixture failed to start");var loginOut=loginChild.StandardOutput.ReadToEndAsync();var loginErr=loginChild.StandardError.ReadToEndAsync();await loginChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                File.WriteAllText(Path.Combine(output,"login-instance.stdout.log"),await loginOut);File.WriteAllText(Path.Combine(output,"login-instance.stderr.log"),await loginErr);
                Check(loginChild.ExitCode==0,"UI05 secondary actual process redirects during login actions");await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(80);Check(lifecycle.Window!.AppWindow.IsVisible,"UI05 explicit activation shows window before login completes");
                fake.ReleaseLogin.TrySetResult();await starting;await Task.Delay(80);
                Check(lifecycle.Window.AppWindow.IsVisible,"UI05 explicit activation remains visible after login actions complete");
                Check(fake.Effects==0,"UI05 activation during login performs no script effects");
            }
            finally {fake.ReleaseLoad.TrySetResult();fake.ReleaseLogin.TrySetResult();await starting;await lifecycle.ExitAsync();}
        }
        async Task TrayDuringLoginCases()
        {
            foreach(int command in new[]{1,2})
            {
                using var instance=new SingleInstanceService();
                var fake=new FakeManagerService(ManagerSnapshot.Empty){LoginEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseLogin=new(TaskCreationOptions.RunContinuationsAsynchronously)};
                var lifecycle=new AppLifecycle(fake,instance,Application.Current,()=>{},_=>true);var starting=lifecycle.StartAsync(["--login"]);
                try
                {
                    await fake.LoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    SendMessage(lifecycle.Tray!.WindowHandle,0x111,(nuint)command,0);await Task.Delay(120);
                    Check(lifecycle.Window!.AppWindow.IsVisible,"UI05 tray command "+command+" shows window during login without instance redirect");
                    fake.ReleaseLogin.TrySetResult();await starting;await Task.Delay(80);
                    Check(lifecycle.Window.AppWindow.IsVisible,"UI05 tray command "+command+" intent survives login completion");
                    Check(fake.Effects==0,"UI05 tray command "+command+" performs no script effects");
                }
                finally
                {
                    fake.ReleaseLogin.TrySetResult();await starting;
                    if(lifecycle.Window?.VisualRoot.XamlRoot is {} xamlRoot)foreach(var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))HideDialog(popup.Child);
                    await lifecycle.ExitAsync();
                }
            }
        }
    }
    static void HideDialog(DependencyObject node){if(node is ContentDialog dialog){dialog.Hide();return;}for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)HideDialog(VisualTreeHelper.GetChild(node,i));}
    [DllImport("user32.dll")]static extern nint SendMessage(nint window,uint message,nuint wp,nint lp);
}
