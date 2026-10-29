using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AhkManager.TestSupport;

public static class InstallerUi
{
    const uint ProcessSnapshot = 2;
    const uint ButtonClick = 0x00F5;
    delegate bool WindowCallback(IntPtr window, IntPtr state);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId;
        public uint Threads;
        public uint ParentId;
        public int Priority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string File;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

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

    [DllImport("user32.dll")]
    static extern IntPtr GetDlgItem(IntPtr window, int identifier);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int limit);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);

    static string Text(IntPtr window)
    {
        var text = new StringBuilder(4096);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    static bool Matches(int pid, string executable, DateTime started, bool exactStart = false)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            DateTime created = process.StartTime.ToUniversalTime();
            return !process.HasExited
                && (exactStart ? created == started : created >= started)
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

    static HashSet<int> InstallerProcesses(int root, string executable, DateTime started)
    {
        var allowed = new HashSet<int>();
        if (!Matches(root, executable, started, exactStart: true))
            return allowed;
        allowed.Add(root);
        var entries = new List<ProcessEntry>();
        IntPtr snapshot = CreateToolhelp32Snapshot(ProcessSnapshot, 0);
        if (snapshot == new IntPtr(-1))
            return allowed;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
                do
                {
                    entries.Add(entry);
                } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        for (int pass = 0; pass < 8; pass++)
        {
            bool changed = false;
            foreach (var entry in entries)
                if (
                    !allowed.Contains((int)entry.ProcessId)
                    && allowed.Contains((int)entry.ParentId)
                    && Matches((int)entry.ProcessId, executable, started)
                )
                    changed |= allowed.Add((int)entry.ProcessId);
            if (!changed)
                break;
        }
        return allowed;
    }

    static bool Click(IntPtr button, int owner, string executable, DateTime started)
    {
        GetWindowThreadProcessId(button, out uint current);
        return current == owner
            && IsWindowEnabled(button)
            && Matches(owner, executable, started)
            && PostMessage(button, ButtonClick, IntPtr.Zero, IntPtr.Zero);
    }

    // Called only by the disposable Windows CI smoke test. Every window and
    // button is tied to this exact installer process or its verified children.
    public static string[] Advance(
        int root,
        string executable,
        DateTime started,
        string installation,
        bool approve,
        ref bool decided
    )
    {
        var allowed = InstallerProcesses(root, executable, started);
        var actions = new List<string>();
        bool decision = decided;
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out uint pid);
                if (!allowed.Contains((int)pid) || !IsWindowVisible(window))
                    return true;
                IntPtr yes = GetDlgItem(window, 6),
                    no = GetDlgItem(window, 7);
                if (!decision && pid == root && yes != IntPtr.Zero && no != IntPtr.Zero)
                {
                    var content = new StringBuilder(Text(window));
                    EnumChildWindows(
                        window,
                        (child, state) =>
                        {
                            content.Append('\n').Append(Text(child));
                            return true;
                        },
                        IntPtr.Zero
                    );
                    string question = content.ToString();
                    if (
                        question.Contains(
                            "Update or repair this AHK Manager installation?",
                            StringComparison.Ordinal
                        )
                        && question.Contains(installation, StringComparison.OrdinalIgnoreCase)
                        && Click(approve ? yes : no, (int)pid, executable, started)
                    )
                    {
                        decision = true;
                        actions.Add(
                            approve ? "repair confirmation: Yes" : "repair confirmation: No"
                        );
                    }
                    return true;
                }
                if (!approve || !decision)
                    return true;
                IntPtr next = GetDlgItem(window, 1);
                string label = Text(next).Replace("&", "").Trim();
                if (
                    (
                        label == "Next >"
                        || label == "Next"
                        || label == "Install"
                        || label == "Finish"
                    ) && Click(next, (int)pid, executable, started)
                )
                    actions.Add("verified installer button: " + label);
                return true;
            },
            IntPtr.Zero
        );
        decided = decision;
        return actions.ToArray();
    }
}
