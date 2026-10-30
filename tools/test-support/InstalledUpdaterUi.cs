using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AhkManager.TestSupport;

public static class InstalledUpdaterUi
{
    static readonly object accessibilityGate = new();
    static readonly Dictionary<string, Task<string>> accessibilityReads = new();

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

    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(
        IntPtr window,
        uint objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object accessible
    );

    [DllImport("oleacc.dll")]
    static extern int AccessibleChildren(
        IntPtr accessible,
        int first,
        int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
        out int obtained
    );

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

    static object AccessibleProperty(
        object accessible,
        string property,
        params object[] arguments
    ) =>
        accessible
            .GetType()
            .InvokeMember(property, BindingFlags.GetProperty, null, accessible, arguments);

    static int Children(object accessible, object[] children, out int obtained)
    {
        obtained = 0;
        IntPtr unknown = Marshal.GetIUnknownForObject(accessible);
        IntPtr dispatch = IntPtr.Zero;
        try
        {
            var interfaceId = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
            int result = Marshal.QueryInterface(unknown, in interfaceId, out dispatch);
            return result < 0
                ? result
                : AccessibleChildren(dispatch, 0, children.Length, children, out obtained);
        }
        finally
        {
            if (dispatch != IntPtr.Zero)
                Marshal.Release(dispatch);
            Marshal.Release(unknown);
        }
    }

    static string ReadAccessibleText(IntPtr window, int pid, string executable, DateTime started)
    {
        if (!Owned(window, pid, executable, started))
            return "";
        // IAccessible inherits IDispatch. Late-bound property reads use MSAA
        // without requiring Windows Desktop assemblies in the fixture runtime.
        var interfaceId = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
        if (AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref interfaceId, out var root) < 0)
            return "";
        var text = new StringBuilder();
        int visited = 0;

        void Name(object accessible, object child)
        {
            foreach (string property in new[] { "accName", "accDescription", "accValue" })
                try
                {
                    if (
                        text.Length < 16384
                        && AccessibleProperty(accessible, property, child) is string value
                    )
                    {
                        text.Append('\n');
                        text.Append(value.AsSpan(0, Math.Min(value.Length, 16384 - text.Length)));
                    }
                }
                catch (Exception) { }
        }

        void Visit(object accessible, int depth)
        {
            if (++visited > 128 || depth > 8 || text.Length >= 16384)
                return;
            Name(accessible, 0);
            int count;
            try
            {
                count = Math.Clamp(
                    Convert.ToInt32(AccessibleProperty(accessible, "accChildCount")),
                    0,
                    128 - visited
                );
            }
            catch (Exception)
            {
                return;
            }
            if (count == 0)
                return;
            var children = new object[count];
            if (Children(accessible, children, out int obtained) < 0)
                return;
            for (int index = 0; index < Math.Min(obtained, count); index++)
            {
                if (text.Length >= 16384 || visited >= 128)
                    break;
                if (children[index] is int child)
                {
                    visited++;
                    Name(accessible, child);
                }
                else if (children[index] is { } descendant)
                    Visit(descendant, depth + 1);
            }
        }

        try
        {
            Visit(root, 0);
        }
        catch (Exception)
        {
            return "";
        }
        return Owned(window, pid, executable, started) ? text.ToString() : "";
    }

    static string AccessibleText(IntPtr window, int pid, string executable, DateTime started)
    {
        string key = $"{pid}:{started.Ticks}:{window.ToInt64()}";
        Task<string> reading;
        bool created = false;
        lock (accessibilityGate)
        {
            var completed = new List<string>();
            foreach (var entry in accessibilityReads)
                if (entry.Value.IsCompleted)
                    completed.Add(entry.Key);
            foreach (string previous in completed)
                accessibilityReads.Remove(previous);
            if (!accessibilityReads.TryGetValue(key, out reading))
            {
                if (accessibilityReads.Count >= 8)
                    return "";
                reading = Task.Run(() => ReadAccessibleText(window, pid, executable, started));
                accessibilityReads.Add(key, reading);
                created = true;
            }
        }
        // A hung provider cannot block the driver or create repeated workers.
        // Thread-pool workers are background threads; no process is terminated.
        try
        {
            return reading.Wait(created ? 250 : 0) && Owned(window, pid, executable, started)
                ? reading.GetAwaiter().GetResult()
                : "";
        }
        catch (Exception)
        {
            return "";
        }
        finally
        {
            // Completed snapshots never authorize a later dialog using a reused
            // HWND, and a partially populated accessibility tree can be retried.
            if (reading.IsCompleted)
                lock (accessibilityGate)
                    if (
                        accessibilityReads.TryGetValue(key, out var current)
                        && ReferenceEquals(current, reading)
                    )
                        accessibilityReads.Remove(key);
        }
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

    public static string AccessibleDialogText(
        IntPtr window,
        int pid,
        string executable,
        DateTime started
    ) =>
        Owned(window, pid, executable, started)
        && IsWindowVisible(window)
        && ClassName(window) == "#32770"
            ? AccessibleText(window, pid, executable, started)
            : "";

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

    public static bool CloseAndWait(int pid, string executable, DateTime earliest)
    {
        Process process = null;
        DateTime started;
        try
        {
            process = Process.GetProcessById(pid);
            // Enumeration includes short-lived Chromium children. Retain the
            // OS handle before reading identity or dispatching WM_CLOSE so a
            // normal fast exit still has an observable exit code.
            _ = process.SafeHandle;
            if (process.HasExited)
            {
                process.Dispose();
                return false;
            }
            started = process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            return false;
        }
        catch (InvalidOperationException)
        {
            process?.Dispose();
            return false;
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 87)
        {
            process?.Dispose();
            return false;
        }
        catch
        {
            process?.Dispose();
            throw;
        }

        using (process)
        {
            if (
                started < earliest
                || !Matches(pid, executable, started)
                || !Close(pid, executable, started)
            )
                return false;
            if (!process.WaitForExit(30000))
                throw new InvalidOperationException(
                    "The test Manager did not close normally. It was preserved."
                );
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"The test Manager close returned {process.ExitCode}."
                );
            return true;
        }
    }

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
                if (ClassName(window) == "#32770")
                    result.Add(
                        "accessible text=" + AccessibleText(window, pid, executable, started)
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
                const string detail =
                    "The manager will close and reopen. Running scripts will keep running, and your settings will be preserved.";
                bool targetMatches =
                    question.Contains(
                        "Update to version " + version + "?",
                        StringComparison.Ordinal
                    )
                    || question.Contains(
                        "Return to version " + version + "?",
                        StringComparison.Ordinal
                    );
                if (
                    buttons.Count == 1
                    && ClassName(window) == "#32770"
                    && (!targetMatches || !question.Contains(detail, StringComparison.Ordinal))
                )
                {
                    question += AccessibleText(window, pid, executable, started);
                    targetMatches =
                        question.Contains(
                            "Update to version " + version + "?",
                            StringComparison.Ordinal
                        )
                        || question.Contains(
                            "Return to version " + version + "?",
                            StringComparison.Ordinal
                        );
                }
                if (
                    buttons.Count != 1
                    || !targetMatches
                    || !question.Contains(detail, StringComparison.Ordinal)
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
