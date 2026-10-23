using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.WinUI.Tests.Core;

public static partial class CoreSuite
{
    static async Task RunStorageAsync()
    {
        var root = System.IO.Path.Combine(FixtureRoot, "storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "settings.json");
        var store = new SettingsStore(path);
        var first = await store.LoadAsync();
        Check(!first.ReadOnly && first.Document.Scripts.Count == 0, "absent settings load writable and empty");
        Check((await store.SaveAsync(first.Document with { Settings = new() { Theme = AppThemePreference.Dark } })).Succeeded, "settings initial atomic write");
        var stale = new SettingsStore(path);
        await stale.LoadAsync();
        Check((await store.SaveAsync(first.Document with { Settings = new() { Theme = AppThemePreference.Light } })).Succeeded, "settings replacement creates backup");
        Check((await stale.SaveAsync(first.Document)).Code == ReasonCode.ConcurrentSettingsChange, "stale loaded fingerprint refuses lost update");
        await File.WriteAllTextAsync(path, "{invalid");
        var recoveredStore = new SettingsStore(path);
        var recovered = await recoveredStore.LoadAsync();
        Check(recovered.ReadOnly && recovered.Issues.Any(i => i.Code == ReasonCode.RecoveredBackup), "corrupt primary presents backup read only");
        Check((await recoveredStore.SaveAsync(recovered.Document)).Code == ReasonCode.ReadOnlySettings, "recovery requires explicit restore");
        Check((await recoveredStore.RestoreBackupAsync()).Succeeded, "explicit backup restore works");
        Check((await new SettingsStore(path).LoadAsync()).Document.Settings.Theme == AppThemePreference.Dark, "valid backup content retained");
        await File.WriteAllTextAsync(path, "{\"Schema\":999,\"Scripts\":[]}");
        var future = await new SettingsStore(path).LoadAsync();
        Check(future.ReadOnly && future.Issues.Any(i => i.Code == ReasonCode.UnsupportedSchema), "future schema never overwritten");
        Check(File.ReadAllText(path).Contains("999", StringComparison.Ordinal), "future primary preserved");
        // Retain isolated fixture directories for the dispatcher to record/clean exact test paths.
        Console.WriteLine("FIXTURE " + root);
    }
}
