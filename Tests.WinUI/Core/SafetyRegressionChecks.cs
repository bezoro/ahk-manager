using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.WinUI.Tests.Core;

public static partial class CoreSuite
{
    static async Task RunSafetyRegressionsAsync()
    {
        var failures = new List<string>();
        void Regression(bool passed, string name)
        {
            Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
            if (!passed) failures.Add(name); else checks++;
        }
        var root = Path.Combine(FixtureRoot, "safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owner = new FakeOwner(root);
        string script = Path.Combine(root, "regression.ahk");
        await File.WriteAllTextAsync(script, "#Requires AutoHotkey v2.0\n^a::return\n");
        string escaped = JsonEncodedText.Encode(script).ToString();
        string incomplete = Path.Combine(root, "missing-root-fields.json");
        await File.WriteAllTextAsync(incomplete, "{\"Schema\":1}");
        var incompleteLoad = await new SettingsStore(incomplete).LoadAsync();
        Console.WriteLine("OBSERVED missing settings roots: " + string.Join("; ", incompleteLoad.Issues.Select(i => i.Code + ": " + i.Detail)));
        Regression(incompleteLoad.ReadOnly && incompleteLoad.Issues.Any(i => i.Code == ReasonCode.InvalidSettings),
            "settings missing required root fields open read only");
        var missingSettingsRoots = JsonSerializer.Deserialize("{\"Schema\":1}", ManagerJson.Default.SettingsDocument);
        Console.WriteLine("CHARACTERIZATION generated missing settings roots: scriptsNull=" + (missingSettingsRoots?.Scripts is null) + ", collectionsNull=" + (missingSettingsRoots?.Collections is null) + ", settingsNull=" + (missingSettingsRoots?.Settings is null));
        byte[] backupState = JsonSerializer.SerializeToUtf8Bytes(new SettingsDocument { LastLoginKey = "backup-proof" }, ManagerJson.Default.SettingsDocument);
        foreach (var malformed in new[] { ("array", "[]"), ("null", "null"), ("null-script", "{\"Schema\":1,\"Scripts\":[null]}"), ("null-collection", "{\"Schema\":1,\"Collections\":[null]}"), ("schema-string", "\"1\""), ("schema-null", "null"), ("schema-bool", "true") })
        {
            string malformedPath = Path.Combine(root, "malformed-settings-" + malformed.Item1 + ".json");
            string malformedJson = malformed.Item2;
            if (malformed.Item1 is "null-script" or "null-collection")
            {
                var fullState = JsonNode.Parse(backupState)!;
                fullState[malformed.Item1 == "null-script" ? "Scripts" : "Collections"] = new JsonArray((JsonNode?)null);
                malformedJson = fullState.ToJsonString();
            }
            else if (malformed.Item1.StartsWith("schema-", StringComparison.Ordinal))
            {
                var fullState = JsonNode.Parse(backupState)!;
                fullState["Schema"] = JsonNode.Parse(malformed.Item2);
                malformedJson = fullState.ToJsonString();
            }
            await File.WriteAllTextAsync(malformedPath, malformedJson);
            await File.WriteAllBytesAsync(malformedPath + ".bak", backupState);
            bool recovered = false;
            try
            {
                var loaded = await new SettingsStore(malformedPath).LoadAsync();
                recovered = loaded.ReadOnly && loaded.Document.LastLoginKey == "backup-proof";
            }
            catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
            { Console.WriteLine("OBSERVED malformed settings " + malformed.Item1 + ": " + ex.GetType().Name); }
            Regression(recovered, "malformed settings " + malformed.Item1 + " opens read only with valid backup");
        }
        string flags = Path.Combine(root, "missing-script-flags.json");
        var validState = new SettingsDocument { Scripts = [new() { Path = script, FriendlyName = "Fixture" }] };
        var validNode = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(validState, ManagerJson.Default.SettingsDocument))!;
        var scriptNode = validNode["Scripts"]!.AsArray()[0]!.AsObject();
        scriptNode.Remove("RunAsAdministrator"); scriptNode.Remove("LegacyGuard");
        using (var bytes = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(bytes)) validNode.WriteTo(writer);
            await File.WriteAllBytesAsync(flags, bytes.ToArray());
        }
        Regression((await new SettingsStore(flags).LoadAsync()).ReadOnly,
            "settings never default missing administrator or legacy protection flags");
        string missingJournal = Path.Combine(root, "missing-journal-operations.json");
        await File.WriteAllTextAsync(missingJournal, "{\"Schema\":1}");
        bool journalRejected = false;
        var missingJournalStore = new OperationJournal(missingJournal);
        try { await missingJournalStore.LoadAsync(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { journalRejected = true; Console.WriteLine("OBSERVED missing journal operations: " + ex.Message); }
        Regression(journalRejected && !missingJournalStore.Ready, "journal missing operations cannot become an empty trusted journal");
        var missingJournalRoots = JsonSerializer.Deserialize("{\"Schema\":1}", ManagerJson.Default.JournalDocument);
        Console.WriteLine("CHARACTERIZATION generated missing journal roots: operationsNull=" + (missingJournalRoots?.Operations is null));
        string malformedJournal = Path.Combine(root, "malformed-journal-record.json");
        await File.WriteAllTextAsync(malformedJournal, "{\"Schema\":1,\"Operations\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"ScriptId\":\"" + Guid.NewGuid() + "\"}]}");
        bool malformedRejected = false;
        try { await new OperationJournal(malformedJournal).LoadAsync(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { malformedRejected = true; }
        catch (NullReferenceException) { Console.WriteLine("OBSERVED malformed journal escaped with NullReferenceException"); }
        Regression(malformedRejected, "malformed journal required strings produce recoverable validation failure");
        var importer = new LegacyImportService(Path.Combine(root, "import-target"), owner);
        string legacy = Path.Combine(root, "legacy.json");
        string row = "{\"Id\":\"" + Guid.NewGuid() + "\",\"Path\":\"" + escaped + "\",\"RunAsAdministrator\":false,\"ManagementMode\":1}";
        await File.WriteAllTextAsync(legacy, "{\"Schema\":4,\"Scripts\":[" + row + "]}");
        bool rejected = false;
        try { await importer.ReadAsync(legacy); } catch (InvalidDataException) { rejected = true; }
        Regression(rejected, "legacy schema4 without authority is refused");
        await File.WriteAllTextAsync(legacy, "{\"Schema\":3,\"Scripts\":[" + row.Replace("\"RunAsAdministrator\":false", "\"RunAsAdministrator\":null", StringComparison.Ordinal) + "]}");
        rejected = false;
        try { await importer.ReadAsync(legacy); } catch (InvalidDataException) { rejected = true; }
        Regression(rejected, "legacy schema3 requires explicit boolean admin preference");
        foreach (int oldSchema in new[] { 1, 2 })
        {
            await File.WriteAllTextAsync(legacy, "{\"Schema\":" + oldSchema + ",\"Scripts\":[" + row.Replace("\"RunAsAdministrator\":false", "\"RunAsAdministrator\":true", StringComparison.Ordinal) + "]}");
            rejected = false;
            try { await importer.ReadAsync(legacy); } catch (InvalidDataException) { rejected = true; }
            Regression(rejected, "legacy schema" + oldSchema + " refuses unsupported admin field without downgrade");
        }
        await File.WriteAllTextAsync(legacy, "{\"Schema\":1,\"Scripts\":[" + row.Replace(",\"RunAsAdministrator\":false", "", StringComparison.Ordinal) + "]}");
        await File.WriteAllTextAsync(Path.Combine(root, "launch-requests.json"), "{\"Schema\":2,\"Requests\":[{\"Nonce\":\"" + Guid.NewGuid() + "\"}]}");
        var pending = await importer.ReadAsync(legacy);
        Regression(pending.Scripts.Single().LegacyGuard, "legacy schema1 import preserves pending sibling request guard");
        string adjacent = Path.Combine(root, "authority-copy"), authorityRoot = Path.Combine(root, "authority-real");
        Directory.CreateDirectory(adjacent); Directory.CreateDirectory(authorityRoot);
        Guid ledgerId = Guid.NewGuid();
        string boundSource = Path.Combine(adjacent, "legacy.json");
        string Binding(Guid bindingId) => "{\"Sid\":\"" + JsonEncodedText.Encode(owner.Sid) + "\",\"Fixture\":false,\"LedgerId\":\"" + bindingId + "\"}";
        await File.WriteAllTextAsync(boundSource, "{\"Schema\":4,\"Authority\":" + Binding(ledgerId) + ",\"Scripts\":[" + row + "]}");
        async Task WriteLedger(string directory, Guid bindingId, bool hasPending)
        {
            string localBinding = Binding(bindingId);
            await File.WriteAllTextAsync(Path.Combine(directory, "launch-requests.json"), "{\"Schema\":2,\"Binding\":" + localBinding + ",\"Requests\":[" + (hasPending ? "{\"Nonce\":\"" + Guid.NewGuid() + "\"}" : "") + "]}");
            await File.WriteAllTextAsync(Path.Combine(directory, "launch-authority.anchor.json"), localBinding);
        }
        var boundImporter = new LegacyImportService(Path.Combine(root, "bound-import-target"), owner, authorityRoot);
        await WriteLedger(adjacent, ledgerId, false); await WriteLedger(authorityRoot, ledgerId, true);
        Regression((await boundImporter.ReadAsync(boundSource)).Scripts.Single().LegacyGuard, "empty adjacent ledger cannot hide pending authority ledger");
        await WriteLedger(adjacent, ledgerId, true); await WriteLedger(authorityRoot, ledgerId, false);
        Regression((await boundImporter.ReadAsync(boundSource)).Scripts.Single().LegacyGuard, "pending adjacent ledger remains guarded regardless of order");
        await WriteLedger(adjacent, ledgerId, false); await WriteLedger(authorityRoot, Guid.NewGuid(), false);
        Regression((await boundImporter.ReadAsync(boundSource)).Scripts.Single().LegacyGuard, "empty adjacent ledger cannot hide mismatched authority binding");
        await WriteLedger(authorityRoot, ledgerId, false);
        Regression(!(await boundImporter.ReadAsync(boundSource)).Scripts.Single().LegacyGuard, "all matching empty authority ledgers permit import without guard");
        var registration = new FakeStartup();
        var boundary = new FakeProcesses(owner);
        await using var manager = new ManagerService(Path.Combine(root, "data"), owner, boundary,
            new UserStartupService(registration, Path.Combine(root, "Manager.exe")));
        await manager.LoadAsync();
        await UseNormalStartupFixtureAsync(manager);
        Guid id = (await manager.AddFilesAsync(new[] { script })).Added.Single();
        var metadata = new ScriptMetadata("Regression", "", null, true, false, false, []);
        Check((await manager.SaveMetadataAsync(id, metadata, true)).Succeeded, "regression setup registers fake startup with valid owner");
        registration.Entry = null; registration.Writes = 0; owner.Held = false;
        var result = await manager.SaveMetadataAsync(id, metadata with { FriendlyName = "changed" }, true);
        Regression(result.Code == ReasonCode.EffectOwnerUnavailable && registration.Writes == 0,
            "startup metadata re-consent cannot write adapter after owner lost");
        owner.Held = true;
        boundary.SetRunning(script, manager.Snapshot.Settings.InterpreterPath!);
        await manager.RefreshAsync();
        await using (var capability = new ProcessManager(owner, documentedControlsProven: true))
            Regression(capability.Supports(ScriptOperation.TogglePause), "proved documented controls expose explicit TogglePause capability");
        var delivered = new OperationRecord(Guid.NewGuid(), id, ScriptOperation.TogglePause, script,
            manager.Snapshot.Settings.InterpreterPath!, "", "", owner.Sid, owner.LoginKey,
            DateTimeOffset.UtcNow, Outcome.Unknown, Code: ReasonCode.StateNotObservable);
        Regression(!OperationJournal.IsPending(delivered), "delivered toggle with unobservable state is terminal history");
        Regression(OperationJournal.IsPending(delivered with { Code = ReasonCode.DispatchFailed }), "actual ambiguous toggle dispatch still prevents blind replay");
        boundary.NextControl = new(Outcome.Unknown, ReasonCode.StateNotObservable);
        var pause = await manager.PrepareAsync(Select(manager, id), ScriptOperation.TogglePause);
        Regression(pause.Allowed, "proved identity permits deliberate documented pause toggle");
        if (pause.Allowed)
        {
            var requested = await manager.PerformAsync(pause, pause.Selection, true);
            Regression(requested.Outcome == Outcome.Unknown && manager.Snapshot.Scripts.Single().Process?.Paused is null,
                "pause request retains unknown observed state");
            Regression((await manager.PrepareAsync(Select(manager, id), ScriptOperation.Stop)).Allowed,
                "unknown pause state does not block proved graceful stop");
            Regression((await manager.PrepareAsync(Select(manager, id), ScriptOperation.TogglePause)).Allowed,
                "second deliberate pause toggle gets new intent without replaying first");
        }
        boundary.NextControl = new(Outcome.Unknown, ReasonCode.ExitNotObserved);
        var failedRestart = await manager.PrepareAsync(Select(manager, id), ScriptOperation.Restart);
        Regression((await manager.PerformAsync(failedRestart, failedRestart.Selection, true)).Outcome == Outcome.Unknown && boundary.Starts == 0,
            "restart with unobserved old exit never launches replacement");
        Regression((await manager.RecoverPendingAsync()).Outcome == Outcome.Unknown && boundary.Starts == 0,
            "recovery never confirms incomplete restart while old identity remains observed");
        boundary.Current = new([], true, []); await manager.RecoverPendingAsync();
        boundary.SetRunning(script, manager.Snapshot.Settings.InterpreterPath!); await manager.RefreshAsync();
        boundary.NextControl = new(Outcome.Confirmed); boundary.UnknownStart = true; boundary.UnknownWithIdentity = true;
        var replacementRestart = await manager.PrepareAsync(Select(manager, id), ScriptOperation.Restart);
        var replacementResult = await manager.PerformAsync(replacementRestart, replacementRestart.Selection, true);
        Regression(replacementResult.Outcome == Outcome.Unknown && boundary.Starts == 1 && (await manager.RecoverPendingAsync()).Succeeded,
            "recovery confirms observed replacement after old exit was proved");
        string versionRoot = Path.Combine(root, "version"); Directory.CreateDirectory(versionRoot);
        string versionScript = Path.Combine(versionRoot, "requires.ahk");
        await File.WriteAllTextAsync(versionScript, "#Requires AutoHotkey v2.999.999\nPersistent\n");
        var versionOwner = new FakeOwner(versionRoot);
        var versionBoundary = new FakeProcesses(versionOwner);
        await using (var versionManager = new ManagerService(Path.Combine(versionRoot, "data"), versionOwner, versionBoundary,
            new UserStartupService(new FakeStartup(), Path.Combine(versionRoot, "Manager.exe"))))
        {
            await versionManager.LoadAsync();
            Guid versionId = (await versionManager.AddFilesAsync(new[] { versionScript })).Added.Single();
            var future = await versionManager.PrepareAsync(Select(versionManager, versionId), ScriptOperation.Start);
            var futureResult = await versionManager.PerformAsync(future, future.Selection, true);
            Regression(futureResult.Code == ReasonCode.UnsupportedInterpreter && versionBoundary.Starts == 0,
                "future required v2 minimum refuses launch before process boundary");
            await File.WriteAllTextAsync(versionScript, "#Requires AutoHotkey v2garbage\nPersistent\n");
            versionBoundary.Current = new([], true, []); await versionManager.RefreshAsync();
            var malformed = await versionManager.PrepareAsync(Select(versionManager, versionId), ScriptOperation.Start);
            var malformedResult = await versionManager.PerformAsync(malformed, malformed.Selection, true);
            Regression(malformedResult.Code == ReasonCode.UnsupportedInterpreter && versionBoundary.Starts == 0,
                "malformed required version refuses launch before process boundary");
            await File.WriteAllTextAsync(versionScript, "#Requires AutoHotkey v2.0\ntext := \"\n(\n#Requires AutoHotkey v1.1\n)\"\nPersistent\n");
            bool literalAllowed = true;
            try { await AutoHotkeyDiscovery.ValidateScriptVersionAsync(versionScript, AutoHotkeyDiscovery.Validate(versionManager.Snapshot.Settings.InterpreterPath!), CancellationToken.None); }
            catch (InvalidDataException) { literalAllowed = false; }
            Regression(literalAllowed, "requires text inside literal continuation is not an interpreter directive");
        }
        Console.WriteLine("FIXTURE " + root);
        if (failures.Count > 0) throw new InvalidOperationException("REGRESSIONS: " + string.Join("; ", failures));
    }
}
