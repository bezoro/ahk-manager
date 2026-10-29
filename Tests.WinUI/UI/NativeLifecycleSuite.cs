using System.Runtime.InteropServices;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

internal static class NativeLifecycleSuite
{
    public static async Task RunAsync(string output,Action<bool,string> check)
    {
        int show=0,settings=0,exit=0,adds=0;bool registration=true;
        using(var real=new TrayService(DispatcherQueue.GetForCurrentThread(),()=>show++,()=>settings++,()=>exit++,()=>[]))
            check(real.Registered,"native-shell-tray-registration");
        using(var tray=new TrayService(DispatcherQueue.GetForCurrentThread(),()=>show++,()=>settings++,()=>exit++,()=>[],command=>{if(command==0)adds++;return registration;}))
        {
            check(tray.Registered&&adds==1,"tray-initial-boundary-registration");
            SendMessage(tray.WindowHandle,0x111,1,0);await Task.Delay(30);check(show==1,"tray-show-dispatched-on-ui-queue");
            SendMessage(tray.WindowHandle,tray.ShellRestartMessage,0,0);check(tray.Registered&&adds==2,"explorer-restart-readds-owned-icon");
            registration=false;SendMessage(tray.WindowHandle,tray.ShellRestartMessage,0,0);await Task.Delay(30);
            check(!tray.Registered&&show==2,"explorer-registration-failure-restores-window-access");
            SendMessage(tray.WindowHandle,0x111,2,0);SendMessage(tray.WindowHandle,0x111,3,0);await Task.Delay(30);check(settings==1&&exit==1,"native-tray-settings-exit-commands");
        }
        int applicationExits=0;
        using var instance=new SingleInstanceService();
        var scope=Guid.NewGuid();check(instance.Register(scope),"isolated-primary-instance-registration");
        var service=new FakeManagerService(ManagerSnapshot.Empty with {Settings=new ManagerSettings {CloseBehavior=CloseBehavior.HideToTray}});
        var lifecycle=new AppLifecycle(service,instance,Application.Current,()=>applicationExits++,_=>true);
        await lifecycle.StartAsync([]);await Task.Delay(60);var window=lifecycle.Window!;
        SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(window),0x112,0xF060,0);await Task.Delay(60);
        check(!window.AppWindow.IsVisible&&applicationExits==0&&service.Disposals==0,"native-close-hides-only-with-registered-tray");
        string childRoot=Path.Combine(output,"second-instance-"+scope.ToString("N"));
        var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--instance-redirect");start.ArgumentList.Add(childRoot);
        using(var child=System.Diagnostics.Process.Start(start)??throw new InvalidOperationException("Isolated second instance failed to start"))
        {
            var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            File.WriteAllText(Path.Combine(output,"second-instance.stdout.log"),await stdout);File.WriteAllText(Path.Combine(output,"second-instance.stderr.log"),await stderr);
            check(child.ExitCode==0&&File.ReadAllText(Path.Combine(childRoot,"result.txt")).StartsWith("PASS",StringComparison.Ordinal),"second-process-activation-redirected");
        }
        await Task.Delay(100);check(window.AppWindow.IsVisible,"hidden-native-window-restores-from-second-instance");
        check(GetForegroundWindow()==WinRT.Interop.WindowNative.GetWindowHandle(window),"second-instance-restores-owned-window-foreground");
        await lifecycle.ExitAsync();check(applicationExits==1&&service.Disposals==1&&service.Effects==0,"native-exit-disposes-manager-without-script-control");
        var fallbackService=new FakeManagerService(ManagerSnapshot.Empty with {Settings=new ManagerSettings {CloseBehavior=CloseBehavior.HideToTray}});
        var fallback=new AppLifecycle(fallbackService,instance,Application.Current,()=>applicationExits++,_=>false);
        await fallback.StartAsync(["--login"]);await Task.Delay(50);
        check(fallback.Window!.AppWindow.IsVisible,"tray-unavailable-never-hides-login-window");
        SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(fallback.Window),0x112,0xF060,0);await Task.Delay(60);
        check(applicationExits==2&&fallbackService.Disposals==1,"tray-unavailable-close-exits-instead-of-stranding-window");
    }
    [DllImport("user32.dll")]static extern nint SendMessage(nint window,uint message,nuint wp,nint lp);
    [DllImport("user32.dll")]static extern nint GetForegroundWindow();
}
