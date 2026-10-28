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
        check(!loaded.ReadOnly && !loaded.Document.Settings.StartInTray && loaded.Document.Settings.LaunchManagerAtLogin && loaded.Document.Settings.LaunchManagerAsAdministrator, "missing profile defaults to visible administrator startup with Windows");

        var legacy = JsonSerializer.SerializeToNode(new SettingsDocument { Settings = new() { ShowWindowAtLogin = true, LaunchManagerAtLogin = false, LaunchManagerAsAdministrator = false } }, ManagerJson.Default.SettingsDocument)!;
        legacy["Settings"]!.AsObject().Remove("StartInTray");
        await File.WriteAllTextAsync(path, legacy.ToJsonString());
        store = new SettingsStore(path);
        loaded = await store.LoadAsync();
        check(!loaded.ReadOnly && !loaded.Document.Settings.StartInTray && loaded.Document.Settings.ShowWindowAtLogin,
            "existing schema without tray preference preserves login visibility and defaults false");

        var enabled = loaded.Document with { Settings = loaded.Document.Settings with { StartInTray = true } };
        check((await store.SaveAsync(enabled)).Succeeded, "tray preference saves through settings store");
        loaded = await new SettingsStore(path).LoadAsync();
        check(!loaded.ReadOnly && loaded.Document.Settings.StartInTray && !loaded.Document.Settings.LaunchManagerAtLogin && !loaded.Document.Settings.LaunchManagerAsAdministrator && loaded.Document.Settings.ShowWindowAtLogin,
            "tray preference round trips without altering login settings");
        var payload = new SettingsPayload(loaded.Document.Settings, false);
        string wire = JsonSerializer.Serialize(payload, Protocol.Json.SettingsPayload);
        var received = JsonSerializer.Deserialize(wire, Protocol.Json.SettingsPayload)!;
        check(wire.Contains("\"startInTray\":true") && received.Settings.StartInTray && !received.Settings.LaunchManagerAsAdministrator,
            "backend protocol carries boolean tray preference in both directions");

        store = new SettingsStore(path);
        loaded = await store.LoadAsync();
        check((await store.SaveAsync(loaded.Document with { Settings = loaded.Document.Settings with { StartInTray = false } })).Succeeded,
            "tray preference can be disabled");
        check(!(await new SettingsStore(path).LoadAsync()).Document.Settings.StartInTray, "disabled tray preference persists");

        string administrativePath = Path.Combine(directory, "administrator-settings.json");
        var withoutAdministrator = JsonSerializer.SerializeToNode(new SettingsDocument
        {
            Settings = new() { LaunchManagerAtLogin = false, LaunchManagerAsAdministrator = false }
        }, ManagerJson.Default.SettingsDocument)!;
        withoutAdministrator["Settings"]!.AsObject().Remove("LaunchManagerAsAdministrator");
        await File.WriteAllTextAsync(administrativePath, withoutAdministrator.ToJsonString());
        var administrativeStore = new SettingsStore(administrativePath);
        var administrative = await administrativeStore.LoadAsync();
        check(!administrative.ReadOnly && !administrative.Document.Settings.LaunchManagerAtLogin && administrative.Document.Settings.LaunchManagerAsAdministrator,
            "existing profile without administrative preference keeps disabled login and receives the administrator default");
        check((await administrativeStore.SaveAsync(administrative.Document with
        {
            Settings = administrative.Document.Settings with { LaunchManagerAsAdministrator = false }
        })).Succeeded && !(await new SettingsStore(administrativePath).LoadAsync()).Document.Settings.LaunchManagerAsAdministrator,
            "an explicit disabled administrator preference persists independently of login");
        string administrativeWire = JsonSerializer.Serialize(new SettingsPayload(administrative.Document.Settings, false), Protocol.Json.SettingsPayload);
        check(JsonSerializer.Deserialize(administrativeWire, Protocol.Json.SettingsPayload)!.Settings.LaunchManagerAsAdministrator,
            "backend protocol preserves the enabled administrator preference as a boolean");
        var invalidAdministrator = JsonSerializer.SerializeToNode(administrative.Document, ManagerJson.Default.SettingsDocument)!;
        invalidAdministrator["Settings"]!["LaunchManagerAsAdministrator"] = "true";
        await File.WriteAllTextAsync(administrativePath, invalidAdministrator.ToJsonString());
        var invalidAdministrative = await new SettingsStore(administrativePath).LoadAsync();
        check(invalidAdministrative.ReadOnly && invalidAdministrative.Issues.Any(issue => issue.Code == ReasonCode.InvalidSettings),
            "string administrative preference opens read-only recovery instead of enabling startup");

        var invalid = JsonSerializer.SerializeToNode(enabled, ManagerJson.Default.SettingsDocument)!;
        invalid["Settings"]!["StartInTray"] = "true";
        await File.WriteAllTextAsync(path, invalid.ToJsonString());
        loaded = await new SettingsStore(path).LoadAsync();
        check(loaded.ReadOnly && loaded.Issues.Any(issue => issue.Code == ReasonCode.InvalidSettings),
            "invalid tray preference uses explicit read-only recovery");
    }
}
