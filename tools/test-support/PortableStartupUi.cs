using System;
using System.Collections.Generic;
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
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
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

    public static bool DismissRefusal(
        int pid,
        string image,
        DateTime started,
        Func<IntPtr, int, string, DateTime, string> readDialogText
    )
    {
        bool closed = false;
        EnumWindows((window, _) => {
            if (closed || !Owned(window, pid, image, started) || !IsWindowVisible(window) || Class(window) != "#32770" || Text(window) != "Error") return true;
            string body;
            try { body = readDialogText(window, pid, image, started); }
            catch (Exception) { return true; }
            if (!body.Contains("\nAHK Manager\n", StringComparison.Ordinal) || !body.Contains(Refusal, StringComparison.Ordinal)) return true;
            var buttons = new List<IntPtr>();
            EnumChildWindows(window, (child, state) => {
                if (Owned(child, pid, image, started) && IsWindowVisible(child) && IsWindowEnabled(child) && Class(child) == "Button" && GetDlgCtrlID(child) == 1 && Text(child).Replace("&", "", StringComparison.Ordinal).Trim() == "OK") buttons.Add(child);
                return buttons.Count <= 1;
            }, IntPtr.Zero);
            if (buttons.Count == 1 && Owned(window, pid, image, started) && Owned(buttons[0], pid, image, started) && Text(window) == "Error") closed = PostMessageW(buttons[0], 0x00F5, IntPtr.Zero, IntPtr.Zero);
            return !closed;
        }, IntPtr.Zero);
        return closed;
    }
}
