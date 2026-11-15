using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.AgentCli;

internal static class AgentPipe
{
    internal const int Connections = 8;
    const uint Overlapped = 0x40000000,
        FirstInstance = 0x00080000,
        RejectRemote = 8,
        SecurityQosPresent = 0x00100000;

    internal static NamedPipeServerStream Listen(
        string name,
        string sid,
        bool first,
        int instances = Connections
    )
    {
        // The SID comes from an authenticated Windows token. Require high integrity for writes
        // from that account, while leaving out broad built-in groups and inherited entries.
        if (
            !ConvertStringSecurityDescriptorToSecurityDescriptor(
                "D:P(A;;GA;;;" + sid + ")S:(ML;;NW;;;HI)",
                1,
                out var descriptor,
                out _
            )
        )
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor,
            };
            var handle = CreateNamedPipe(
                Address(name),
                3u | Overlapped | (first ? FirstInstance : 0u),
                RejectRemote,
                (uint)instances,
                65536,
                65536,
                0,
                ref attributes
            );
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    internal static NamedPipeClientStream? Connect(string name)
    {
        // SECURITY_SQOS_PRESENT without an impersonation-level flag requests
        // SECURITY_ANONYMOUS. The server authenticates the client PID, never its token by impersonation.
        var handle = CreateFile(
            Address(name),
            0xc0000000,
            0,
            0,
            3,
            Overlapped | SecurityQosPresent,
            0
        );
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return error switch
            {
                2 => null, // ERROR_FILE_NOT_FOUND is the only absence that permits launch.
                231 => throw new AgentFailure("HostBusy"),
                _ => throw new AgentFailure("PeerUnverified"),
            };
        }
        try
        {
            return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static int ClientPid(SafePipeHandle handle) =>
        GetNamedPipeClientProcessId(handle, out uint pid) && pid is > 0 and <= int.MaxValue
            ? (int)pid
            : throw new AgentFailure("PeerUnverified");

    internal static int ServerPid(SafePipeHandle handle) =>
        GetNamedPipeServerProcessId(handle, out uint pid) && pid is > 0 and <= int.MaxValue
            ? (int)pid
            : throw new AgentFailure("PeerUnverified");

    internal static void RequirePrivateStdio(int parentPid)
    {
        // libuv keeps the server end of each child stdio pipe in the spawning process.
        foreach (int file in new[] { -10, -11, -12 })
        {
            using var handle = new SafePipeHandle(GetStdHandle(file), ownsHandle: false);
            if (handle.IsInvalid || ServerPid(handle) != parentPid)
                throw new AgentFailure("PrivateParentRequired");
        }
    }

    static string Address(string name) => @"\\.\pipe\" + name;

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        internal int Length;
        internal nint Descriptor;
        internal int Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string text,
        uint revision,
        out nint descriptor,
        out uint size
    );

    [DllImport("kernel32.dll")]
    static extern nint LocalFree(nint memory);

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
    static extern SafePipeHandle CreateFile(
        string name,
        uint access,
        uint share,
        nint security,
        uint disposition,
        uint flags,
        nint template
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll")]
    static extern nint GetStdHandle(int device);
}
