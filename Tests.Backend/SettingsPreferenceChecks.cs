using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Tests.Backend;

internal static class SettingsPreferenceChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "start-in-tray");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        var loaded = await store.LoadAsync();
        check(!loaded.ReadOnly && !loaded.Document.Settings.StartInTray, "missing profile defaults to visible manual startup");

        var legacy = JsonSerializer.SerializeToNode(new SettingsDocument { Settings = new() { ShowWindowAtLogin = true } }, ManagerJson.Default.SettingsDocument)!;
        legacy["Settings"]!.AsObject().Remove("StartInTray");
        await File.WriteAllTextAsync(path, legacy.ToJsonString());
        store = new SettingsStore(path);
        loaded = await store.LoadAsync();
        check(!loaded.ReadOnly && !loaded.Document.Settings.StartInTray && loaded.Document.Settings.ShowWindowAtLogin,
            "existing schema without tray preference preserves login visibility and defaults false");

        var enabled = loaded.Document with { Settings = loaded.Document.Settings with { StartInTray = true } };
        check((await store.SaveAsync(enabled)).Succeeded, "tray preference saves through settings store");
        loaded = await new SettingsStore(path).LoadAsync();
        check(!loaded.ReadOnly && loaded.Document.Settings.StartInTray && !loaded.Document.Settings.LaunchManagerAtLogin && loaded.Document.Settings.ShowWindowAtLogin,
            "tray preference round trips without altering login settings");
        var payload = new SettingsPayload(loaded.Document.Settings, false);
        string wire = JsonSerializer.Serialize(payload, Protocol.Json.SettingsPayload);
        var received = JsonSerializer.Deserialize(wire, Protocol.Json.SettingsPayload)!;
        check(wire.Contains("\"startInTray\":true") && received.Settings.StartInTray,
            "backend protocol carries boolean tray preference in both directions");

        store = new SettingsStore(path);
        loaded = await store.LoadAsync();
        check((await store.SaveAsync(loaded.Document with { Settings = loaded.Document.Settings with { StartInTray = false } })).Succeeded,
            "tray preference can be disabled");
        check(!(await new SettingsStore(path).LoadAsync()).Document.Settings.StartInTray, "disabled tray preference persists");

        var invalid = JsonSerializer.SerializeToNode(enabled, ManagerJson.Default.SettingsDocument)!;
        invalid["Settings"]!["StartInTray"] = "true";
        await File.WriteAllTextAsync(path, invalid.ToJsonString());
        loaded = await new SettingsStore(path).LoadAsync();
        check(loaded.ReadOnly && loaded.Issues.Any(issue => issue.Code == ReasonCode.InvalidSettings),
            "invalid tray preference uses explicit read-only recovery");
    }
}
