using Microsoft.UI.Windowing;
using Windows.Graphics;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

internal static class WindowPlacementService
{
    public static void Restore(AppWindow window, WindowPlacement saved)
    {
        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea;
        double dpi = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id));
        double ratio = saved.Dpi is >= 48 and <= 768 && dpi > 0 ? dpi / saved.Dpi : 1;
        int width = Math.Clamp((int)Math.Round(saved.Width * ratio), Math.Min(1000, area.Width), area.Width);
        int height = Math.Clamp((int)Math.Round(saved.Height * ratio), Math.Min(720, area.Height), area.Height);
        int x = Math.Clamp(saved.X, area.X, area.X + area.Width - width);
        int y = Math.Clamp(saved.Y, area.Y, area.Y + area.Height - height);
        window.MoveAndResize(new RectInt32(x, y, width, height));
        if (saved.Maximized && window.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }
    public static WindowPlacement Capture(AppWindow window) => new(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height, window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }, GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id)));
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
}
