using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    static async Task RenderElevation()
    {
        using var r = new FunctionalRig("render-only"); await r.Session.Refresh();
        var window = new MainWindow(r.Session); var admin = RowPreference(window, r.Entry.Id, "Executar como administrador"); ToggleRowPreference(admin, true);
        WpfProgram.Render(window, Path.Combine(root, "administrator-min.png"), 1000, 720, 1);
        WpfProgram.Render(window, Path.Combine(root, "administrator-150.png"), 1220, 800, 1.5);
        WpfProgram.Render(window, Path.Combine(root, "administrator-200.png"), 1000, 720, 2);
        Check(admin.IsChecked == true, "rendered preference checked");
        Check(admin.TransformToAncestor((System.Windows.Media.Visual)window.Content).Transform(new Point()).Y < 400, "preference visible above detail fold");
        Check(!window.ActionButton(ScriptAction.Start).IsEnabled && r.Session.Availability(ScriptAction.Start).Message.Contains("Dispatcher manual indisponível"), "intermediate unavailable manual elevation is explicit");
        Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Recycler.Calls == 0, "render-only fixture zero process effects");
        window.Close();
    }
    static void LegacyPayload(CatalogStore store, Catalog catalog, int schema)
    {
        var node = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(catalog, TestJson.Default.Catalog))!.AsObject();
        node["Schema"] = schema;
        if (schema < 4) node.Remove("Authority");
        node.Remove("Runtime");
        foreach (var script in node["Scripts"]!.AsArray()) { var item = script!.AsObject(); item["RequestedEnabled"] = item["StartWithWindows"]?.DeepClone(); item.Remove("StartWithWindows"); }
        if (schema < 3) foreach (var script in node["Scripts"]!.AsArray()) script!.AsObject().Remove("RunAsAdministrator");
        File.WriteAllText(store.FilePath, node.ToJsonString());
    }
    static async Task RunElevationPreference()
    {
        using (var r = new FunctionalRig("preference"))
        {
            await r.Session.Refresh(); var window = new MainWindow(r.Session);
            var admin = RowPreference(window, r.Entry.Id, "Executar como administrador");
            byte[] before = File.ReadAllBytes(r.Store.FilePath);
            r.Session.Select(r.Session.Entries[1].Id); r.Session.Select(r.Entry.Id);
            Check(before.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), "programmatic selection and repaint do not save preference");
            ToggleRowPreference(admin, true);
            Check(r.Session.Selected!.RunAsAdministrator && r.Store.Load().Scripts.Single(e => e.Id == r.Session.SelectedId).RunAsAdministrator, "checkbox persists preference before committed UI state");
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Recycler.Calls == 0 && r.Interaction.Confirmations == 0, "checkbox has zero process task UAC or confirmation effects");
            Check(AutomationProperties.GetName(admin).Length > 0 && admin.Focusable && admin.IsTabStop && System.Windows.Input.KeyboardNavigation.GetTabIndex(admin) > 0, "administrator preference has accessible name and tab order");
            using (var locked = new FileStream(r.Store.FilePath + ".tmp", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) ToggleRowPreference(admin, false);
            Check(r.Session.Selected!.RunAsAdministrator && admin.IsChecked == true && r.Session.Error, "failed atomic preference save reverts checkbox and preserves committed value");
            r.Owner.Held = false; ToggleRowPreference(admin, false);
            Check(r.Session.Selected.RunAsAdministrator && r.Processes.Starts == 0, "lost owner refuses preference writes and effects");
            window.Close();
        }
    }
    static async Task RunElevation()
    {
        foreach (int schema in new[] { 1, 2 })
        {
            using var r = new FunctionalRig("conversion-" + schema);
            r.Arm();
            var before = r.Session.CurrentCatalog;
            before.Recovery.Add(new() { ScriptId = before.Scripts[0].Id, LoginKey = r.Owner.LoginKey, AttemptsUtcTicks = [r.Clock.UtcNow.AddMinutes(-1).Ticks], Failures = 2, NextTryUtcTicks = r.Clock.UtcNow.AddMinutes(2).Ticks, Suspended = true });
            LegacyPayload(r.Store, before, schema); r.Reload();
            Check(r.Store.LoadedSchema == schema && r.Session.NeedsSchemaConversion && r.Session.CurrentCatalog.Schema == 5, "legacy schema " + schema + " retains loaded version until explicit save3");
            var converted = r.Session.CurrentCatalog;
            Check(converted.Scripts.Zip(before.Scripts).All(pair => pair.First.Id == pair.Second.Id && pair.First.Path == pair.Second.Path && pair.First.Description == pair.Second.Description && pair.First.Interpreter == pair.Second.Interpreter && !pair.First.RunAsAdministrator), "conversion preserves script metadata and defaults preference false");
            Check(schema == 1 ? converted.Scripts.All(e => e.RequestedEnabled is null && e.ManagementMode == ManagementMode.Unmanaged) : converted.Scripts.Zip(before.Scripts).All(pair => pair.First.RequestedEnabled == pair.Second.RequestedEnabled && pair.First.ManagementMode == pair.Second.ManagementMode), "schema-specific existing intentions preserved without inference");
            if (schema == 2)
            {
                Check(converted.Migration.JournalDigest == before.Migration.JournalDigest && converted.Migration.OwnerSid == before.Migration.OwnerSid && converted.Migration.CompletedUtcTicks == before.Migration.CompletedUtcTicks, "schema2 durable migration state preserved");
                var budget = converted.Recovery.Single();
                Check(budget.LoginKey == before.Recovery[0].LoginKey && budget.AttemptsUtcTicks.SequenceEqual(before.Recovery[0].AttemptsUtcTicks) && budget.Failures == 2 && budget.Suspended && budget.NextTryUtcTicks == before.Recovery[0].NextTryUtcTicks, "schema2 login attempts cooldown suspension preserved exactly");
            }
            byte[] primary = File.ReadAllBytes(r.Store.FilePath), backup = File.ReadAllBytes(r.Store.FilePath + ".bak");
            await r.Session.ConvertSchema();
            Check(primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && backup.SequenceEqual(File.ReadAllBytes(r.Store.FilePath + ".bak")), "conversion Cancel preserves primary and backup bytes");
            r.Interaction.Confirmed = true; await r.Session.ConvertSchema();
            Check(r.Store.LoadedSchema == 5 && !r.Session.NeedsSchemaConversion && r.Store.VerifyPrimary(r.Session.CurrentCatalog), "confirmed conversion durably saves schema3");
            using var document = JsonDocument.Parse(File.ReadAllBytes(r.Store.FilePath));
            Check(document.RootElement.GetProperty("Scripts").EnumerateArray().All(e => e.GetProperty("RunAsAdministrator").ValueKind == JsonValueKind.False), "schema3 emits explicit false boolean for every script");
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Recycler.Calls == 0, "conversion causes zero process or recycle effects");
        }
        using (var r = new FunctionalRig("conversion-race"))
        {
            r.Store.Save(r.Session.CurrentCatalog);
            LegacyPayload(r.Store, r.Session.CurrentCatalog, 2); r.Reload();
            r.Interaction.HoldConfirmation = new(); var conversion = r.Session.ConvertSchema();
            File.AppendAllText(r.Store.FilePath, " ");
            var primary = File.ReadAllBytes(r.Store.FilePath); var backup = File.ReadAllBytes(r.Store.FilePath + ".bak");
            r.Interaction.HoldConfirmation.SetResult(true); await conversion;
            Check(primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && backup.SequenceEqual(File.ReadAllBytes(r.Store.FilePath + ".bak")) && r.Store.LoadedSchema == 2 && r.Session.Error, "changed schema2 primary during prompt refuses conversion without overwrite");
        }
        foreach (string invalid in new[] { "duplicate-preference", "missing-schema", "missing-preference", "null-preference", "string-preference", "unknown-root", "unknown-script", "schema1-preference", "schema2-preference" })
        {
            using var r = new FunctionalRig(invalid);
            var node = JsonNode.Parse(File.ReadAllBytes(r.Store.FilePath))!.AsObject();
            if (invalid == "missing-schema") node.Remove("Schema");
            else if (invalid == "unknown-root") node["Typo"] = true;
            else if (invalid == "unknown-script") node["Scripts"]![0]!["Typo"] = false;
            else if (invalid == "schema1-preference" || invalid == "schema2-preference") node["Schema"] = invalid == "schema1-preference" ? 1 : 2;
            else if (invalid == "missing-preference") node["Scripts"]![0]!.AsObject().Remove("RunAsAdministrator");
            else node["Scripts"]![0]!["RunAsAdministrator"] = invalid == "null-preference" ? null : JsonValue.Create("false");
            File.WriteAllText(r.Store.FilePath, invalid == "duplicate-preference" ? node.ToJsonString().Replace("\"RunAsAdministrator\":false", "\"RunAsAdministrator\":true,\"RunAsAdministrator\":false", StringComparison.Ordinal) : node.ToJsonString()); r.Reload();
            var primary = File.ReadAllBytes(r.Store.FilePath); await r.Session.Reconcile();
            Check(r.Store.ReadOnly && !r.Store.PrimaryValid && r.Processes.Starts == 0 && r.Processes.Stops == 0 && primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), invalid + " is read-only with zero automatic effects");
        }
        await RunElevationPreference();
        using (var r = new FunctionalRig("automatic-guard"))
        {
            r.Arm(); r.EmptyProcesses(); await r.Session.Refresh(); r.Session.SetRunAsAdministrator(true);
            byte[] before = File.ReadAllBytes(r.Store.FilePath);
            await r.Session.Reconcile(); await r.Session.Reconcile(); r.Session.RetryRecovery();
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Session.CurrentCatalog.Recovery.Count == 0 && before.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), "checked login recovery repeated ticks and Retry never start or reserve budget");
            var controller = new ActionController(r.Processes, r.Recycler);
            var result = await controller.Execute(r.Entry, ScriptAction.Start, true);
            Check(!result.Success && r.Processes.Starts == 0, "core controller refuses checked normal automatic launch");
            result = await new WindowsProcesses().Start(r.Entry, CancellationToken.None);
            Check(!result.Success && r.Processes.Starts == 0, "native Start directly refuses checked preference before OS query or fallback");
            r.Processes.Current = new([new(7777, 123, r.Entry.Interpreter, r.Entry.Path)], true, "Fixture"); await r.Session.Refresh();
            result = await controller.Execute(r.Entry, ScriptAction.Restart, true);
            Check(!result.Success && r.Processes.Stops == 0 && r.Processes.Starts == 0, "checked restart refuses before stop");
            Check(!r.Session.Availability(ScriptAction.Restart).Success, "presentation shares checked restart policy");
        }
        using (var r = new FunctionalRig("pending-external"))
        {
            var catalog = r.Store.Load(); catalog.Scripts[0].ManagementMode = ManagementMode.ExternalPending; catalog.Scripts[0].RequestedEnabled = true; r.Store.Save(catalog); r.Reload();
            r.Session.SetRunAsAdministrator(true);
            Check(r.Session.Selected!.ManagementMode == ManagementMode.ExternalPending && r.Session.Selected.RunAsAdministrator && !r.Session.Availability(ScriptAction.Start).Success, "checkbox preserves external pending mode without task bypass");
            await r.Session.Act(ScriptAction.Start); await r.Session.Reconcile();
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Recycler.Calls == 0, "external pending preference never invokes process or task effects");
        }
    }
}
