using Microsoft.UI.Xaml;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.WinUI;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        using var instance = new SingleInstanceService();
        if (!instance.Register()) return;
        Application.Start(startup =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(instance, args);
        });
    }
}
