using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Services;

namespace Tests.Backend;

internal static class StartupCommandChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        string own = new UserStartupService(new FakeStartup(), @"V:\Repos\Bezoro.AhkManager\publish\Bezoro.AhkManager.exe").Command;
        var manager = RegistryStartupAdapter.InspectCommand(own);
        check(manager.Complete && manager.ScriptPaths.IsEmpty, "exact Manager login command containing .Ahk is unrelated to script startup");

        string script = Path.Combine(root, "startup command with spaces.ahk");
        File.WriteAllText(script, "; startup-inspection fixture only");
        var direct = RegistryStartupAdapter.InspectCommand(ScriptPaths.QuoteArgument(script));
        check(direct.Complete && direct.ScriptPaths.Length == 1 && ScriptPaths.Equal(direct.ScriptPaths[0], ScriptPaths.Canonical(script)), "quoted direct AHK startup script remains detected");
        var interpreter = RegistryStartupAdapter.InspectCommand("\"C:\\Program Files\\AutoHotkey\\v2\\AutoHotkey64.exe\" /script " + ScriptPaths.QuoteArgument(script));
        check(interpreter.Complete && interpreter.ScriptPaths.Length == 1 && ScriptPaths.Equal(interpreter.ScriptPaths[0], direct.ScriptPaths[0]), "quoted interpreter and AHK script remain detected");

        foreach (string command in new[]
        {
            "\"C:\\Scripts\\broken.ahk",
            "\"%LOCALAPPDATA%\\Scripts\\login.ahk\"",
            "AutoHotkey64.exe relative.ahk",
            "cmd.exe /c \"C:\\Scripts\\wrapped.ahk\"",
            "cmd.exe /c \"C:\\Scripts\\wrapped.ahk&other.exe\"",
            "cmd.exe /c C:\\Scripts\\wrapped.ahk|other.exe",
            "\"C:\\AutoHotkey64.exe\" /unsupported \"C:\\Scripts\\login.ahk\"",
            "\"C:\\Scripts\\login.ahk\" --unknown",
            "\"C:\\Scripts\\login.ahk'"
        })
        {
            var unsupported = RegistryStartupAdapter.InspectCommand(command);
            check(!unsupported.Complete && unsupported.ScriptPaths.IsEmpty, "unsupported relevant startup command remains uncertain: " + command);
        }
    }
}
