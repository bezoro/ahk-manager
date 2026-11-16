using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend.Core;

public static partial class CoreSuite
{
    public static async Task RunDocumentPolicyChecksAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        string[] schemas = ["1.5", "2147483648", "1e100", "\"1\"", "null", "true", "[]", "{}"];
        foreach (var (schema, index) in schemas.Select((value, index) => (value, index)))
        {
            string directory = Path.Combine(root, "journal-" + index);
            Directory.CreateDirectory(directory);
            string journalPath = Path.Combine(directory, "operations.json");
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"Schema\":" + schema + ",\"Operations\":[]}"
            );
            byte[] backup = System.Text.Encoding.UTF8.GetBytes("preserved journal backup");
            await File.WriteAllBytesAsync(journalPath, bytes);
            await File.WriteAllBytesAsync(journalPath + ".bak", backup);
            var journal = new OperationJournal(journalPath);
            bool rejected = false;
            try
            {
                await journal.LoadAsync();
            }
            catch (Exception error)
                when (error is UnsupportedSchemaException or InvalidDataException or JsonException)
            {
                rejected = true;
            }
            check(
                rejected && !journal.Ready,
                "malformed journal schema is rejected safely: " + schema
            );

            var record = new ScriptRecord
            {
                Path = Path.Combine(directory, "missing.ahk"),
                FriendlyName = "Preserved catalog entry",
            };
            byte[] settings = JsonSerializer.SerializeToUtf8Bytes(
                new SettingsDocument
                {
                    Scripts = [record],
                    Settings = DocumentPolicySettings(directory),
                },
                ManagerJson.Default.SettingsDocument
            );
            string settingsPath = Path.Combine(directory, "settings.json");
            await File.WriteAllBytesAsync(settingsPath, settings);
            var owner = new FakeOwner(directory);
            var processes = new FakeProcesses(owner);
            await using var manager = new ManagerService(
                directory,
                owner,
                processes,
                new UserStartupService(new FakeStartup(), Path.Combine(directory, "Manager.exe"))
            );
            await manager.LoadAsync();
            check(
                manager.Snapshot.Scripts.Single().Id == record.Id
                    && manager.Snapshot.Issues.Any(issue =>
                        issue.Code == ReasonCode.PendingOperation
                    )
                    && !manager.Snapshot.EffectsAvailable
                    && processes.Starts == 0
                    && processes.Controls == 0,
                "malformed journal leaves the catalog inspectable with effects blocked: " + schema
            );
            check(
                (await File.ReadAllBytesAsync(journalPath)).SequenceEqual(bytes)
                    && (await File.ReadAllBytesAsync(journalPath + ".bak")).SequenceEqual(backup)
                    && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(settings),
                "malformed journal loading preserves primary, backup and settings bytes: " + schema
            );
        }

        await CheckLegacyDocumentNumbersAsync(Path.Combine(root, "legacy"), schemas, check);
        await CheckCollectionNameIdentityAsync(Path.Combine(root, "collections"), check);
    }

    static ManagerSettings DocumentPolicySettings(string root) =>
        new()
        {
            // No Windows interpreter discovery or real startup registration belongs in policy tests.
            InterpreterPath = Path.Combine(root, "inert-interpreter.exe"),
            LaunchManagerAtLogin = false,
            LaunchManagerAsAdministrator = false,
        };

    static async Task CheckLegacyDocumentNumbersAsync(
        string root,
        IEnumerable<string> schemas,
        Action<bool, string> check
    )
    {
        Directory.CreateDirectory(root);
        var owner = new FakeOwner(root);
        var importer = new LegacyImportService(Path.Combine(root, "destination"), owner);
        string source = Path.Combine(root, "legacy.json");
        foreach (string schema in schemas)
        {
            string text = "{\"Schema\":" + schema + ",\"Scripts\":[]}";
            await File.WriteAllTextAsync(source, text);
            bool rejected = false;
            try
            {
                await importer.ReadAsync(source);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            check(
                rejected && await File.ReadAllTextAsync(source) == text,
                "malformed legacy schema is rejected without changing source: " + schema
            );
            text = "{\"Schema\":3,\"Scripts\":[],\"Migration\":{\"State\":" + schema + "}}";
            await File.WriteAllTextAsync(source, text);
            rejected = false;
            try
            {
                await importer.ReadAsync(source);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            check(
                rejected && await File.ReadAllTextAsync(source) == text,
                "malformed legacy migration state cannot become safe authority: " + schema
            );
        }

        Guid ledgerId = Guid.NewGuid();
        JsonObject Binding() =>
            new()
            {
                ["Sid"] = owner.Sid,
                ["Fixture"] = false,
                ["LedgerId"] = ledgerId.ToString("D"),
            };
        var row = new JsonObject
        {
            ["Id"] = Guid.NewGuid().ToString("D"),
            ["Path"] = Path.Combine(root, "external.ahk"),
            ["RunAsAdministrator"] = false,
            ["ManagementMode"] = 1,
        };
        await File.WriteAllTextAsync(
            source,
            new JsonObject
            {
                ["Schema"] = 4,
                ["Authority"] = Binding(),
                ["Scripts"] = new JsonArray(row),
            }.ToJsonString()
        );
        await File.WriteAllTextAsync(
            Path.Combine(root, "launch-authority.anchor.json"),
            Binding().ToJsonString()
        );
        foreach (string schema in schemas)
        {
            var ledger = new JsonObject
            {
                ["Schema"] = JsonNode.Parse(schema),
                ["Binding"] = Binding(),
                ["Requests"] = new JsonArray(),
            };
            string ledgerPath = Path.Combine(root, "launch-requests.json");
            string text = ledger.ToJsonString();
            await File.WriteAllTextAsync(ledgerPath, text);
            var imported = await importer.ReadAsync(source);
            check(
                imported.Scripts.Single().LegacyGuard
                    && await File.ReadAllTextAsync(ledgerPath) == text,
                "malformed authority schema remains guarded and unchanged: " + schema
            );
        }
        var validLedger = new JsonObject
        {
            ["Schema"] = 2,
            ["Binding"] = Binding(),
            ["Requests"] = new JsonArray(),
        };
        string ledgerFile = Path.Combine(root, "launch-requests.json");
        string anchorFile = Path.Combine(root, "launch-authority.anchor.json");
        await File.WriteAllTextAsync(ledgerFile, validLedger.ToJsonString());
        check(
            !(await importer.ReadAsync(source)).Scripts.Single().LegacyGuard,
            "a matching integer-schema authority ledger remains accepted"
        );
        foreach (
            string malformed in new[]
            {
                "binding-null",
                "binding-id-number",
                "binding-id-invalid",
                "anchor-array",
                "anchor-id-invalid",
            }
        )
        {
            var ledger = (JsonObject)validLedger.DeepClone();
            JsonNode anchor = Binding();
            switch (malformed)
            {
                case "binding-null":
                    ledger["Binding"] = null;
                    break;
                case "binding-id-number":
                    ledger["Binding"]!["LedgerId"] = 42;
                    break;
                case "binding-id-invalid":
                    ledger["Binding"]!["LedgerId"] = "not-a-guid";
                    break;
                case "anchor-array":
                    anchor = new JsonArray();
                    break;
                case "anchor-id-invalid":
                    anchor["LedgerId"] = "not-a-guid";
                    break;
            }
            await File.WriteAllTextAsync(ledgerFile, ledger.ToJsonString());
            await File.WriteAllTextAsync(anchorFile, anchor.ToJsonString());
            check(
                (await importer.ReadAsync(source)).Scripts.Single().LegacyGuard,
                "malformed authority identity stays guarded: " + malformed
            );
        }
    }

    static async Task CheckCollectionNameIdentityAsync(string root, Action<bool, string> check)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Directory.CreateDirectory(root);
            string settingsPath = Path.Combine(root, "settings.json");
            await File.WriteAllBytesAsync(
                settingsPath,
                JsonSerializer.SerializeToUtf8Bytes(
                    new SettingsDocument { Settings = DocumentPolicySettings(root) },
                    ManagerJson.Default.SettingsDocument
                )
            );
            var owner = new FakeOwner(root);
            await using var manager = new ManagerService(
                root,
                owner,
                new FakeProcesses(owner),
                new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe"))
            );
            await manager.LoadAsync();
            check(
                (await manager.SaveCollectionAsync(null, "FILE")).Succeeded,
                "first collection name is saved under Turkish culture"
            );
            byte[] saved = await File.ReadAllBytesAsync(settingsPath);
            var duplicate = await manager.SaveCollectionAsync(null, "file");
            check(
                duplicate.Code == ReasonCode.InvalidMetadata
                    && manager.Snapshot.Collections.Length == 1
                    && (await File.ReadAllBytesAsync(settingsPath)).SequenceEqual(saved),
                "ordinal duplicate collection is refused before persistence, independent of culture"
            );
            check(
                (await manager.SaveCollectionAsync(null, "fıle")).Succeeded
                    && manager.Snapshot.Collections.Length == 2,
                "ordinally distinct collection remains valid under Turkish culture"
            );
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
