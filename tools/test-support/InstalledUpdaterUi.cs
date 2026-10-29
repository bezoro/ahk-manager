using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AhkManager.TestSupport;

public static class InstalledUpdaterUi
{
    sealed class OutputCapture : IDisposable
    {
        readonly Process process;
        readonly StreamWriter output;
        readonly StreamWriter error;
        readonly object gate = new();
        int outputBytes,
            errorBytes;
        bool disposed;

        internal OutputCapture(Process process, string outputPath, string errorPath)
        {
            this.process = process;
            output = new StreamWriter(outputPath, false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            error = new StreamWriter(errorPath, false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            process.OutputDataReceived += Output;
            process.ErrorDataReceived += Error;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        void Output(object sender, DataReceivedEventArgs value) =>
            Write(output, value.Data, ref outputBytes);

        void Error(object sender, DataReceivedEventArgs value) =>
            Write(error, value.Data, ref errorBytes);

        void Write(StreamWriter writer, string line, ref int count)
        {
            lock (gate)
            {
                if (disposed || line is null || count >= 256 * 1024)
                    return;
                line = line[..Math.Min(line.Length, 4096)];
                writer.WriteLine(line);
                count += Encoding.UTF8.GetByteCount(line) + 2;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                process.OutputDataReceived -= Output;
                process.ErrorDataReceived -= Error;
                output.Dispose();
                error.Dispose();
            }
        }
    }

    public static IDisposable Capture(Process process, string outputPath, string errorPath) =>
        new OutputCapture(process, outputPath, errorPath);

    delegate bool WindowCallback(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(WindowCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int limit);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder text, int limit);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint first, uint second, bool attach);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool ShowWindowAsync(IntPtr window, int command);

    static string Text(IntPtr window)
    {
        var text = new StringBuilder(4096);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    static string ClassName(IntPtr window)
    {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    public static bool Matches(int pid, string executable, DateTime started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && process.StartTime.ToUniversalTime() == started
                && string.Equals(
                    process.MainModule?.FileName,
                    executable,
                    StringComparison.OrdinalIgnoreCase
                );
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool Owned(IntPtr window, int pid, string executable, DateTime started)
    {
        GetWindowThreadProcessId(window, out uint owner);
        return owner == pid && Matches(pid, executable, started);
    }

    // The test may activate or close only this exact installed Manager session.
    static bool MainWindow(int pid, string executable, DateTime started, bool close)
    {
        bool dispatched = false;
        EnumWindows(
            (window, _) =>
            {
                if (
                    !Owned(window, pid, executable, started)
                    || !ClassName(window).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)
                    || !IsWindowVisible(window)
                )
                    return true;
                if (close)
                    dispatched |= PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                else
                {
                    uint caller = GetCurrentThreadId();
                    uint foreground = GetWindowThreadProcessId(
                        GetForegroundWindow(),
                        out uint foregroundPid
                    );
                    bool attached =
                        foreground != 0
                        && foreground != caller
                        && AttachThreadInput(caller, foreground, true);
                    try
                    {
                        ShowWindowAsync(window, 9);
                        dispatched |= SetForegroundWindow(window);
                    }
                    finally
                    {
                        if (attached)
                            AttachThreadInput(caller, foreground, false);
                    }
                }
                return true;
            },
            IntPtr.Zero
        );
        return dispatched;
    }

    public static bool Focus(int pid, string executable, DateTime started) =>
        MainWindow(pid, executable, started, close: false);

    public static bool Close(int pid, string executable, DateTime started) =>
        MainWindow(pid, executable, started, close: true);

    public static string[] Describe(int pid, string executable, DateTime started)
    {
        var result = new List<string>();
        EnumWindows(
            (window, _) =>
            {
                if (!Owned(window, pid, executable, started) || !IsWindowVisible(window))
                    return true;
                result.Add(
                    $"window class={ClassName(window)}; enabled={IsWindowEnabled(window)}; foreground={GetForegroundWindow() == window}; text={Text(window)}"
                );
                EnumChildWindows(
                    window,
                    (child, state) =>
                    {
                        if (result.Count >= 100)
                            return false;
                        if (Owned(child, pid, executable, started))
                            result.Add(
                                $"child class={ClassName(child)}; enabled={IsWindowEnabled(child)}; text={Text(child)}"
                            );
                        return true;
                    },
                    IntPtr.Zero
                );
                return result.Count < 100;
            },
            IntPtr.Zero
        );
        return result.ToArray();
    }

    // No generic dialog acceptance: require the exact version and the production
    // consent text, then post only the Confirm button belonging to this session.
    public static bool Confirm(int pid, string executable, DateTime started, string version)
    {
        bool dispatched = false;
        EnumWindows(
            (window, _) =>
            {
                if (
                    dispatched
                    || !Owned(window, pid, executable, started)
                    || !IsWindowVisible(window)
                )
                    return true;
                var content = new StringBuilder(Text(window));
                var buttons = new List<IntPtr>();
                EnumChildWindows(
                    window,
                    (child, state) =>
                    {
                        content.Append('\n').Append(Text(child));
                        if (
                            string.Equals(ClassName(child), "Button", StringComparison.Ordinal)
                            && Text(child).Replace("&", "").Trim() == "Confirm"
                        )
                            buttons.Add(child);
                        return true;
                    },
                    IntPtr.Zero
                );
                string question = content.ToString();
                if (
                    buttons.Count != 1
                    || !(
                        question.Contains(
                            "Update to version " + version + "?",
                            StringComparison.Ordinal
                        )
                        || question.Contains(
                            "Return to version " + version + "?",
                            StringComparison.Ordinal
                        )
                    )
                    || !question.Contains(
                        "The manager will close and reopen.",
                        StringComparison.Ordinal
                    )
                    || !question.Contains(
                        "Running scripts will keep running",
                        StringComparison.Ordinal
                    )
                )
                    return true;
                IntPtr button = buttons[0];
                if (
                    Owned(button, pid, executable, started)
                    && IsWindowEnabled(button)
                    && Matches(pid, executable, started)
                )
                    dispatched = PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                return true;
            },
            IntPtr.Zero
        );
        return dispatched;
    }
}
