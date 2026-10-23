using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Bezoro.AhkManager.WinUI.Resources;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

internal sealed class TrayService : IDisposable
{
    const uint Callback = 0x8001, Destroy = 2, Command = 0x111;
    readonly DispatcherQueue dispatcher;
    readonly Action show, settings, exit;
    readonly Action hide;
    readonly Func<bool> visible;
    readonly Func<IReadOnlyList<string>> running;
    readonly Func<uint,bool>? notificationBoundary;
    readonly WndProc callback;
    readonly string className = "Bezoro.AhkManager.Tray." + Guid.NewGuid().ToString("N");
    readonly uint taskbarCreated;
    nint handle, icon;
    bool ownsIcon;
    bool disposed;
    public bool Registered { get; private set; }
    internal nint WindowHandle => handle;
    internal uint ShellRestartMessage => taskbarCreated;
    public TrayService(DispatcherQueue dispatcher, Action show, Action settings, Action exit, Func<IReadOnlyList<string>> running,Func<uint,bool>? notificationBoundary=null,Action? hide=null,Func<bool>? visible=null)
    {
        this.dispatcher = dispatcher; this.show = show; this.settings = settings; this.exit = exit; this.running = running;this.notificationBoundary=notificationBoundary;
        this.hide=hide??show;this.visible=visible??(()=>false);
        callback = WindowProc; taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var nativeClass = new WndClass { Size = (uint)Marshal.SizeOf<WndClass>(), Proc = Marshal.GetFunctionPointerForDelegate(callback), Instance = GetModuleHandle(null), ClassName = className };
        if (RegisterClassEx(ref nativeClass) == 0) return;
        handle = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, 0, 0, nativeClass.Instance, 0);
        string productIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        int iconSize = GetSystemMetricsForDpi(49, GetDpiForWindow(handle));
        icon = LoadImage(0, productIcon, 1, iconSize, iconSize, 0x10);
        ownsIcon = icon != 0;
        if (icon == 0) icon = LoadIcon(0, (nint)32512);
        Register();
    }
    void Register()
    {
        if (disposed || handle == 0 || icon == 0) return;
        var data = Data(); Registered = Notify(0, ref data);
        if (Registered) { data.TimeoutOrVersion = 4; Notify(4, ref data); }
    }
    bool Notify(uint command,ref NotifyData data)=>notificationBoundary?.Invoke(command)??Shell_NotifyIcon(command,ref data);
    NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = handle, Id = 1, Flags = 1 | 2 | 4, CallbackMessage = Callback, Icon = icon, Tip = Text.Get("ApplicationTitle"), Info = "", InfoTitle = "" };
    nint WindowProc(nint window, uint message, nuint wp, nint lp)
    {
        if (message == taskbarCreated) { Registered = false; Register(); if (!Registered) dispatcher.TryEnqueue(() => show()); return 0; }
        if (message == Callback)
        {
            uint action = (uint)((long)lp & 0xffff);
            if (action is 0x202 or 0x203 or 0x400) dispatcher.TryEnqueue(() => show());
            else if (action is 0x205 or 0x7b) Menu();
            return 0;
        }
        if (message == Command)
        {
            int id = (int)(wp & 0xffff); dispatcher.TryEnqueue(() => { if (id == 1) {if(visible())hide();else show();} else if (id == 2) settings(); else if (id == 3) exit(); }); return 0;
        }
        return DefWindowProc(window, message, wp, lp);
    }
    void Menu()
    {
        var menu = CreatePopupMenu(); if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, 1, Text.Get(visible()?"HideManager":"ShowManager"));
            var scripts = running();
            AppendMenu(menu, 2, 0, string.Format(Text.Get("TrayRunning"), scripts.Count));
            foreach (var script in scripts.Take(20)) AppendMenu(menu, 2, 0, script.Replace("&", "&&", StringComparison.Ordinal));
            AppendMenu(menu, 0, 2, Text.Get("Settings")); AppendMenu(menu, 0x800, 0, ""); AppendMenu(menu, 0, 3, Text.Get("ExitManager"));
            GetCursorPos(out var point); SetForegroundWindow(handle); TrackPopupMenu(menu, 0x2, point.X, point.Y, 0, handle, 0);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (Registered) { var data = Data(); Notify(2, ref data); Registered = false; }
        if (handle != 0) { DestroyWindow(handle); handle = 0; }
        if (ownsIcon && icon != 0) { DestroyIcon(icon); icon = 0; ownsIcon = false; }
        UnregisterClass(className, GetModuleHandle(null)); GC.KeepAlive(callback);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate nint WndProc(nint hwnd, uint message, nuint wp, nint lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WndClass { public uint Size, Style; public nint Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct NotifyData { public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip; public uint State, StateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint TimeoutOrVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle; public uint InfoFlags; public Guid Guid; public nint BalloonIcon; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern ushort RegisterClassEx(ref WndClass value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool UnregisterClass(string name,nint instance);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern nint CreateWindowEx(uint ex,string name,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern nint DefWindowProc(nint handle,uint message,nuint wp,nint lp);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern nint LoadIcon(nint instance,nint name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint LoadImage(nint instance,string name,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index,uint dpi);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message,ref NotifyData value);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool AppendMenu(nint menu,uint flags,nuint id,string value);
    [DllImport("user32.dll")] static extern bool TrackPopupMenu(nint menu,uint flags,int x,int y,int reserved,nint window,nint rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point value);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
}
