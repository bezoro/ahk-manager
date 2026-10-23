using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.Graphics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Markup;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

// A code-only Application using the SDK's ordinary entry sequence. It deliberately
// avoids TestApp XAML, product services and RTB so the native first frame is decisive.
internal sealed class MinimalRenderApp : Application,IXamlMetadataProvider
{
    readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider metadata=new();
    public IXamlType GetXamlType(Type type)=>metadata.GetXamlType(type);
    public IXamlType GetXamlType(string name)=>metadata.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions()=>metadata.GetXmlnsDefinitions();
    readonly string output;
    readonly Action<int> complete;
    Window? window;
    public MinimalRenderApp(string output,Action<int> complete)
    {
        this.output=output;this.complete=complete;
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        UnhandledException+=(_,args)=>{File.WriteAllText(Path.Combine(output,"unhandled.txt"),args.Exception.ToString());complete(1);Exit();};
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var root=new Grid {RequestedTheme=ElementTheme.Light,Background=new SolidColorBrush(Microsoft.UI.Colors.White)};
        root.Children.Add(new TextBlock {Text="WinUI native first frame",Foreground=new SolidColorBrush(Microsoft.UI.Colors.Black),FontSize=30,Margin=new Thickness(30)});
        root.Children.Add(new Rectangle {Width=160,Height=100,Fill=new SolidColorBrush(Microsoft.UI.Colors.Red),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(30,110,0,0)});
        window=new Window {Title="Owned native minimal Application",Content=root};window.AppWindow.Resize(new SizeInt32(800,600));window.Activate();
        nint hwnd=WinRT.Interop.WindowNative.GetWindowHandle(window);bool foreground=SetForegroundWindow(hwnd);
        window.DispatcherQueue.TryEnqueue(async()=>
        {
            try
            {
                await Task.Delay(700);
                DwmGetWindowAttribute(hwnd,14,out int cloaked,sizeof(int));GetWindowDisplayAffinity(hwnd,out uint affinity);
                File.WriteAllText(Path.Combine(output,"native-state.txt"),$"hwnd={hwnd}\nforegroundRequest={foreground}\nforegroundHwnd={GetForegroundWindow()}\ncloaked={cloaked}\naffinity={affinity}\nloaded={root.IsLoaded}\nwidth={root.ActualWidth}\nheight={root.ActualHeight}\n");
                await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"minimal-native-marker.png"),true);
                File.WriteAllText(Path.Combine(output,"result.txt"),"PASS native marker presented without RTB\n");Console.WriteLine("PASS native marker presented without RTB");complete(0);
            }
            catch(Exception error){File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());complete(1);}
            finally {window.Close();Exit();}
        });
    }
    [DllImport("user32.dll")]static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]static extern bool GetWindowDisplayAffinity(nint window,out uint affinity);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(nint window,int attribute,out int value,int size);
}
