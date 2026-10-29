using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.WinUI.Resources;
using Bezoro.AhkManager.WinUI.Views;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

internal sealed class AppLifecycle
{
    readonly IManagerService service;
    readonly SingleInstanceService instance;
    readonly Action exitApplication;
    readonly Func<uint,bool>? trayNotificationBoundary;
    MainWindow? window;
    TrayService? tray;
    bool exiting;
    bool startupDecisionComplete;
    int activationRequested;
    internal MainWindow? Window => window;
    internal TrayService? Tray => tray;
    public AppLifecycle(IManagerService service, SingleInstanceService instance, Application app,Action? exitApplication=null,Func<uint,bool>? trayNotificationBoundary=null) { this.service = service; this.instance = instance;this.exitApplication=exitApplication??(()=>app.Exit());this.trayNotificationBoundary=trayNotificationBoundary; }
    public async Task StartAsync(string[] arguments)
    {
        instance.Activated += InstanceActivated;
        if(instance.ConsumePendingActivation())Interlocked.Exchange(ref activationRequested,1);
        await service.LoadAsync();
        window = new(service);
        WindowPlacementService.Restore(window.AppWindow, service.Snapshot.Settings.Window);
        tray = new(window.DispatcherQueue, Show, () => _ = ShowSettingsFromTrayAsync(), () => _ = ExitAsync(), () => service.Snapshot.Scripts.Where(script => script.Status == ScriptStatus.Running).Select(script => script.FriendlyName).ToArray(),trayNotificationBoundary,()=>window.AppWindow.Hide(),()=>window.AppWindow.IsVisible);
        window.ExitRequested += (_, _) => _ = ExitAsync();
        window.AppWindow.Closing += WindowClosing;
        bool login = arguments.Contains("--login", StringComparer.Ordinal);
        if (login) await service.RunLoginAsync();
        bool showAtStartup = !login || service.Snapshot.Settings.ShowWindowAtLogin || !tray.Registered || Volatile.Read(ref activationRequested)!=0 || instance.ConsumePendingActivation();
        startupDecisionComplete=true;
        if(showAtStartup) Show(); else window.AppWindow.Hide();
    }
    void InstanceActivated(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref activationRequested,1);
        window?.DispatcherQueue.TryEnqueue(() => Show());
    }
    async Task ShowSettingsFromTrayAsync()
    {
        if(window is not {} target||exiting)return;
        Show();
        try {if(!exiting)await target.ShowSettingsWhenReadyAsync();}
        catch(OperationCanceledException) { }
    }
    internal void Show()
    {
        if (exiting||window is null) return;
        // Explicit activation during login must remain intent until the final
        // startup visibility decision; showing early must not consume it.
        if(startupDecisionComplete){Interlocked.Exchange(ref activationRequested,0);instance.ConsumePendingActivation();}
        else Interlocked.Exchange(ref activationRequested,1);
        window.AppWindow.Show(); window.Activate();
        NativeForeground.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        _ = service.RefreshAsync();
    }
    async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (exiting) return;
        args.Cancel = true;
        if (window is null) return;
        await service.SaveSettingsAsync(service.Snapshot.Settings with { Window = WindowPlacementService.Capture(window.AppWindow) });
        if (service.Snapshot.Settings.CloseBehavior == CloseBehavior.HideToTray && tray?.Registered == true) window.AppWindow.Hide();
        else await ExitAsync();
    }
    internal async Task ExitAsync()
    {
        if (exiting) return; exiting = true;
        instance.Activated -= InstanceActivated;
        tray?.Dispose(); await service.DisposeAsync();
        window?.Close(); exitApplication();
    }
    static class NativeForeground
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint window);
    }
}
