using Microsoft.UI.Xaml;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.WinUI;

public sealed partial class App : Application
{
    readonly SingleInstanceService instance;
    readonly string[] arguments;
    AppLifecycle? lifecycle;
    internal App(SingleInstanceService instance, string[] arguments)
    {
        this.instance = instance; this.arguments = arguments; InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try { var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManagerWinUI"); Directory.CreateDirectory(root); File.AppendAllText(Path.Combine(root, "native-crash.log"), DateTimeOffset.UtcNow + Environment.NewLine + args.Exception + Environment.NewLine); } catch { }
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        lifecycle = new(ManagerService.CreateDefault(), instance, this);
        await lifecycle.StartAsync(arguments);
    }
}
