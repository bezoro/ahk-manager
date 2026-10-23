using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

internal static class NativeRenderDiagnosticSuite
{
    public static async Task RunAsync(string output)
    {
        var root=new Grid {Name="MinimalNativeRoot",RequestedTheme=ElementTheme.Light,Background=new SolidColorBrush(Microsoft.UI.Colors.White)};
        root.Children.Add(new TextBlock {Text="Native WinUI first presentation",Foreground=new SolidColorBrush(Microsoft.UI.Colors.Black),FontSize=28,Margin=new Thickness(24)});
        root.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle {Width=160,Height=100,Fill=new SolidColorBrush(Microsoft.UI.Colors.Red),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(30,110,0,0)});
        var window=new Window {Title="Owned native render diagnostic",Content=root};window.AppWindow.Resize(new SizeInt32(800,600));window.Activate();
        try
        {
            await Task.Delay(700);
            OwnedWindowInventory.Write(output);
            await NativeWindowCapture.CaptureAsync(window,Path.Combine(output,"minimal-native-marker.png"),true);
            Console.WriteLine("PASS native text-and-shape scene presented without RTB");
        }
        finally {window.Close();}
    }
}
