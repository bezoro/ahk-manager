using System.ComponentModel;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Backend;

internal static class ElevationNative
{
    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Inherit;
    }

    static nint Descriptor(string sid)
    {
        // Explicit protected user DACL; no cross-IL CurrentUserOnly comparison and no broad groups.
        if (
            !ConvertStringSecurityDescriptorToSecurityDescriptor(
                "D:P(A;;GA;;;" + sid + ")",
                1,
                out nint descriptor,
                out _
            )
        )
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return descriptor;
    }

    internal static NamedPipeServerStream CreateServer(string name, string sid)
    {
        nint descriptor = Descriptor(sid);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor,
            };
            var handle = CreateNamedPipe(
                @"\\.\pipe\" + name,
                3 | 0x40000000 | 0x00080000,
                8,
                1,
                4096,
                4096,
                0,
                ref attributes
            );
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally
        {
            ProcessNative.LocalFree(descriptor);
        }
    }

    internal static void CreatePrivateDirectory(string path, string sid)
    {
        for (
            string? current = Path.GetDirectoryName(path);
            current is not null;
            current = Path.GetDirectoryName(current)
        )
            if (
                !Directory.Exists(current)
                || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
            )
                throw new IOException("Coordination ancestor unavailable or redirected.");
        nint descriptor = Descriptor(sid);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor,
            };
            if (!CreateDirectory(path, ref attributes))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            ProcessNative.LocalFree(descriptor);
        }
    }

    internal static void RecoveryError() =>
        MessageBox(
            0,
            "AHK Manager could not be reopened as administrator. Open it normally to recover. No scripts were stopped.",
            "AHK Manager",
            0x10
        );

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    static extern int MessageBox(nint window, string text, string caption, uint kind);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string text,
        uint revision,
        out nint descriptor,
        out uint size
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint instances,
        uint output,
        uint input,
        uint timeout,
        ref SecurityAttributes attributes
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    internal static int ActualParent(int pid)
    {
        using var query = new ManagementObjectSearcher(
            "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid
        );
        query.Options.Timeout = TimeSpan.FromSeconds(5);
        using var rows = query.Get();
        foreach (ManagementObject row in rows)
            using (row)
                return checked((int)(uint)row["ParentProcessId"]);
        return 0;
    }

    internal static bool IsExpectedFailure(Exception ex) =>
        ex
            is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidOperationException
                or OperationCanceledException
                or JsonException
                or TimeoutException
                or ArgumentException;

    internal static ProcessIdentity ToIdentity(ProcessStamp stamp) =>
        new(stamp.Pid, stamp.Created, stamp.Executable, "", stamp.Sid, stamp.Session);
}
