using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Core.Infrastructure;

internal static class LaunchNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PrivilegeSet
    {
        public uint Count,
            Control;
        public ProcessNative.Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Size;
        public string? Reserved,
            Desktop,
            Title;
        public uint X,
            Y,
            XSize,
            YSize,
            XCount,
            YCount,
            Fill,
            Flags;
        public ushort Show,
            ReservedSize;
        public nint ReservedPointer,
            Input,
            Output,
            Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfo
    {
        public nint Process,
            Thread;
        public uint Pid,
            Tid;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool LookupPrivilegeValue(
        string? system,
        string name,
        out ProcessNative.Luid luid
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool PrivilegeCheck(
        SafeAccessTokenHandle token,
        ref PrivilegeSet required,
        [MarshalAs(UnmanagedType.Bool)] out bool enabled
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle token,
        uint access,
        nint attributes,
        int level,
        int type,
        out SafeAccessTokenHandle primary
    );

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(
        out nint environment,
        SafeAccessTokenHandle token,
        bool inherit
    );

    [DllImport("userenv.dll")]
    internal static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle token,
        uint logonFlags,
        string executable,
        StringBuilder command,
        uint flags,
        nint environment,
        string directory,
        ref StartupInfo startup,
        out ProcessInfo process
    );
}
