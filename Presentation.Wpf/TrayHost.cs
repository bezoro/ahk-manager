using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Bezoro.AhkManager.Presentation;

// The OS host is constructed only by the normal application branch.
public sealed class TrayHost : IDisposable
{
    readonly MainWindow window;
    readonly Application app;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(60) };
    readonly HwndSource source;
    readonly nint icon;
    readonly TrayRecovery recovery;
    readonly ExitCoordinator exitCoordinator;
    readonly StopAllExitCoordinator stopAllExit;
    bool exiting, disposed;
    const int Callback = 0x8001;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid GuidItem; public nint BalloonIcon;
    }
    public static int NativeDataSize => Marshal.SizeOf<NotifyData>();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    public static HwndSourceParameters SourceParameters() => new("AHK Manager tray") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = 0 };
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = source.Handle, Id = 1, Flags = 1 | 2 | 4, CallbackMessage = Callback, Icon = icon, Tip = "Bezoro AHK Manager - abrir ou sair", Info = "", InfoTitle = "" };
    public TrayHost(Application app, MainWindow window, Action? approvedClosing = null)
    {
        this.app = app; this.window = window;
        source = new HwndSource(SourceParameters()); source.AddHook(Hook);
        _ = ExtractIconEx(Environment.ProcessPath ?? "", 0, out var large, out var small, 1); icon = small;
        if (large != 0) DestroyIcon(large);
        var data = Data();
        if (icon == 0 || !Shell_NotifyIcon(0, ref data)) { source.Dispose(); if (icon != 0) DestroyIcon(icon); throw new Win32Exception("Nao foi possivel criar a bandeja. Nenhuma supervisao iniciada."); }
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (taskbarCreated == 0) { var remove = Data(); _ = Shell_NotifyIcon(2, ref remove); source.Dispose(); DestroyIcon(icon); throw new Win32Exception("Nao foi possivel registrar recuperacao da bandeja."); }
        recovery = new((int)taskbarCreated, AddIcon, available =>
        {
            window.TrayAvailable = available;
            if (!available) { ShowExistingWindow(); window.Session.ReportTrayFailure(); }
        });
        exitCoordinator = new(window.Session, ConfirmExit,
            () => { approvedClosing?.Invoke(); exiting = true; timer.Stop(); },
            () => { window.CloseToTray = false; window.Close(); app.Shutdown(); }, TimeSpan.FromSeconds(15));
        stopAllExit = new(window.Session, exitCoordinator, () => timer.Stop(), () => timer.Start(), (result, token) => Task.FromResult(BatchResultWindow.Show(result, window, true, token)));
        window.CloseToTray = true;
        window.Session.ExitRequested += ExitClicked;
        app.SessionEnding += SessionEnding;
        timer.Tick += Tick; timer.Start();
    }
    void ShowExistingWindow()
    {
        if (!disposed && !exiting) _ = ExistingWindowDisplay.Show(new WpfExistingWindow(window), CancellationToken.None);
    }
    bool AddIcon() { var data = Data(); return Shell_NotifyIcon(0, ref data); }
    async void Tick(object? sender, EventArgs args)
    {
        if (exiting || disposed) return;
        if (!recovery.Available && !recovery.Restore()) return;
        await window.Session.Reconcile();
    }
    nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed || exiting) return 0;
        // This is a hidden top-level HWND, not a message-only window: it receives broadcasts.
        if (recovery is not null && recovery.HandleMessage(message)) { handled = true; return 0; }
        if (message != Callback) return 0;
        if ((int)lParam is 0x202 or 0x203) { ShowExistingWindow(); handled = true; }
        else if ((int)lParam == 0x205)
        {
            var menu = new ContextMenu();
            var show = new MenuItem { Header = "Abrir gerenciador" }; show.Click += (_, _) => { ShowExistingWindow(); };
            var exit = new MenuItem { Header = "Sair (deixar scripts vivos)" }; exit.Click += (_, _) => ExitClicked();
            var stopExit = new MenuItem { Header = "Sair e parar todos os scripts" }; stopExit.Click += async (_, _) => { if (!exiting && !disposed) await stopAllExit.Request(); };
            menu.Items.Add(show); menu.Items.Add(exit); menu.Items.Add(stopExit); menu.IsOpen = true; handled = true;
        }
        return 0;
    }
    Task<bool> ConfirmExit()
    {
        var confirm = new ConfirmationWindow("Sair", "Encerrar somente o gerenciador? Scripts existentes continuarao vivos. As escolhas desta sessão permanecem; somente o próximo login configurado aplica as escolhas de inicialização.") { Owner = window.IsVisible ? window : null };
        return Task.FromResult(confirm.ShowDialog() == true);
    }
    async void ExitClicked()
    {
        if (exiting || disposed || stopAllExit.Active) return;
        await exitCoordinator.Request();
    }
    async void SessionEnding(object? sender, SessionEndingCancelEventArgs args)
    {
        timer.Stop(); args.Cancel = false;
        if (!exiting) await exitCoordinator.FinishConfirmed();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop();
        window.Session.ExitRequested -= ExitClicked; app.SessionEnding -= SessionEnding;
        var data = Data(); _ = Shell_NotifyIcon(2, ref data); source.Dispose(); if (icon != 0) DestroyIcon(icon);
    }
}

// Typed recovery boundary exercises TaskbarCreated/re-add/failure without restarting Explorer.
public sealed class TrayRecovery(int taskbarCreated, Func<bool> addIcon, Action<bool> availabilityChanged)
{
    public bool Available { get; private set; } = true;
    public bool HandleMessage(int message)
    {
        if (taskbarCreated == 0 || message != taskbarCreated) return false;
        Restore(); return true;
    }
    public bool Restore()
    {
        Available = addIcon(); availabilityChanged(Available); return Available;
    }
}
