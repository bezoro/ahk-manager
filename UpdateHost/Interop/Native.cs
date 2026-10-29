using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.UpdateHost;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct FileStamp
    {
        public uint Attributes;
        public long Creation,
            Access,
            Write;
        public uint Volume,
            SizeHigh,
            SizeLow,
            Links,
            IndexHigh,
            IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicInformation
    {
        public nint Reserved,
            Peb,
            Reserved2,
            Reserved3,
            Pid,
            ParentPid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out FileStamp identity
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle handle,
        out long created,
        out long exited,
        out long kernel,
        out long user
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageName(
        SafeProcessHandle handle,
        int flags,
        StringBuilder path,
        ref int length
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(
        SafeProcessHandle handle,
        uint access,
        out SafeAccessTokenHandle token
    );

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        SafeProcessHandle handle,
        int kind,
        out BasicInformation basic,
        int length,
        out int returned
    );

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        EntryPoint = "CreateHardLinkW"
    )]
    internal static extern bool CreateHardLink(string link, string existing, nint reserved);
}
