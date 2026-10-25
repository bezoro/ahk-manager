using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

internal static class ProcessNative
{
    public const uint QuerySynchronize = 0x1000 | 0x00100000;
    [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out TokenStatistics statistics, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, int length, out int returned);
    [DllImport("advapi32.dll")] public static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] public static extern nint GetSidSubAuthority(nint sid, uint index);
    [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] public struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId; public long Expiration; public int TokenType, ImpersonationLevel;
        public uint Charged, Available, Groups, Privileges; public Luid ModifiedId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern uint GetLongPathName(string path, StringBuilder output, uint size);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct FileStamp
    {
        public uint Attributes; public long Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileStamp stamp);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] public static extern nint LocalFree(nint pointer);
    public delegate bool WindowCallback(nint window, nint argument);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(WindowCallback callback, nint argument);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] public static extern nint GetMenu(nint window);
    [DllImport("user32.dll")] public static extern uint GetMenuState(nint menu, uint id, uint flags);
}
