using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend.Core;

public static partial class CoreSuite
{
    public static async Task RunDerivedAnalysisChecksAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        const int count = 40;
        var records = new List<ScriptRecord>();
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(root, $"source-{i}.ahk");
            await File.WriteAllTextAsync(path, "source " + i);
            records.Add(new() { Path = path, FriendlyName = "Script " + i });
        }
        await File.WriteAllBytesAsync(
            Path.Combine(root, "settings.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SettingsDocument { Scripts = records, Settings = DocumentPolicySettings(root) },
                ManagerJson.Default.SettingsDocument
            )
        );
        int calls = 0;
        async Task<HotkeyAnalysis> Analyze(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            string header = await File.ReadAllTextAsync(path, ct);
            return new([], AnalysisCoverage.Partial, [], header);
        }
        var owner = new FakeOwner(root);
        var processes = new FakeProcesses(owner);
        var startup = new FakeStartup();
        await using var manager = new ManagerService(
            root,
            owner,
            processes,
            new UserStartupService(startup, Path.Combine(root, "Manager.exe")),
            null,
            false,
            Analyze
        );
        await manager.LoadAsync();
        check(calls == count, "initial load analyzes all forty existing scripts");
        int before = calls;
        var timer = Stopwatch.StartNew();
        check(
            (
                await manager.SaveMetadataAsync(
                    records[0].Id,
                    new("Renamed", "manual", null, false, false, false, [])
                )
            ).Succeeded,
            "analysis fixture changes metadata without script execution"
        );
        check(
            (await manager.SaveCollectionAsync(null, "Grouping")).Succeeded,
            "analysis fixture adds a collection"
        );
        check(
            (
                await manager.SaveSettingsAsync(
                    manager.Snapshot.Settings with
                    {
                        // Clear the inert bootstrap interpreter; this policy fixture never discovers or validates a Windows executable.
                        InterpreterPath = null,
                        Window = manager.Snapshot.Settings.Window with { Width = 1400 },
                    }
                )
            ).Succeeded,
            "analysis fixture saves window preferences without native interpreter discovery"
        );
        timer.Stop();
        int metadataCalls = calls - before;
        Console.WriteLine(
            $"ANALYSIS scripts={count} metadataCommits=3 analyses={metadataCalls} elapsedMs={timer.ElapsedMilliseconds}"
        );
        check(
            metadataCalls == 0,
            "unrelated metadata, collection and window saves reuse derived analysis"
        );
        await File.WriteAllTextAsync(records[0].Path, "changed before refresh");
        before = calls;
        await manager.RefreshAsync();
        check(
            calls - before == count
                && manager
                    .Snapshot.Scripts.Single(script => script.Id == records[0].Id)
                    .HeaderDescription == "changed before refresh",
            "explicit refresh rereads all scripts and replaces stale derived descriptions"
        );
        before = calls;
        check(
            (await manager.RemoveAsync(records[0].Id)).Succeeded,
            "removing a catalog entry succeeds"
        );
        check(
            calls == before && File.Exists(records[0].Path),
            "catalog removal does not reanalyze other scripts or delete their files"
        );
        await File.WriteAllTextAsync(records[0].Path, "reimported after removal");
        string source = Path.Combine(root, "legacy.json");
        await Import(records[0]);
        check(
            calls - before == 1
                && manager
                    .Snapshot.Scripts.Single(script => script.Id == records[0].Id)
                    .HeaderDescription == "reimported after removal",
            "removal evicts analysis so reimport of the same identity cannot reuse removed state"
        );
        File.Delete(records[0].Path);
        before = calls;
        check(
            (
                await manager.SaveCollectionAsync(
                    manager.Snapshot.Collections.Single().Id,
                    "Renamed grouping"
                )
            ).Succeeded,
            "collection rename succeeds while a script is missing"
        );
        var missing = manager.Snapshot.Scripts.Single(script => script.Id == records[0].Id);
        check(
            calls == before
                && missing.Status == ScriptStatus.Missing
                && missing.HeaderDescription is null,
            "a missing file clears its cached description without reading unrelated files"
        );
        await File.WriteAllTextAsync(records[0].Path, "restored at same path");
        check(
            (
                await manager.SaveMetadataAsync(
                    records[0].Id,
                    new("Restored", "manual", null, false, false, false, [])
                )
            ).Succeeded,
            "metadata save detects a restored file"
        );
        check(
            calls - before == 1
                && manager
                    .Snapshot.Scripts.Single(script => script.Id == records[0].Id)
                    .HeaderDescription == "restored at same path",
            "restored file existence invalidates its former missing analysis"
        );

        var added = new ScriptRecord
        {
            Path = Path.Combine(root, "new-source.ahk"),
            FriendlyName = "New script",
        };
        await File.WriteAllTextAsync(added.Path, "newly imported");
        before = calls;
        await Import(added);
        check(
            calls - before == 1 && manager.Snapshot.Scripts.Length == count + 1,
            "a new catalog identity analyzes only its own source"
        );
        await File.WriteAllTextAsync(records[1].Path, "changed before restore");
        before = calls;
        check((await manager.RestoreBackupAsync()).Succeeded, "explicit backup restore succeeds");
        check(
            calls - before == manager.Snapshot.Scripts.Length
                && manager
                    .Snapshot.Scripts.Single(script => script.Id == records[1].Id)
                    .HeaderDescription == "changed before restore",
            "backup restore forces fresh analysis instead of retaining previous display data"
        );
        await File.WriteAllTextAsync(records[1].Path, "changed before invalidation");
        before = calls;
        var refreshed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void OnChanged(object? sender, ManagerSnapshot snapshot) => refreshed.TrySetResult();
        manager.Changed += OnChanged;
        try
        {
            processes.Invalidate();
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            manager.Changed -= OnChanged;
        }
        check(
            calls - before == manager.Snapshot.Scripts.Length
                && manager
                    .Snapshot.Scripts.Single(script => script.Id == records[1].Id)
                    .HeaderDescription == "changed before invalidation",
            "process invalidation retains full source reanalysis"
        );
        check(
            processes.Starts == 0 && processes.Controls == 0 && startup.Writes == 0,
            "derived analysis and catalog changes never dispatch process or startup effects"
        );

        async Task Import(ScriptRecord record)
        {
            var document = new JsonObject
            {
                ["Schema"] = 3,
                ["Scripts"] = new JsonArray(
                    new JsonObject
                    {
                        ["Id"] = record.Id.ToString("D"),
                        ["Path"] = record.Path,
                        ["FriendlyName"] = record.FriendlyName,
                        ["RunAsAdministrator"] = false,
                        ["ManagementMode"] = 1,
                    }
                ),
            };
            await File.WriteAllTextAsync(source, document.ToJsonString());
            var preview = await manager.PreviewImportAsync(source);
            check(
                preview.CanImport && (await manager.ImportAsync(preview)).Succeeded,
                "read-only legacy preview and explicit import preserve source-backed identities"
            );
        }
    }
}
