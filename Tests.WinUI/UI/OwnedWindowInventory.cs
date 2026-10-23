using System.Runtime.InteropServices;
using System.Text;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

internal static class OwnedWindowInventory
{
    public static void Write(string output)
    {
        var rows=new List<string>();
        EnumProc child=(window,_)=>{Record(window);return true;};
        EnumProc top=(window,_)=>{Record(window);EnumChildWindows(window,child,0);return true;};
        EnumThreadWindows(GetCurrentThreadId(),top,0);
        File.WriteAllLines(Path.Combine(output,"owned-hwnds.txt"),rows);GC.KeepAlive(top);GC.KeepAlive(child);
        void Record(nint window)
        {
            uint thread=GetWindowThreadProcessId(window,out uint pid);if(pid!=Environment.ProcessId)return;
            var name=new StringBuilder(256);GetClassName(window,name,name.Capacity);GetWindowRect(window,out var rect);GetClientRect(window,out var client);
            int cloakResult=DwmGetWindowAttribute(window,14,out int cloaked,sizeof(int));bool affinityResult=GetWindowDisplayAffinity(window,out uint affinity);
            var placement=new Placement {Length=(uint)Marshal.SizeOf<Placement>()};bool placed=GetWindowPlacement(window,ref placement);
            rows.Add($"hwnd={window};parent={GetParent(window)};class={name};pid={pid};thread={thread};visible={IsWindowVisible(window)};rect={rect};client={client};cloaked={cloaked};cloakHR={cloakResult};affinity={affinity};affinityRead={affinityResult};placementRead={placed};show={placement.Show};normal={placement.Normal}");
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate bool EnumProc(nint window,nint parameter);
    [StructLayout(LayoutKind.Sequential)]struct Rect {public int Left,Top,Right,Bottom;public override readonly string ToString()=>$"{Left},{Top},{Right},{Bottom}";}
    [StructLayout(LayoutKind.Sequential)]struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct Placement {public uint Length,Flags,Show;public Point Min,Max;public Rect Normal;}
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]static extern bool EnumThreadWindows(uint thread,EnumProc callback,nint parameter);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(nint window,EnumProc callback,nint parameter);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(nint window,StringBuilder name,int maximum);
    [DllImport("user32.dll")]static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")]static extern bool GetClientRect(nint window,out Rect rect);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]static extern nint GetParent(nint window);
    [DllImport("user32.dll")]static extern bool GetWindowDisplayAffinity(nint window,out uint affinity);
    [DllImport("user32.dll")]static extern bool GetWindowPlacement(nint window,ref Placement placement);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(nint window,int attribute,out int value,int size);
}
