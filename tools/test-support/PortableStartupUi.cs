using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AhkManager.TestSupport;

public static class PortableStartupUi
{
    const string Refusal = "The portable folder could not be prepared. Extract the complete portable ZIP into its own local folder and check access to the app and data folders.";
    delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    static string Text(IntPtr window) { var text = new StringBuilder(4096); GetWindowTextW(window, text, text.Capacity); return text.ToString(); }
    static string Class(IntPtr window) { var text = new StringBuilder(128); GetClassNameW(window, text, text.Capacity); return text.ToString(); }
    static bool Owned(IntPtr window, int pid, string image, DateTime started)
    {
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != pid) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime() == started && string.Equals(process.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    public static bool DismissRefusal(int pid, string image, DateTime started)
    {
        bool closed = false;
        EnumWindows((window, _) => {
            if (closed || !Owned(window, pid, image, started) || !IsWindowVisible(window) || Class(window) != "#32770" || Text(window) != "AHK Manager") return true;
            var body = new StringBuilder();
            EnumChildWindows(window, (child, state) => { if (Owned(child, pid, image, started)) body.AppendLine(Text(child)); return body.Length < 16384; }, IntPtr.Zero);
            if (body.ToString().Contains(Refusal, StringComparison.Ordinal) && Owned(window, pid, image, started)) closed = PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return !closed;
        }, IntPtr.Zero);
        return closed;
    }
}
