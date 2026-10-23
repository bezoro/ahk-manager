using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Tests.Core;

public static partial class CoreSuite
{
    static int checks;
    static string FixtureRoot = "";
    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    public static async Task RunAsync(string? fixtureRoot = null)
    {
        FixtureRoot = System.IO.Path.GetFullPath(fixtureRoot ?? throw new ArgumentException("Dispatcher must provide an isolated core fixture root."));
        Directory.CreateDirectory(FixtureRoot);
        checks = 0;
        Check(!ManagerSnapshot.Empty.Settings.LaunchManagerAtLogin, "login defaults off");
        Check(!ManagerSnapshot.Empty.Settings.ShowWindowAtLogin, "login window defaults off");
        Check(ManagerSnapshot.Empty.RunningCount == 0, "empty state has no fabricated processes");
        await RunStorageAsync();
        await RunDomainAsync();
        await RunSafetyRegressionsAsync();
        await RunManagerAsync();
        Console.WriteLine($"CORE checks={checks}");
    }
}
