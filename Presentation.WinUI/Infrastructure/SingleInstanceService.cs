using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Windows.AppLifecycle;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

internal sealed class SingleInstanceService : IDisposable
{
    AppInstance? instance;
    int pendingActivation;
    public event EventHandler? Activated;
    public bool Register(Guid? isolatedTestScope=null)
    {
        string key = "Bezoro.AhkManager.WinUI." + WindowsIdentity.GetCurrent().User!.Value + "." + Process.GetCurrentProcess().SessionId;
        if(isolatedTestScope is {} scope)key+=".Fixture."+scope.ToString("N");
        instance = AppInstance.FindOrRegisterForKey(key);
        if (!instance.IsCurrent) { instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().GetAwaiter().GetResult(); return false; }
        instance.Activated += OnActivated; return true;
    }
    internal bool ConsumePendingActivation()=>Interlocked.Exchange(ref pendingActivation,0)!=0;
    void OnActivated(object? sender, AppActivationArguments args)
    {
        Interlocked.Exchange(ref pendingActivation,1);
        Activated?.Invoke(this,EventArgs.Empty);
    }
    public void Dispose() { if (instance?.IsCurrent == true) { instance.Activated -= OnActivated; instance.UnregisterKey(); } }
}
