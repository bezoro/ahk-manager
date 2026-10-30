using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AhkManager.TestSupport;

public static class PortableDataPickerUi
{
    delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr window, uint message, IntPtr first, IntPtr second);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr first, string second, uint flags, uint timeout, out UIntPtr result);

    static string Text(IntPtr window) { var text = new StringBuilder(4096); GetWindowTextW(window, text, text.Capacity); return text.ToString(); }
    static string Class(IntPtr window) { var text = new StringBuilder(128); GetClassNameW(window, text, text.Capacity); return text.ToString(); }
    static bool Owned(IntPtr window, int pid, string image, DateTime started)
    {
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != pid) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime() == started && string.Equals(process.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    static IntPtr Dialog(int pid, string image, DateTime started)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) => {
            if (Owned(window, pid, image, started) && IsWindowVisible(window) && Class(window) == "#32770" && Text(window) == "Choose data folder") result = result == IntPtr.Zero ? window : new IntPtr(-1);
            return true;
        }, IntPtr.Zero);
        return result == new IntPtr(-1) ? IntPtr.Zero : result;
    }

    static bool FolderNameEdit(IntPtr window, IntPtr dialog)
    {
        if (Class(window) != "Edit") return false;
        for (var current = window; current != IntPtr.Zero && current != dialog; current = GetParent(current))
            if (GetDlgCtrlID(current) is 1148 or 1152) return true;
        return false;
    }

    // Only the native picker supplies the product's chosen path. The test sends
    // no path through the renderer API and never interacts with another app.
    public static bool Choose(int pid, string image, DateTime started, string directory)
    {
        var dialog = Dialog(pid, image, started);
        if (dialog == IntPtr.Zero) return false;
        var edits = new List<IntPtr>();
        var buttons = new List<IntPtr>();
        EnumChildWindows(dialog, (window, _) => {
            if (!Owned(window, pid, image, started) || !IsWindowVisible(window) || !IsWindowEnabled(window)) return true;
            if (FolderNameEdit(window, dialog)) edits.Add(window);
            if (GetDlgCtrlID(window) == 1 && Class(window) == "Button" && Text(window).Replace("&", "", StringComparison.Ordinal) == "Use this folder") buttons.Add(window);
            return true;
        }, IntPtr.Zero);
        if (edits.Count != 1 || buttons.Count != 1) return false;
        if (!Owned(dialog, pid, image, started) || !Owned(edits[0], pid, image, started)) return false;
        if (SendMessageTimeoutW(edits[0], 0x000C, IntPtr.Zero, directory, 0x0002, 2000, out var result) == IntPtr.Zero || result == UIntPtr.Zero || Text(edits[0]) != directory) return false;
        return Owned(dialog, pid, image, started) && Owned(buttons[0], pid, image, started) && PostMessageW(buttons[0], 0x00F5, IntPtr.Zero, IntPtr.Zero);
    }

    public static string Describe(int pid, string image, DateTime started)
    {
        var dialog = Dialog(pid, image, started);
        if (dialog == IntPtr.Zero) return "The exact owned Choose data folder dialog is absent or ambiguous.";
        var text = new StringBuilder();
        EnumChildWindows(dialog, (window, _) => {
            if (Owned(window, pid, image, started)) text.AppendLine($"{GetDlgCtrlID(window)}\t{Class(window)}\tvisible={IsWindowVisible(window)}\tenabled={IsWindowEnabled(window)}\t{Text(window)}");
            return text.Length < 32768;
        }, IntPtr.Zero);
        return text.ToString();
    }
}
