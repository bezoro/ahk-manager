using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupRecoveryChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string cases = Path.Combine(root, "startup-recovery-records");
        Directory.CreateDirectory(cases);
        var mutations = new Dictionary<string, Action<JsonObject>>
        {
            ["missing state array"] = value => value.Remove("Items"),
            ["null state array"] = value => value["Items"] = null,
            ["missing ticket"] = value => value.Remove("Ticket"),
            ["null ticket"] = value => value["Ticket"] = null,
            ["missing ticket items"] = value => value["Ticket"]!.AsObject().Remove("Items"),
            ["null ticket items"] = value => value["Ticket"]!["Items"] = null,
            ["null ticket item"] = value => value["Ticket"]!["Items"]![0] = null,
            ["NUL in item name"] = value => value["Ticket"]!["Items"]![0]!["Name"] = "bad\0name",
            ["NUL in profile path"] = value => value["Ticket"]!["ProfileRoot"] = "bad\0path",
            ["invalid item hash"] = value =>
                value["Ticket"]!["Items"]![0]!["ScriptHash"] = new string('z', 64),
            ["invalid phase"] = value => value["Phase"] = 999,
            ["invalid item state"] = value => value["Items"]![0] = 999,
            ["missing phase"] = value => value.Remove("Phase"),
            ["invalid ticket shape"] = value => value["Ticket"] = "not an object",
            ["NUL in result detail"] = value =>
                value["Result"] = new JsonObject
                {
                    ["Outcome"] = (int)Outcome.Failed,
                    ["Detail"] = "bad\0detail",
                },
            ["unknown member"] = value => value["Unexpected"] = true,
        };
        int index = 0;
        foreach (var (name, mutate) in mutations)
        {
            string data = Path.Combine(cases, "case-" + index++);
            var record = Record(data);
            var json = JsonSerializer
                .SerializeToNode(record, ContinuationJson.Default.StartupContinuationRecord)!
                .AsObject();
            mutate(json);
            await RejectAsync(
                data,
                record.Ticket.OperationId,
                Encoding.UTF8.GetBytes(json.ToJsonString()),
                name,
                check
            );
        }
        foreach (
            var (name, bytes) in new[]
            {
                ("truncated JSON", Encoding.UTF8.GetBytes("{\"Ticket\":")),
                ("invalid UTF8", new byte[] { 0xff, 0xfe, 0xff }),
                (
                    "oversized JSON",
                    Enumerable
                        .Repeat((byte)' ', StartupContinuationStore.MaximumBytes + 1)
                        .ToArray()
                ),
            }
        )
            await RejectAsync(
                Path.Combine(cases, "case-" + index++),
                Guid.NewGuid(),
                bytes,
                name,
                check
            );
        string duplicateData = Path.Combine(cases, "duplicate");
        var duplicate = Record(duplicateData);
        string encoded = JsonSerializer.Serialize(
            duplicate,
            ContinuationJson.Default.StartupContinuationRecord
        );
        await RejectAsync(
            duplicateData,
            duplicate.Ticket.OperationId,
            Encoding.UTF8.GetBytes(encoded[..^1] + ",\"Items\":[0]}"),
            "duplicate properties",
            check
        );

        var invalidWrite = Record(Path.Combine(cases, "invalid-write"));
        bool refusedWrite = false;
        try
        {
            await new StartupContinuationStore(invalidWrite.Ticket.ProfileRoot).WriteAsync(
                invalidWrite with
                {
                    Items = default,
                },
                null
            );
        }
        catch (InvalidDataException)
        {
            refusedWrite = true;
        }
        check(
            refusedWrite
                && !Directory.Exists(
                    Path.Combine(invalidWrite.Ticket.ProfileRoot, "startup-continuations")
                ),
            "invalid typed state array refuses before creating a recovery record"
        );
        await QuarantineChecksAsync(Path.Combine(cases, "quarantine"), check);
        await ManagerRecoveryAsync(Path.Combine(cases, "manager"), check);
    }

    static StartupContinuationRecord Record(string data)
    {
        var item = new StartupContinuationItem(
            Guid.NewGuid(),
            "Recovery fixture",
            Path.Combine(data, "inert.ahk"),
            Path.Combine(data, "inert.exe"),
            new string('a', 64),
            new string('b', 64)
        );
        var ticket = new StartupContinuationTicket(
            1,
            Guid.NewGuid(),
            data,
            new string('c', 64),
            "S-1-recovery-fixture",
            1,
            "recovery-fixture-login",
            DateTimeOffset.UtcNow.AddMinutes(5),
            [item]
        );
        return new(
            ticket,
            StartupContinuationStore.Hash(ticket),
            StartupContinuationPhase.Prepared,
            [StartupContinuationItemState.NotDispatched]
        );
    }

    static string ActiveFile(string data, Guid id) =>
        Path.Combine(data, "startup-continuations", id.ToString("D") + ".json");

    static async Task RejectAsync(
        string data,
        Guid id,
        byte[] bytes,
        string name,
        Action<bool, string> check
    )
    {
        string file = ActiveFile(data, id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, bytes);
        bool known = false;
        try
        {
            _ = await new StartupContinuationStore(data).ReadAsync(id);
        }
        catch (InvalidDataException ex)
        {
            known =
                ex.Message.Contains(file, StringComparison.Ordinal)
                && ex.Message.Contains("preservado", StringComparison.Ordinal);
        }
        check(known, "malformed continuation remains a named known recovery failure: " + name);
        check(
            (await File.ReadAllBytesAsync(file)).SequenceEqual(bytes),
            "invalid continuation evidence stays byte identical: " + name
        );
    }

    static async Task QuarantineChecksAsync(string data, Action<bool, string> check)
    {
        var store = new StartupContinuationStore(data);
        var valid = Record(data);
        await store.WriteAsync(valid, null);
        byte[] validBefore = await File.ReadAllBytesAsync(
            ActiveFile(data, valid.Ticket.OperationId)
        );
        var originals = new Dictionary<string, byte[]>
        {
            [ActiveFile(data, Guid.NewGuid())] = Encoding.UTF8.GetBytes("{\"Items\":null}"),
            [ActiveFile(data, Guid.NewGuid())] = Encoding.UTF8.GetBytes(
                "truncated invalid evidence"
            ),
            [Path.Combine(data, "startup-continuations", "unexpected.json")] =
                Encoding.UTF8.GetBytes("unexpected filename evidence"),
        };
        foreach (var (path, bytes) in originals)
            await File.WriteAllBytesAsync(path, bytes);
        bool invalidInventory = false;
        try
        {
            _ = await store.PendingAsync();
        }
        catch (InvalidDataException)
        {
            invalidInventory = true;
        }
        check(invalidInventory, "invalid recovery inventory blocks before explicit quarantine");
        var preserved = await store.QuarantineInvalidAsync();
        check(
            preserved.Length == originals.Count && originals.Keys.All(path => !File.Exists(path)),
            "explicit quarantine moves only invalid records out of the active inventory"
        );
        foreach (byte[] bytes in originals.Values)
            check(
                preserved.Count(path => File.ReadAllBytes(path).SequenceEqual(bytes)) == 1,
                "quarantine preserves each original recovery byte sequence exactly once"
            );
        string destination = Path.GetDirectoryName(preserved[0])!;
        check(
            File.Exists(Path.Combine(destination, "manifest.json"))
                && File.Exists(Path.Combine(destination, "completed.json")),
            "quarantine preserves its manifest and completed replay-barrier marker"
        );
        check(
            (
                await File.ReadAllBytesAsync(ActiveFile(data, valid.Ticket.OperationId))
            ).SequenceEqual(validBefore)
                && (await store.PendingAsync()).Single().Ticket.OperationId
                    == valid.Ticket.OperationId,
            "quarantine does not alter valid pending authority evidence"
        );
        check(
            (await store.QuarantineInvalidAsync()).IsEmpty,
            "repeated quarantine cannot replay or duplicate preserved evidence"
        );
        string indented = JsonSerializer
            .SerializeToNode(valid, ContinuationJson.Default.StartupContinuationRecord)!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ActiveFile(data, valid.Ticket.OperationId), indented);
        var reread = (await store.ReadAsync(valid.Ticket.OperationId))!;
        await store.WriteAsync(reread with { Phase = StartupContinuationPhase.Recovered }, reread);
        check(
            (await store.ReadAsync(valid.Ticket.OperationId))!.Phase
                == StartupContinuationPhase.Recovered,
            "valid indented recovery evidence can be settled with semantic compare and exact current-byte hash"
        );

        string interruptedData = Path.Combine(data, "interrupted-recovery");
        string interruptedMarker = ActiveFile(interruptedData, Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(interruptedMarker)!);
        byte[] interruptedBytes = Encoding.UTF8.GetBytes(
            "{\"OperationId\":\"interrupted-recovery-marker\",\"Files\":[]}"
        );
        await File.WriteAllBytesAsync(interruptedMarker, interruptedBytes);
        string oldEvidence = Path.Combine(
            interruptedData,
            "startup-continuations",
            "quarantine",
            "prior-attempt",
            "preserved.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(oldEvidence)!);
        await File.WriteAllTextAsync(oldEvidence, "evidence from the interrupted attempt");
        var interruptedStore = new StartupContinuationStore(interruptedData);
        bool interruptedBlocked = false;
        try
        {
            _ = await interruptedStore.PendingAsync();
        }
        catch (InvalidDataException)
        {
            interruptedBlocked = true;
        }
        var recoveredMarker = await interruptedStore.QuarantineInvalidAsync();
        check(
            interruptedBlocked
                && recoveredMarker.Length == 1
                && (await File.ReadAllBytesAsync(recoveredMarker[0])).SequenceEqual(
                    interruptedBytes
                )
                && (await interruptedStore.PendingAsync()).IsEmpty
                && await File.ReadAllTextAsync(oldEvidence)
                    == "evidence from the interrupted attempt",
            "interrupted quarantine marker blocks automatic use and a new explicit recovery preserves both attempts"
        );

        string changedData = Path.Combine(data, "changed-inventory");
        var changedStore = new StartupContinuationStore(changedData);
        string original = ActiveFile(changedData, Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        byte[] changedBytes = Encoding.UTF8.GetBytes("preserve me if inventory changes");
        await File.WriteAllBytesAsync(original, changedBytes);
        bool changedRefused = false;
        try
        {
            await changedStore.QuarantineInvalidAsync(() =>
                File.WriteAllTextAsync(
                    ActiveFile(changedData, Guid.NewGuid()),
                    "new concurrent record"
                )
            );
        }
        catch (ConcurrentWriteException)
        {
            changedRefused = true;
        }
        check(
            changedRefused
                && (await File.ReadAllBytesAsync(original)).SequenceEqual(changedBytes)
                && !Directory.Exists(
                    Path.Combine(changedData, "startup-continuations", "quarantine")
                ),
            "changed recovery inventory refuses before the first move and preserves original bytes"
        );
    }

    static async Task ManagerRecoveryAsync(string root, Action<bool, string> check)
    {
        foreach (string scenario in new[] { "complete", "incomplete", "pending-journal" })
        {
            string data = Path.Combine(root, scenario);
            Directory.CreateDirectory(data);
            var settings = new SettingsDocument
            {
                Settings = new() { InterpreterPath = Path.Combine(data, "inert-not-executed.exe") },
            };
            await File.WriteAllBytesAsync(
                Path.Combine(data, "settings.json"),
                JsonSerializer.SerializeToUtf8Bytes(settings, ManagerJson.Default.SettingsDocument)
            );
            Guid badId = Guid.NewGuid();
            string invalid = ActiveFile(data, badId);
            Directory.CreateDirectory(Path.GetDirectoryName(invalid)!);
            byte[] bytes = Encoding.UTF8.GetBytes("{\"Ticket\":null,\"Items\":null}");
            await File.WriteAllBytesAsync(invalid, bytes);
            var valid = Record(data) with
            {
                Phase = StartupContinuationPhase.Accepted,
                Items = [StartupContinuationItemState.EnteringExecution],
            };
            var store = new StartupContinuationStore(data);
            await store.WriteAsync(valid, null);
            var owner = new RecoveryOwner();
            var processes = new RecoveryProcesses(scenario != "incomplete");
            var startup = new FakeStartup();
            if (scenario == "pending-journal")
            {
                var journal = new OperationJournal(Path.Combine(data, "operations.json"));
                await journal.LoadAsync();
                await journal.PutAsync(
                    new(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        ScriptOperation.RecycleFile,
                        Path.Combine(data, "unconfirmed.ahk"),
                        "",
                        "",
                        "",
                        owner.Sid,
                        owner.LoginKey,
                        DateTimeOffset.UtcNow,
                        Outcome.Unknown,
                        Code: ReasonCode.PendingOperation
                    )
                );
            }
            await using (
                var manager = new ManagerService(
                    data,
                    owner,
                    processes,
                    new UserStartupService(startup, Path.Combine(data, "Manager.exe"))
                )
            )
            {
                await manager.LoadAsync();
                check(
                    !manager.Snapshot.EffectsAvailable
                        && manager.Snapshot.Issues.Any(issue =>
                            issue.Code == ReasonCode.PendingOperation
                        ),
                    "corrupt continuation loads into recovery instead of crashing: " + scenario
                );
                var result = await manager.RecoverPendingAsync();
                if (scenario == "complete")
                {
                    check(
                        result.Succeeded
                            && manager.Snapshot.EffectsAvailable
                            && !File.Exists(invalid),
                        "explicit recovery releases startup only after complete observation and preserved invalid records"
                    );
                    check(
                        (await store.ReadAsync(valid.Ticket.OperationId))!.Phase
                            == StartupContinuationPhase.Recovered
                            && await manager.GetPreparedStartupContinuationAsync(
                                valid.Ticket.OperationId
                            )
                                is null,
                        "explicit recovery closes valid uncertain evidence without reconstructing continuation authority"
                    );
                    var copies = Directory
                        .EnumerateFiles(
                            Path.Combine(data, "startup-continuations", "quarantine"),
                            "*-"
                                + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                                + ".json",
                            SearchOption.AllDirectories
                        )
                        .ToArray();
                    check(
                        copies.Length == 1
                            && (await File.ReadAllBytesAsync(copies[0])).SequenceEqual(bytes),
                        "manager recovery preserves corrupt source bytes in a named quarantine copy"
                    );
                    check(
                        manager.Snapshot.Activity.Any(activity =>
                            activity.Operation == ScriptOperation.Recover
                            && activity.Detail?.Contains("preservado", StringComparison.Ordinal)
                                == true
                        ),
                        "manager recovery records preserved evidence in activity"
                    );
                }
                else
                    check(
                        !result.Succeeded
                            && !manager.Snapshot.EffectsAvailable
                            && (await File.ReadAllBytesAsync(invalid)).SequenceEqual(bytes)
                            && !Directory.Exists(
                                Path.Combine(data, "startup-continuations", "quarantine")
                            ),
                        "unsafe observation or unresolved process request prevents any quarantine move: "
                            + scenario
                    );
                check(
                    processes.Starts == 0 && processes.Controls == 0 && startup.Writes == 0,
                    "recovery never dispatches scripts, process control or Windows startup writes: "
                        + scenario
                );
            }
            if (scenario == "complete")
            {
                var reopenedProcesses = new RecoveryProcesses(true);
                await using var reopened = new ManagerService(
                    data,
                    new RecoveryOwner(),
                    reopenedProcesses,
                    new UserStartupService(new FakeStartup(), Path.Combine(data, "Manager.exe"))
                );
                await reopened.LoadAsync();
                check(
                    reopened.Snapshot.EffectsAvailable
                        && reopenedProcesses.Starts == 0
                        && reopenedProcesses.Controls == 0,
                    "reopened recovered profile loads without replaying a continuation"
                );
            }
        }
    }

    sealed class RecoveryOwner : IEffectOwner
    {
        public bool Held => true;
        public bool Trusted => true;
        public string Sid => "S-1-recovery-fixture";
        public string LoginKey => "recovery-fixture-login";

        public bool Allows(string scriptPath, string interpreterPath) => true;

        public void Dispose() { }
    }

    sealed class RecoveryProcesses(bool complete) : IProcessManager
    {
        public int Starts,
            Controls;
        public event EventHandler? Invalidated
        {
            add { }
            remove { }
        }

        public bool Supports(ScriptOperation operation) => true;

        public Task<ObservedProcesses> ObserveAsync(
            IReadOnlyCollection<string> interpreters,
            CancellationToken ct = default
        ) => Task.FromResult(new ObservedProcesses([], complete, []));

        public Task<LaunchResult> StartAsync(
            string interpreter,
            string script,
            bool manuallyElevated,
            CancellationToken ct = default
        )
        {
            Starts++;
            return Task.FromResult(
                new LaunchResult(new(Outcome.Failed, ReasonCode.DispatchFailed), null)
            );
        }

        public Task<OperationResult> ControlAsync(
            ProcessIdentity expected,
            ScriptOperation operation,
            CancellationToken ct = default
        )
        {
            Controls++;
            return Task.FromResult(new OperationResult(Outcome.Failed, ReasonCode.DispatchFailed));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
