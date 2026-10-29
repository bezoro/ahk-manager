using Core = Tests.Backend.Core;
using Microsoft.UI.Xaml;
using System.Security.Principal;

namespace Bezoro.AhkManager.WinUI.Tests;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--core" or "--ui" or "--ui-behavior" or "--ui-review-regressions" or "--layout-review" or "--instance-redirect" or "--ahk-owned" or "--render-diagnostic" or "--render-minimal")) { Console.Error.WriteLine("Explicit test selector and isolated-root required."); return 2; }
        string root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root)) { Console.Error.WriteLine("Isolated root must be absent."); return 2; }
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => File.WriteAllText(Path.Combine(root,"unhandled.txt"),error.ExceptionObject.ToString());
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if(args[0]=="--instance-redirect")
        {
            string name=Path.GetFileName(root);const string prefix="second-instance-";
            if(!name.StartsWith(prefix,StringComparison.Ordinal)||!Guid.TryParseExact(name[prefix.Length..],"N",out var scope))return 2;
            using var watchdog=new Timer(_=>Environment.Exit(3),null,TimeSpan.FromSeconds(8),Timeout.InfiniteTimeSpan);
            using var instance=new Bezoro.AhkManager.WinUI.Infrastructure.SingleInstanceService();
            bool primary=instance.Register(scope);File.WriteAllText(Path.Combine(root,"result.txt"),primary?"FAIL unexpectedly primary\n":"PASS activation redirected\n");return primary?1:0;
        }
        if (args[0] == "--core")
        {
            try { Core.CoreSuite.RunAsync(Path.Combine(root,"core-fixtures")).GetAwaiter().GetResult(); File.WriteAllText(Path.Combine(root,"result.txt"),"PASS core\n"); return 0; }
            catch (Exception error) { File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString()); return 1; }
        }
        File.WriteAllText(Path.Combine(root,"startup-stages.txt"),"before Application.Start\n");
        int result = 1;
        Application.Start(startup =>
        {
            File.AppendAllText(Path.Combine(root,"startup-stages.txt"),"Application.Start callback\n");
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            if(args[0]=="--render-minimal")_ = new UI.MinimalRenderApp(root,code=>result=code);
            else _ = new TestApp(args[0],root,code => result=code);
        });
        File.AppendAllText(Path.Combine(root,"startup-stages.txt"),"Application.Start returned result="+result+"\n");
        return result;
    }
}
