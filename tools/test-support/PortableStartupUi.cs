using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AhkManager.TestSupport;

public static class PortableStartupUi
{
    public static string LastObservation { get; private set; } = "No refusal inspection has run.";
    const string Refusal = "The portable package or its data settings are invalid. Extract a fresh portable ZIP into a separate folder. Keep your existing Data folder and portable.json for recovery.";
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

    static bool ExpectedContent(string title, string body) =>
        body.Contains(Refusal, StringComparison.Ordinal) && (title == "Error"
            ? body.Contains("\nAHK Manager\n", StringComparison.Ordinal)
            : title == "AHK Manager"
                && body.Contains("Failure code: invalid-package", StringComparison.Ordinal)
                && body.Contains("Phase: portable-initialization", StringComparison.Ordinal)
                && body.Contains("Stage: verify-root-members", StringComparison.Ordinal)
                && body.Contains("Resource: root", StringComparison.Ordinal));

    static bool ExpectedButtons(bool legacy, int count, int firstId, HashSet<string> labels) =>
        legacy ? count == 1 && labels.SetEquals(new[] { "OK" }) && firstId is 0 or 1
        : count == labels.Count && (
            labels.SetEquals(new[] { "Copy diagnostic details", "Open diagnostics folder", "Close" })
            || labels.SetEquals(new[] { "Copy diagnostic details", "Close" }));

    public static bool DismissRefusal(
        int pid,
        string image,
        DateTime started,
        Func<IntPtr, int, string, DateTime, string> readDialogText
    )
    {
        bool closed = false;
        LastObservation = "No exact owned visible Error task dialog was found.";
        EnumWindows((window, _) => {
            if (closed || !Owned(window, pid, image, started) || !IsWindowVisible(window) || Class(window) != "#32770") return true;
            string title = Text(window);
            bool legacy = title == "Error";
            if (!legacy && title != "AHK Manager") return true;
            string body;
            try { body = readDialogText(window, pid, image, started); }
            catch (Exception error) { LastObservation = "The exact owned dialog reader failed: " + error.GetType().Name + ": " + error.Message[..Math.Min(error.Message.Length, 300)]; return true; }
            if (!ExpectedContent(title, body)) { LastObservation = "The exact owned refusal dialog did not yet expose the expected app instructions and diagnostic context. Read characters=" + body.Length; return true; }
            var buttons = new List<IntPtr>();
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var observed = new List<string>();
            EnumChildWindows(window, (child, state) => {
                if (Owned(child, pid, image, started) && Class(child) == "Button") {
                    observed.Add($"id={GetDlgCtrlID(child)},visible={IsWindowVisible(child)},enabled={IsWindowEnabled(child)}");
                    if (IsWindowVisible(child) && IsWindowEnabled(child)) {
                        buttons.Add(child);
                        labels.Add(Text(child).Replace("&", "", StringComparison.Ordinal).Trim());
                    }
                }
                return buttons.Count <= 3;
            }, IntPtr.Zero);
            bool recognized = ExpectedButtons(legacy, buttons.Count, buttons.Count > 0 ? GetDlgCtrlID(buttons[0]) : -1, labels);
            // Close only the exact owned refusal. WM_CLOSE selects the safe
            // cancel action, never clipboard copying or opening another process.
            if (recognized && Owned(window, pid, image, started) && buttons.TrueForAll(button => Owned(button, pid, image, started)) && Text(window) == title) closed = PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            LastObservation = "The exact owned app refusal matched. Posted close=" + closed + "; buttons=" + string.Join(";", observed);
            return !closed;
        }, IntPtr.Zero);
        return closed;
    }
}
