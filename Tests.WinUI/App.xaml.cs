using Microsoft.UI.Xaml;

namespace Bezoro.AhkManager.WinUI.Tests;

public sealed partial class TestApp : Application
{
    readonly string selector,root;
    readonly Action<int> complete;
    internal TestApp(string selector,string root,Action<int> complete)
    {
        this.selector=selector;this.root=root;this.complete=complete;
        UnhandledException += (_, args) => {File.WriteAllText(Path.Combine(root,"unhandled.txt"),args.Exception.ToString());complete(1);Exit();};
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window? fixtureLifetime=null;
        try
        {
            if(selector=="--ahk-owned")
            {
                fixtureLifetime=new Window{Content=new Microsoft.UI.Xaml.Controls.TextBlock{Text="Validação isolada de AutoHotkey"}};
                fixtureLifetime.Closed+=(_,_)=>File.AppendAllText(Path.Combine(root,"startup-stages.txt"),"fixture lifetime Closed\n");
                fixtureLifetime.Activate();File.AppendAllText(Path.Combine(root,"startup-stages.txt"),"fixture lifetime activated\n");
            }
            if (selector=="--ui") await UI.NativeUiSuite.RunAsync(root);
            else if (selector=="--ui-behavior") await UI.NativeUiSuite.RunAsync(root,false);
            else if (selector=="--ui-review-regressions") await UI.UiReviewRegressionSuite.RunAsync(root);
            else if (selector=="--layout-review") await UI.NativeUiSuite.RunAsync(root,false,true);
            else if (selector=="--render-diagnostic") await UI.NativeRenderDiagnosticSuite.RunAsync(root);
            else await Integration.OwnedAhkSuite.RunAsync(root);
            File.WriteAllText(Path.Combine(root,"result.txt"),"PASS "+selector+Environment.NewLine);complete(0);
        }
        catch(Exception error) {File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());complete(1);}
        finally {File.AppendAllText(Path.Combine(root,"startup-stages.txt"),"suite continuation completed\n");fixtureLifetime?.Close();Exit();}
    }
}
