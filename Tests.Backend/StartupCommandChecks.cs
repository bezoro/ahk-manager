using System.Management;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupCommandChecks
{
    internal static async Task ReadOnlyWindowsInspectionAsync(string root)
    {
        using var handlerClass = new ManagementClass(
            @"\\.\root\Microsoft\Windows\TaskScheduler:MSFT_TaskComHandlerAction"
        );
        using var handler = handlerClass.CreateInstance()!;
        if (
            RegistryStartupAdapter.ReadActionProperty(handler, "Execute") is not null
            || RegistryStartupAdapter.ReadActionProperty(handler, "Arguments") is not null
        )
            throw new InvalidOperationException(
                "COM handler must not expose executable action fields."
            );
        Console.WriteLine("PASS COM-handler missing Execute/Arguments ignored without NotFound");
        using var executableClass = new ManagementClass(
            @"\\.\root\Microsoft\Windows\TaskScheduler:MSFT_TaskExecAction"
        );
        using var executable = executableClass.CreateInstance()!;
        executable["Execute"] = @"C:\fixture\AutoHotkey64.exe";
        executable["Arguments"] = "fixture.ahk";
        if (
            RegistryStartupAdapter.ReadActionProperty(executable, "Execute")
                != @"C:\fixture\AutoHotkey64.exe"
            || RegistryStartupAdapter.ReadActionProperty(executable, "Arguments") != "fixture.ahk"
        )
            throw new InvalidOperationException("Executable action fields were changed.");
        executable["Arguments"] = null;
        if (RegistryStartupAdapter.ReadActionProperty(executable, "Arguments") is not null)
            throw new InvalidOperationException("Null optional arguments changed.");
        Console.WriteLine("PASS executable action fields and optional null arguments preserved");
        Run(
            root,
            (value, name) =>
            {
                if (!value)
                    throw new InvalidOperationException(name);
                Console.WriteLine("PASS " + name);
            }
        );
        var result = await new RegistryStartupAdapter().InspectAsync(CancellationToken.None);
        string summary =
            $"Complete={result.Complete}; Scripts={result.ScriptPaths.Length}; Issues={string.Join(" | ", result.Issues.Select(issue => issue.Detail))}";
        await File.WriteAllTextAsync(Path.Combine(root, "readonly-inspection.txt"), summary);
        Console.WriteLine(summary);
        if (!result.Complete)
            throw new InvalidOperationException(
                "Read-only Windows startup inspection did not complete."
            );
    }

    public static void Run(string root, Action<bool, string> check)
    {
        string own = new UserStartupService(
            new FakeStartup(),
            @"V:\Repos\Bezoro.AhkManager\publish\Bezoro.AhkManager.exe"
        ).Command;
        var manager = RegistryStartupAdapter.InspectCommand(own);
        check(
            manager.Complete && manager.ScriptPaths.IsEmpty,
            "exact Manager login command containing .Ahk is unrelated to script startup"
        );

        string script = Path.Combine(root, "startup command with spaces.ahk");
        File.WriteAllText(script, "; startup-inspection fixture only");
        var direct = RegistryStartupAdapter.InspectCommand(ScriptPaths.QuoteArgument(script));
        check(
            direct.Complete
                && direct.ScriptPaths.Length == 1
                && ScriptPaths.Equal(direct.ScriptPaths[0], ScriptPaths.Canonical(script)),
            "quoted direct AHK startup script remains detected"
        );
        var interpreter = RegistryStartupAdapter.InspectCommand(
            "\"C:\\Program Files\\AutoHotkey\\v2\\AutoHotkey64.exe\" /script "
                + ScriptPaths.QuoteArgument(script)
        );
        check(
            interpreter.Complete
                && interpreter.ScriptPaths.Length == 1
                && ScriptPaths.Equal(interpreter.ScriptPaths[0], direct.ScriptPaths[0]),
            "quoted interpreter and AHK script remain detected"
        );

        foreach (
            string command in new[]
            {
                "\"C:\\Scripts\\broken.ahk",
                "\"%LOCALAPPDATA%\\Scripts\\login.ahk\"",
                "AutoHotkey64.exe relative.ahk",
                "cmd.exe /c \"C:\\Scripts\\wrapped.ahk\"",
                "cmd.exe /c \"C:\\Scripts\\wrapped.ahk&other.exe\"",
                "cmd.exe /c C:\\Scripts\\wrapped.ahk|other.exe",
                "\"C:\\AutoHotkey64.exe\" /unsupported \"C:\\Scripts\\login.ahk\"",
                "\"C:\\Scripts\\login.ahk\" --unknown",
                "\"C:\\Scripts\\login.ahk'",
            }
        )
        {
            var unsupported = RegistryStartupAdapter.InspectCommand(command);
            check(
                !unsupported.Complete && unsupported.ScriptPaths.IsEmpty,
                "unsupported relevant startup command remains uncertain: " + command
            );
        }
    }
}
