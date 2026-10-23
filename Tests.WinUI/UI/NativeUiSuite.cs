using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Graphics;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Views;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

internal static class NativeUiSuite
{
    public static async Task RunAsync(string output,bool capture=true,bool layoutReview=false)
    {
        var checks = new List<string>();
        void Check(bool condition,string name) { if(!condition)throw new InvalidOperationException(name);checks.Add(name);Console.WriteLine("PASS "+name); }
        var id=Guid.NewGuid();var other=Guid.NewGuid();var unknown=Guid.NewGuid();var collection=Guid.NewGuid();
        ScriptSnapshot Row(Guid key,string name,ScriptStatus status)=>new(key,Path.Combine(output,"scripts com espaços 漢字",name+".ahk"),name,"Descrição verificável",collection,false,false,false,status,null,[new("Ctrl+Alt+K","^!k",HotkeyOrigin.Manual)],AnalysisCoverage.Partial,[]);
        var running=Row(other,"Janela e navegação com nome bastante longo para verificar corte e leitura acessível",ScriptStatus.Running);
        running=running with {Process=new(new(123,1,Path.Combine(output,"fixture-interpreter.exe"),running.Path,"fixture-only",1),ExecutionOrigin.Manager,"2.0.30",DateTimeOffset.UtcNow,WindowVerified:true)};
        var state=new ManagerSnapshot(1,[Row(id,"Atalhos de trabalho",ScriptStatus.Stopped),running,Row(unknown,"Observação incompleta",ScriptStatus.Unknown)],[new(collection,"Trabalho")],new(),[],[],false,true,DateTimeOffset.UtcNow);
        await using var service=new FakeManagerService(state);
        var window=new MainWindow(service);window.Activate();await Task.Delay(600);
        using(var current=Process.GetCurrentProcess())File.WriteAllLines(Path.Combine(output,"all-loaded-modules.txt"),current.Modules.Cast<ProcessModule>().Select(module=>module.ModuleName+"="+module.FileName));
        int renderTicks=0;EventHandler<object> onRendering=(_,_)=>renderTicks++;CompositionTarget.Rendering+=onRendering;
        T Find<T>(string name) where T:class => window.VisualRoot.FindName(name) as T ?? throw new InvalidOperationException("Missing native control "+name);
        var list=Find<ListView>("ScriptList");var search=Find<AutoSuggestBox>("SearchBox");var nameDraft=Find<TextBox>("NameDraft");var status=Find<ComboBox>("StatusBox");
        var systemTheme=window.VisualRoot.ActualTheme;
        Check(window.SystemBackdrop is MicaBackdrop,"native-mica");Check(Find<TitleBar>("NativeTitleBar").Title=="AHK Manager"&&window.ExtendsContentIntoTitleBar,"native-localized-titlebar");
        var nav=Find<NavigationView>("Navigation");Check(nav.MenuItems.OfType<NavigationViewItem>().First().Content?.ToString()=="Scripts","localized-visible-navigation");
        Check(list.Items.Count==3,"real-snapshot-rows");
        list.SelectedItem=window.ViewModel.Rows.First(row=>row.Id==other);await Task.Delay(50);
        Check(Find<Button>("PauseAction").IsEnabled&&Find<Button>("PauseAction").Content?.ToString()=="Alternar pausa","verified-running-identity-exposes-honest-pause-toggle-with-unknown-state");
        list.SelectedItem=window.ViewModel.Rows.First(r=>r.Id==id);Find<Expander>("MetadataExpander").IsExpanded=true;await Task.Delay(60);nameDraft.Text="Rascunho ainda não salvo";
        list.SelectedItem=window.ViewModel.Rows.First(r=>r.Id==other);await Task.Delay(40);list.SelectedItem=window.ViewModel.Rows.First(r=>r.Id==id);await Task.Delay(40);
        Check(nameDraft.Text=="Rascunho ainda não salvo","drafts-keyed-by-id");
        window.ApplyTheme(AppThemePreference.Light);await Task.Delay(150);Check(nameDraft.Text=="Rascunho ainda não salvo","theme-preserves-draft");
        Find<Expander>("MetadataExpander").IsExpanded=false;
        File.WriteAllText(Path.Combine(output,"rendering-diagnostics.txt"),$"ticks={renderTicks}\nloaded={window.VisualRoot.IsLoaded}\nhostVisible={window.VisualRoot.XamlRoot.IsHostVisible}\nactualWidth={window.VisualRoot.ActualWidth}\nactualHeight={window.VisualRoot.ActualHeight}\nopacity={window.VisualRoot.Opacity}\nvisibility={window.VisualRoot.Visibility}\nrequestedTheme={window.VisualRoot.RequestedTheme}\nactualTheme={window.VisualRoot.ActualTheme}\n");
        if(capture)await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"native-light-1240x820.png"));
        if(layoutReview)await CaptureLayoutAsync(window,Path.Combine(output,"layout-only-light-1240x820.png"),false);
        await ThemeAsync(AppThemePreference.Dark);if(capture)await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"native-dark-1240x820.png"));
        if(layoutReview)await CaptureLayoutAsync(window,Path.Combine(output,"layout-only-dark-1240x820.png"),true);
        window.AppWindow.Resize(new SizeInt32(1000,720));await Task.Delay(200);if(capture)await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"native-dark-1000x720.png"));
        if(layoutReview)await CaptureLayoutAsync(window,Path.Combine(output,"layout-only-dark-1000x720.png"),true);
        await ThemeAsync(AppThemePreference.Light);if(capture)await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"native-light-1000x720.png"));
        if(layoutReview)await CaptureLayoutAsync(window,Path.Combine(output,"layout-only-light-1000x720.png"),false);
        await ThemeAsync(AppThemePreference.System);Check(window.VisualRoot.RequestedTheme==ElementTheme.Default&&window.VisualRoot.ActualTheme==systemTheme,"return-to-live-system-theme");
        Check(Find<Button>("StartAction").ActualWidth>40&&Find<Button>("StartAction").ActualHeight>=30,"minimum-native-action-target");
        status.SelectedIndex=(int)StatusFilter.Stopped;await Task.Delay(40);Check(list.Items.Count==1,"native-status-filter");
        window.ViewModel.Busy=true;service.Publish(state with {Revision=2,Scripts=[state.Scripts[0] with {Status=ScriptStatus.Running},state.Scripts[1],state.Scripts[2]]});await Task.Delay(100);
        Check(window.ViewModel.EffectiveSelectedId is null&&!Find<Button>("StartAction").IsEnabled,"hidden-while-busy-no-effect-target");
        window.ViewModel.Busy=false;Check(window.ViewModel.SelectedId is null,"deferred-hidden-selection-reconciled");
        status.SelectedIndex=0;await Task.Delay(50);list.SelectedItem=window.ViewModel.Rows.First(r=>r.Id==unknown);await Task.Delay(50);
        Check(Find<TextBlock>("SelectedPath").Text.Contains("Observação incompleta")&&nameDraft.IsEnabled,"unknown-inspectable-editable");
        Check(!Find<Button>("StartAction").IsEnabled&&!Find<Button>("StopAction").IsEnabled,"unknown-process-actions-disabled");
        Check(Find<Button>("FolderAction").IsEnabled&&Find<Button>("RemoveAction").IsEnabled,"unknown-safe-actions-available");
        list.SelectedItem=window.ViewModel.Rows.First(r=>r.Id==id);await Task.Delay(50);Check(nameDraft.Text=="Rascunho ainda não salvo","draft-survives-filter-transition");
        var action=window.ExecuteOperationAsync(ScriptOperation.Stop);await Task.Delay(150);
        var dialog=ContentDialogIn(window.VisualRoot.XamlRoot);
        Check(dialog is not null,"actual-owned-contentdialog-open");
        if(dialog is not null){if(capture)await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"native-confirmation.png"));dialog.Hide();}
        await action;Check(service.Effects==0,"cancel-before-dispatch-no-effects");
        nameDraft.Text="Responsivo após cancelar";Check(nameDraft.Text=="Responsivo após cancelar"&&!window.ViewModel.Busy,"renderer-responsive-after-modal");
        var pending=window.ExecuteOperationAsync(ScriptOperation.Stop);await Task.Delay(100);service.Publish(service.Snapshot with {Revision=3,Scripts=[..service.Snapshot.Scripts.Select(s=>s.Id==id?s with {Status=ScriptStatus.Unknown}:s)]});await Task.Delay(100);
        ContentDialogIn(window.VisualRoot.XamlRoot)?.Hide();await pending;Check(service.Effects==0,"state-change-during-modal-no-effects");
        service.Publish(state with {Revision=4,Scripts=[state.Scripts[0] with {Status=ScriptStatus.Running},state.Scripts[1],state.Scripts[2]]});await Task.Delay(80);
        list.SelectedItem=window.ViewModel.Rows.First(r=>r.Id==id);await Task.Delay(40);service.Confirmation=false;
        service.PerformEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);service.ReleasePerform=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight=window.ExecuteOperationAsync(ScriptOperation.Stop);await service.PerformEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        status.SelectedIndex=(int)StatusFilter.Stopped;await Task.Delay(40);service.ReleasePerform.TrySetResult();await inFlight;
        Check(service.Effects==0,"query-change-during-native-boundary-await-cancels-before-effect");
        service.Publish(service.Snapshot with {ReadOnly=true,Revision=5,Issues=[new(ReasonCode.ReadOnlySettings)]});await Task.Delay(100);Check(!Find<Button>("SaveDraftAction").IsEnabled,"readonly-metadata-disabled");
        if(capture)await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"native-unknown-readonly.png"));
        var recovery=Find<InfoBar>("GlobalInfo").ActionButton as Button;Check(recovery is not null,"global-recovery-reachable");
        Check(AutomationProperties.GetName(list).Length>0&&AutomationProperties.GetName(search).Length>0,"native-accessible-control-names");
        Check(window.VisualRoot.KeyboardAccelerators.Count>=2,"native-keyboard-accelerators");
        service.Publish(service.Snapshot with {ReadOnly=false,Issues=[],Revision=6});await Task.Delay(60);
        var preferences=window.ShowSettingsAsync();await Task.Delay(100);
        var settingsDialog=ContentDialogIn(window.VisualRoot.XamlRoot)??throw new InvalidOperationException("Native settings dialog missing");
        var settingsPanel=(StackPanel)((ScrollViewer)settingsDialog.Content).Content;
        settingsPanel.Children.OfType<ComboBox>().Single(control=>control.Name=="SettingsTheme").SelectedIndex=(int)AppThemePreference.Dark;
        settingsPanel.Children.OfType<TextBox>().Single(control=>control.Name=="SettingsEditor").Text=Path.Combine(output,"editor preference.exe");
        Invoke(Descendant<Button>(settingsDialog,"PrimaryButton")??throw new InvalidOperationException("Native primary dialog button missing"));
        await preferences;await Task.Delay(60);
        Check(service.Snapshot.Settings.Theme==AppThemePreference.Dark&&window.VisualRoot.RequestedTheme==ElementTheme.Dark,"native-settings-save-applies-theme-preference");
        Check(service.Snapshot.Settings.EditorPath==Path.Combine(output,"editor preference.exe"),"native-settings-editor-preference-reaches-service");
        var cancelledPreferences=window.ShowSettingsAsync();await Task.Delay(80);settingsDialog=ContentDialogIn(window.VisualRoot.XamlRoot)!;
        settingsPanel=(StackPanel)((ScrollViewer)settingsDialog.Content).Content;
        settingsPanel.Children.OfType<ComboBox>().Single(control=>control.Name=="SettingsTheme").SelectedIndex=(int)AppThemePreference.Light;
        settingsDialog.Hide();await cancelledPreferences;Check(service.Snapshot.Settings.Theme==AppThemePreference.Dark,"native-settings-cancel-keeps-saved-preference");
        nav.SelectedItem=nav.MenuItems.OfType<NavigationViewItem>().Single(item=>item.Tag?.ToString()=="collections");await Task.Delay(50);
        Invoke(Find<Button>("OtherAction"));await Task.Delay(80);var collectionDialog=ContentDialogIn(window.VisualRoot.XamlRoot)??throw new InvalidOperationException("Native collection dialog missing");
        ((StackPanel)collectionDialog.Content).Children.OfType<TextBox>().Single(control=>control.Name=="CollectionName").Text="Coleção de teste nativa";
        bool primaryClicked=false;var collectionClosed=new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        collectionDialog.PrimaryButtonClick+=(_,_)=>primaryClicked=true;collectionDialog.Closed+=(_,args)=>collectionClosed.TrySetResult(args.Result);
        Invoke(Descendant<Button>(collectionDialog,"PrimaryButton")!);var collectionResult=await collectionClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if(window.ViewModel.Busy)
        {
            var idle=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);EventHandler changed=(_,_)=>{if(!window.ViewModel.Busy)idle.TrySetResult();};window.ViewModel.PresentationChanged+=changed;
            try {if(!window.ViewModel.Busy)idle.TrySetResult();await idle.Task.WaitAsync(TimeSpan.FromSeconds(2));}finally {window.ViewModel.PresentationChanged-=changed;}
        }
        File.WriteAllText(Path.Combine(output,"collection-native-result.txt"),$"primaryClicked={primaryClicked}\nresult={collectionResult}\nserviceCalls={service.SavedCollections.Count}\nbusy={window.ViewModel.Busy}\n");
        Check(primaryClicked&&collectionResult==ContentDialogResult.Primary,"native-collection-primary-command-submitted");
        Check(service.SavedCollections.Single().Name=="Coleção de teste nativa","native-collection-editor-submits-explicit-name");
        await NativeLifecycleSuite.RunAsync(output,Check);
        var modules=new Dictionary<string,string>();using(var process=Process.GetCurrentProcess())foreach(ProcessModule module in process.Modules)if(module.ModuleName is "coreclr.dll" or "Microsoft.UI.Xaml.dll")modules[module.ModuleName]=module.FileName;
        foreach(var module in modules)Check(Path.GetFullPath(module.Value).StartsWith(Path.GetFullPath(AppContext.BaseDirectory),StringComparison.OrdinalIgnoreCase),"payload-runtime-"+module.Key);
        File.WriteAllLines(Path.Combine(output,"checks.txt"),checks);File.WriteAllLines(Path.Combine(output,"runtime-modules.txt"),modules.Select(p=>p.Key+"="+p.Value));
        window.Close();
        CompositionTarget.Rendering-=onRendering;
        async Task ThemeAsync(AppThemePreference theme){await service.SaveSettingsAsync(service.Snapshot.Settings with {Theme=theme});await Task.Delay(150);Check(window.VisualRoot.RequestedTheme==(theme==AppThemePreference.System?ElementTheme.Default:theme==AppThemePreference.Dark?ElementTheme.Dark:ElementTheme.Light),"native-theme-preference-"+theme);}
    }
    static async Task CaptureLayoutAsync(MainWindow window,string path,bool dark)
    {
        var root=(Grid)window.VisualRoot;var background=root.Background;
        try
        {
            // Layout review only. A neutral test brush makes alpha readable; this
            // does not depict or validate the native frame, Mica or composition.
            root.Background=new SolidColorBrush(dark?Microsoft.UI.Colors.Black:Microsoft.UI.Colors.White);
            var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();await bitmap.RenderAsync(root);
            var buffer=await bitmap.GetPixelsAsync();byte[] pixels=System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer);
            File.WriteAllBytes(path,[]);var file=await Windows.Storage.StorageFile.GetFileFromPathAsync(path);using var stream=await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var encoder=await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);await encoder.FlushAsync();
            File.WriteAllText(path+".scope.txt","TEST-ONLY RenderTargetBitmap layout review, neutral harness brush. Native frame/Mica/normal first presentation NOT validated.\n");
        }
        finally {root.Background=background;}
    }
    static ContentDialog? ContentDialogIn(XamlRoot root)
    {
        foreach(var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))if(FindDialog(popup.Child) is {} dialog)return dialog;
        return null;
    }
    static void Invoke(Button button)=>((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    static T? Descendant<T>(DependencyObject node,string name) where T:FrameworkElement
    {
        if(node is T match&&match.Name==name)return match;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(Descendant<T>(VisualTreeHelper.GetChild(node,i),name) is {} found)return found;
        return null;
    }
    static ContentDialog? FindDialog(DependencyObject? node)
    {
        if(node is ContentDialog dialog)return dialog;
        if(node is null)return null;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(FindDialog(VisualTreeHelper.GetChild(node,i)) is {} found)return found;
        return null;
    }
}
