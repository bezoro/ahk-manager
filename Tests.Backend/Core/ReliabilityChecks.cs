using System.Text.Json;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend.Core;

public static partial class CoreSuite
{
    public static async Task RunReliabilityChecksAsync(string root, Action<bool, string> check)
    {
        await CheckRecoveryProofs(Path.Combine(root, "journal"), check);
        await CheckOverlappingInvalidations(Path.Combine(root, "refresh"), check);
        await CheckOversizedRestore(Path.Combine(root, "restore"), check);
    }

    static async Task CheckRecoveryProofs(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var settings = DocumentPolicySettings(root);
        string path = Path.Combine(root, "operations.json");
        await File.WriteAllBytesAsync(
            Path.Combine(root, "settings.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SettingsDocument { Settings = settings },
                ManagerJson.Default.SettingsDocument
            )
        );
        var owner = new FakeOwner(root);
        var processes = new FakeProcesses(owner);
        var identity = new ProcessIdentity(
            7111,
            12345,
            settings.InterpreterPath!,
            Path.Combine(root, "source.ahk"),
            owner.Sid,
            1,
            "observed-script",
            "observed-interpreter"
        );
        var start = new OperationRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ScriptOperation.Start,
            identity.ScriptPath,
            identity.ExecutablePath,
            identity.ScriptHash,
            identity.InterpreterHash,
            owner.Sid,
            owner.LoginKey,
            DateTimeOffset.UtcNow,
            Outcome.Unknown,
            identity
        );
        var restart = start with
        {
            Id = Guid.NewGuid(),
            Operation = ScriptOperation.Restart,
            RestartOldExited = true,
            ScriptHash = "planned-script",
        };
        var changedInterpreter = start with
        {
            Id = Guid.NewGuid(),
            InterpreterHash = "planned-interpreter",
        };
        var journal = new OperationJournal(path);
        await journal.LoadAsync();
        foreach (var record in new[] { start, restart, changedInterpreter })
            check((await journal.PutAsync(record)).Succeeded, "seed durable launch receipt");
        processes.Current = new(
            [new(identity, ExecutionOrigin.Manager, "2", DateTimeOffset.UtcNow)],
            true,
            []
        );
        await using var manager = new ManagerService(
            root,
            owner,
            processes,
            new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe"))
        );
        await manager.LoadAsync();
        await journal.LoadAsync();
        check(
            journal.Records.Single(r => r.Id == start.Id).Outcome == Outcome.Confirmed
                && journal.Records.Count(OperationJournal.IsPending) == 2,
            "only a matching receipt with both planned hashes confirms a live start or restart"
        );
        byte[] reserved = await File.ReadAllBytesAsync(path);
        processes.Current = new([], true, []);
        using (File.Open(path + ".write.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            check(
                !(await manager.RecoverPendingAsync()).Succeeded,
                "writer contention preserves pending reservations"
            );
            check(
                !(await manager.RecoverPendingAsync()).Succeeded,
                "recovery respects the active writer lease"
            );
        }
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new JournalDocument(),
                ManagerJson.Default.JournalDocument
            )
        );
        check(
            !(await manager.RecoverPendingAsync()).Succeeded,
            "a replacement journal cannot erase durable reservations"
        );
        File.Delete(path);
        check(
            !(await manager.RecoverPendingAsync()).Succeeded,
            "a missing journal cannot erase durable reservations"
        );
        await File.WriteAllBytesAsync(path, reserved);
        check(
            (await manager.RecoverPendingAsync()).Succeeded,
            "same manager recovers when the exact durable journal is available"
        );
        await journal.LoadAsync();
        check(
            journal.Records.Count == 3
                && !journal.HasPending
                && processes.Starts == 0
                && processes.Controls == 0,
            "recovery settles proven absence and never dispatches a process effect"
        );
    }

    static async Task CheckOverlappingInvalidations(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var script = new ScriptRecord
        {
            Path = Path.Combine(root, "script.ahk"),
            FriendlyName = "Fixture",
        };
        await File.WriteAllTextAsync(script.Path, "; inert");
        var settings = DocumentPolicySettings(root);
        await File.WriteAllTextAsync(settings.InterpreterPath!, "inert fixture");
        await File.WriteAllBytesAsync(
            Path.Combine(root, "settings.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SettingsDocument { Scripts = [script], Settings = settings },
                ManagerJson.Default.SettingsDocument
            )
        );
        var owner = new FakeOwner(root);
        var processes = new FakeProcesses(owner) { AfterObserve = _ => { } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int analyses = 0;
        async Task<HotkeyAnalysis> Analyze(string path, CancellationToken ct)
        {
            if (Interlocked.Increment(ref analyses) == 2)
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            return new([], AnalysisCoverage.Partial, []);
        }
        await using var manager = new ManagerService(
            root,
            owner,
            processes,
            new UserStartupService(new FakeStartup(), Path.Combine(root, "Manager.exe")),
            null,
            false,
            Analyze
        );
        await manager.LoadAsync();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += (_, snapshot) =>
        {
            if (snapshot.Scripts.Single().Status == ScriptStatus.Stopped)
                stopped.TrySetResult();
        };
        processes.SetRunning(script.Path, settings.InterpreterPath!);
        processes.Invalidate();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        processes.Current = new([], true, []);
        processes.Invalidate();
        processes.Invalidate();
        release.SetResult();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(
            processes.Observations == 3 && analyses == 3,
            "two overlapping invalidations result in one trailing observation"
        );
    }

    static async Task CheckOversizedRestore(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        byte[] backup = JsonSerializer.SerializeToUtf8Bytes(
            new SettingsDocument { Settings = DocumentPolicySettings(root) },
            ManagerJson.Default.SettingsDocument
        );
        await File.WriteAllBytesAsync(path + ".bak", backup);
        byte[] oversized = new byte[SafeJsonFiles.MaximumBytes + 1];
        new Random(168).NextBytes(oversized);
        await File.WriteAllBytesAsync(path, oversized);
        var store = new SettingsStore(path);
        var loaded = await store.LoadAsync();
        check(
            loaded.ReadOnly && loaded.Issues.Any(i => i.Code == ReasonCode.RecoveredBackup),
            "oversized primary exposes validated backup"
        );
        using (
            File.Open(
                path + ".write.lock",
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None
            )
        )
            check(
                !(await store.RestoreBackupAsync()).Succeeded,
                "restore respects the existing writer lease"
            );
        check(
            (await store.RestoreBackupAsync()).Succeeded,
            "explicit restore replaces an oversized primary"
        );
        string preserved = Directory.GetFiles(root, "settings.json.recovery-*").Single();
        check(
            (await File.ReadAllBytesAsync(preserved)).SequenceEqual(oversized)
                && (await File.ReadAllBytesAsync(path)).SequenceEqual(backup)
                && (await File.ReadAllBytesAsync(path + ".bak")).SequenceEqual(backup)
                && !(await store.LoadAsync()).ReadOnly,
            "restoration preserves every original byte, the recovery backup and a writable profile"
        );
    }
}
