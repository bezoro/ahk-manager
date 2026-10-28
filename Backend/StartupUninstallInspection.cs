using System.Runtime.InteropServices;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.Backend;

// Exit 0: no owned task; 2: disable startup before removal; 3: inspection unknown.
// The installed backend's directory fixes the only allowed host path. This mode
// never opens a profile, acquires an effect owner, registers a task or starts a process.
internal static class StartupUninstallInspection
{
    internal static string HostPath(string backendDirectory) => ScriptPaths.LocalPath(Path.GetFullPath(Path.Combine(backendDirectory, "..", "Bezoro.AhkManager.exe")));
    internal static int Classify(StartupTaskRegistration? task, string host, string sid)
    {
        string command = ScriptPaths.QuoteArgument(ScriptPaths.LocalPath(host)) + " --login --background";
        return task is not null && UserStartupService.OwnsTask(task, command, sid) ? 2 : 0;
    }
    internal static int Run()
    {
        try
        {
            string host = HostPath(AppContext.BaseDirectory);
            string sid = new RegistryStartupAdapter().UserSid;
            return Classify(WindowsStartupTaskStore.Read(sid), host, sid);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return 3; }
    }
}
