using System.Text.Json;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupTransferChecks
{
    const string Sid = "S-1-5-21-100-200-300-1001";

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        foreach (string mode in new[] { "new-task", "current-task", "relocated-task", "standard" })
        {
            await using var f = await Fixture.Create(root, mode, elevated: mode != "standard");
            if (mode == "current-task")
                f.Adapter.Task = TaskFor(f.Startup.Command);
            if (mode == "relocated-task")
                f.Adapter.Task = TaskFor(f.PreviousEntry) with
                {
                    NonstandardWorkingDirectory = "old working directory",
                };
            var previousTask = f.Adapter.Task;
            var preview = f.Transfer.Preview();
            check(
                preview.CanTransfer && f.Adapter.Writes == 0 && !Directory.Exists(f.Backups),
                "transfer preview is effect-free: " + mode
            );
            bool backupBeforeRemoval = false;
            f.Adapter.BeforeEntryChange = () =>
            {
                using var saved = JsonDocument.Parse(
                    File.ReadAllBytes(Directory.GetFiles(f.Backups, "*.json").Single())
                );
                var value = saved.RootElement;
                backupBeforeRemoval =
                    value.GetProperty("previousRunEntry").GetString() == f.PreviousEntry
                    && value.GetProperty("currentCommand").GetString() == f.Startup.Command
                    && value.GetProperty("userSid").GetString() == Sid
                    && value.GetProperty("launchManagerAsAdministrator").GetBoolean()
                        == (mode != "standard");
                if (previousTask is not null)
                    backupBeforeRemoval &=
                        value.GetProperty("previousTask").GetProperty("command").GetString()
                            == previousTask.Command
                        && value.GetProperty("previousTask").GetProperty("fingerprint").GetString()
                            == previousTask.Fingerprint;
            };
            var result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            check(
                result.Succeeded
                    && backupBeforeRemoval
                    && f.Startup.RegistrationStatus(true, mode != "standard") == ReasonCode.None,
                "confirmed transfer establishes saved startup mode after durable backup: " + mode
            );
            check(
                mode == "standard"
                    ? f.Adapter.Entry == f.Startup.Command && f.Adapter.Task is null
                    : f.Adapter.Entry is null
                        && f.Adapter.Task
                            is {
                                Enabled: true,
                                HighestPrivileges: true,
                                InteractiveToken: true,
                                UserLogonOnly: true
                            } task
                        && task.Command == f.Startup.Command
                        && task.UserSid == Sid,
                "transfer leaves exactly one supported registration, never an elevated Run entry: "
                    + mode
            );
            check(
                f.Preserved()
                    && (
                        mode != "current-task"
                        || f.Adapter.Task == previousTask && f.Adapter.TaskWrites == 0
                    ),
                "transfer preserves settings, external scripts, processes and an already correct task: "
                    + mode
            );
            check(
                (await f.Transfer.CommitAsync(preview.PreviewId, true)).Code
                    == ReasonCode.StaleSelection,
                "consumed transfer preview cannot replay Windows writes: " + mode
            );
        }

        await using (var f = await Fixture.Create(root, "cancel"))
        {
            var preview = f.Transfer.Preview();
            var result = await f.Transfer.CommitAsync(preview.PreviewId, false);
            check(
                result.Outcome == Outcome.CancelledBeforeDispatch && f.Unchanged(),
                "declined transfer creates no backup or startup changes"
            );
        }
        foreach (
            string gate in new[]
            {
                "source",
                "target",
                "owner",
                "revision",
                "entry",
                "task",
                "account",
            }
        )
        {
            await using var f = await Fixture.Create(root, "gate-" + gate);
            var preview = f.Transfer.Preview();
            switch (gate)
            {
                case "source":
                    File.AppendAllText(f.PreviousHost, "changed");
                    break;
                case "target":
                    File.AppendAllText(f.CurrentHost, "changed");
                    break;
                case "owner":
                    f.Owner.HeldValue = false;
                    break;
                case "revision":
                    await f.Manager.RefreshAsync();
                    break;
                case "entry":
                    f.Adapter.Entry += " --foreign";
                    break;
                case "task":
                    f.Adapter.Task = TaskFor(f.Startup.Command) with { Source = "Foreign" };
                    break;
                case "account":
                    f.Adapter.SidValue += "-999";
                    break;
            }
            string? entry = f.Adapter.Entry;
            var task = f.Adapter.Task;
            var result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            check(
                !result.Succeeded
                    && f.Adapter.Writes == 0
                    && f.Adapter.Entry == entry
                    && f.Adapter.Task == task
                    && !Directory.Exists(f.Backups),
                "review binds executable hashes, owner, revision and exact registration: " + gate
            );
        }
        await using (var f = await Fixture.Create(root, "foreign-product"))
        {
            f.ProductVerified = false;
            check(
                !f.Transfer.Preview().CanTransfer && f.Unchanged(),
                "a foreign executable never authorizes startup transfer"
            );
        }
        await using (var f = await Fixture.Create(root, "unexpected-arguments"))
        {
            f.Adapter.Entry += " --extra";
            check(
                !f.Transfer.Preview().CanTransfer
                    && f.Adapter.Writes == 0
                    && !Directory.Exists(f.Backups),
                "additional source command arguments cannot authorize transfer"
            );
        }
        await using (var f = await Fixture.Create(root, "disabled-startup", enabled: false))
        {
            var preview = f.Transfer.Preview();
            check(
                !preview.CanTransfer
                    && preview.Reason == ReasonCode.StartupInactive
                    && f.Unchanged(),
                "disabled startup never authorizes migration effects"
            );
        }
        await using (var f = await Fixture.Create(root, "foreign-task-preview"))
        {
            f.Adapter.Task = TaskFor(f.Startup.Command) with
            {
                ParseIssue = StartupTaskParseIssue.TriggerAccountUnresolved,
            };
            check(
                !f.Transfer.Preview().CanTransfer
                    && f.Adapter.Writes == 0
                    && !Directory.Exists(f.Backups),
                "an unresolved task account is not admitted by transfer preview"
            );
        }
        await using (var f = await Fixture.Create(root, "backup-full"))
        {
            var preview = f.Transfer.Preview();
            Directory.CreateDirectory(f.Backups);
            for (int i = 0; i < 32; i++)
                File.WriteAllText(Path.Combine(f.Backups, i + ".json"), "preserved");
            var result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            check(
                result.Code == ReasonCode.StorageFailure
                    && f.Adapter.Writes == 0
                    && f.Adapter.Entry == f.PreviousEntry
                    && f.Adapter.Task is null
                    && Directory.GetFiles(f.Backups).Length == 32,
                "unavailable durable backup blocks every startup write"
            );
        }
        foreach (bool existingTask in new[] { false, true })
        {
            await using var f = await Fixture.Create(root, "task-create-failure-" + existingTask);
            if (existingTask)
                f.Adapter.Task = TaskFor(f.PreviousEntry) with
                {
                    NonstandardWorkingDirectory = "preserved old directory",
                };
            var originalTask = f.Adapter.Task;
            var preview = f.Transfer.Preview();
            f.Adapter.FailTaskCreation = true;
            var result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            check(
                result.Outcome == Outcome.Failed
                    && f.Adapter.Entry == f.PreviousEntry
                    && f.Adapter.Task == originalTask
                    && Directory.GetFiles(f.Backups).Length == 1
                    && f.Preserved(),
                "failed task creation restores exact reviewed Run/task state and retains its backup: "
                    + existingTask
            );
        }
        await using (var f = await Fixture.Create(root, "concurrent-run"))
        {
            var preview = f.Transfer.Preview();
            string foreign = f.PreviousEntry + " --replaced";
            f.Adapter.BeforeEntryChange = () =>
            {
                f.Adapter.Entry = foreign;
                f.Adapter.BeforeEntryChange = null;
            };
            var result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            check(
                result.Outcome == Outcome.Unknown
                    && f.Adapter.Entry == foreign
                    && f.Adapter.Task is null
                    && f.Adapter.Writes == 0,
                "a Run replacement at removal CAS is preserved without creating a task"
            );
        }
        await using (var f = await Fixture.Create(root, "concurrent-task"))
        {
            var preview = f.Transfer.Preview();
            var foreign = TaskFor(f.Startup.Command) with { Source = "Concurrent replacement" };
            f.Adapter.BeforeEntryChange = () =>
            {
                f.Adapter.Task = foreign;
                f.Adapter.BeforeEntryChange = null;
            };
            var result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            check(
                result.Outcome == Outcome.Partial
                    && f.Adapter.Task == foreign
                    && f.Adapter.Entry is null
                    && f.Adapter.TaskWrites == 0
                    && Directory.GetFiles(f.Backups).Length == 1,
                "rollback never deletes or replaces a task installed concurrently after Run removal"
            );
        }
        foreach (bool loseOwner in new[] { false, true })
        {
            await using var f = await Fixture.Create(root, "backup-race-" + loseOwner);
            var preview = f.Transfer.Preview();
            byte[]? changed = null;
            f.Adapter.AfterTaskRead = () =>
            {
                if (!Directory.Exists(f.Backups))
                    return;
                f.Adapter.AfterTaskRead = null;
                if (loseOwner)
                    f.Owner.HeldValue = false;
                else
                {
                    changed = JsonSerializer.SerializeToUtf8Bytes(
                        f.Document with
                        {
                            Settings = f.Document.Settings with
                            {
                                StartInTray = !f.Document.Settings.StartInTray,
                            },
                        },
                        ManagerJson.Default.SettingsDocument
                    );
                    File.WriteAllBytes(f.SettingsPath, changed);
                }
            };
            var result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            check(
                result.Code
                    == (
                        loseOwner
                            ? ReasonCode.EffectOwnerUnavailable
                            : ReasonCode.ConcurrentSettingsChange
                    )
                    && f.Adapter.Writes == 0
                    && f.Adapter.Entry == f.PreviousEntry
                    && f.Adapter.Task is null
                    && (
                        changed is null
                        || File.ReadAllBytes(f.SettingsPath).AsSpan().SequenceEqual(changed)
                    ),
                "post-backup native observation cannot authorize effects after owner or settings change: "
                    + loseOwner
            );
        }
        await using (var f = await Fixture.Create(root, "post-transfer-settings-lock"))
        {
            var preview = f.Transfer.Preview();
            FileStream? locked = null;
            OperationResult result;
            try
            {
                f.Adapter.BeforeEntryChange = () =>
                {
                    f.Adapter.BeforeEntryChange = null;
                    locked = new FileStream(
                        f.SettingsPath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None
                    );
                };
                result = await f.Transfer.CommitAsync(preview.PreviewId, true);
            }
            finally
            {
                locked?.Dispose();
            }
            check(
                result.Outcome == Outcome.Partial
                    && result.Code == ReasonCode.StorageFailure
                    && f.Startup.RegistrationStatus(true, true) == ReasonCode.None
                    && Directory.GetFiles(f.Backups).Length == 1
                    && f.Preserved(),
                "a post-transfer settings read failure reports partial completion and preserves the applied registration"
            );
        }
    }

    static StartupTaskRegistration TaskFor(string command) =>
        new(
            command,
            Sid,
            true,
            true,
            true,
            true,
            UserStartupService.TaskSource,
            new string('a', 64)
        );

    sealed class Fixture : IAsyncDisposable
    {
        internal required string CurrentHost,
            PreviousHost,
            PreviousEntry,
            SettingsPath,
            Backups,
            Script;
        internal required SettingsDocument Document;
        internal required ManagerSettings LoadedSettings;
        internal required byte[] SettingsBytes,
            ScriptBytes;
        internal required ManagerService Manager;
        internal required UserStartupService Startup;
        internal required StartupTransferService Transfer;
        internal required Adapter Adapter;
        internal required Owner Owner;
        internal required Processes Processes;
        internal bool ProductVerified = true;

        internal static async Task<Fixture> Create(
            string root,
            string name,
            bool elevated = true,
            bool enabled = true
        )
        {
            string directory = Path.Combine(root, "startup-transfer-" + name);
            string previous = Path.Combine(directory, "previous", "Bezoro.AhkManager.exe");
            string current = Path.Combine(directory, "current", "Bezoro.AhkManager.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.WriteAllText(previous, "Inert previous product; never execute.");
            File.WriteAllText(current, "Inert current product; never execute.");
            string script = Path.Combine(directory, "external.ahk");
            File.WriteAllText(script, "; preserved external script; never execute");
            string profile = Path.Combine(directory, "profile");
            string settingsPath = Path.Combine(profile, "settings.json");
            var document = new SettingsDocument
            {
                Scripts =
                [
                    new()
                    {
                        Path = script,
                        FriendlyName = "Preserved external script",
                        RunAtLogin = false,
                    },
                ],
                Settings = new()
                {
                    InterpreterPath = current,
                    LaunchManagerAtLogin = enabled,
                    LaunchManagerAsAdministrator = elevated,
                    EditorArguments = ["preserved argument"],
                },
            };
            var store = new SettingsStore(settingsPath);
            await store.LoadAsync();
            if (!(await store.SaveAsync(document)).Succeeded)
                throw new InvalidOperationException("Cannot seed transfer profile.");
            var adapter = new Adapter();
            var owner = new Owner();
            var processes = new Processes();
            var startup = new UserStartupService(adapter, current);
            string oldEntry = new UserStartupService(adapter, previous).Command;
            adapter.Entry = oldEntry;
            var manager = new ManagerService(profile, owner, processes, startup);
            await manager.LoadAsync();
            Fixture? fixture = null;
            var transfer = new StartupTransferService(
                adapter,
                current,
                () => owner.Held && owner.Trusted,
                () => manager.Snapshot,
                _ => fixture!.ProductVerified,
                manager.TransferStartupAsync
            );
            fixture = new Fixture
            {
                CurrentHost = current,
                PreviousHost = previous,
                PreviousEntry = oldEntry,
                SettingsPath = settingsPath,
                Backups = Path.Combine(profile, StartupRepairBackupStore.DirectoryName),
                Script = script,
                Document = document,
                LoadedSettings = manager.Snapshot.Settings,
                SettingsBytes = File.ReadAllBytes(settingsPath),
                ScriptBytes = File.ReadAllBytes(script),
                Manager = manager,
                Startup = startup,
                Transfer = transfer,
                Adapter = adapter,
                Owner = owner,
                Processes = processes,
            };
            return fixture;
        }

        internal bool Preserved() =>
            File.ReadAllBytes(SettingsPath).AsSpan().SequenceEqual(SettingsBytes)
            && File.ReadAllBytes(Script).AsSpan().SequenceEqual(ScriptBytes)
            && Manager.Snapshot.Settings == LoadedSettings
            && Manager.Snapshot.Scripts.Single().Path == Script
            && Processes.Effects == 0;

        internal bool Unchanged() =>
            Adapter.Writes == 0
            && Adapter.Entry == PreviousEntry
            && Adapter.Task is null
            && !Directory.Exists(Backups)
            && Preserved();

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    sealed class Adapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        internal string SidValue = Sid;
        internal string? Entry;
        internal StartupTaskRegistration? Task;
        internal int Writes,
            TaskWrites;
        internal bool FailTaskCreation;
        internal Action? BeforeEntryChange,
            AfterTaskRead;
        public string UserSid => SidValue;

        public string? ReadEntry() => Entry;

        public StartupTaskRegistration? ReadTask()
        {
            var task = Task;
            AfterTaskRead?.Invoke();
            return task;
        }

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            if (Entry != expected)
                return false;
            BeforeEntryChange?.Invoke();
            if (Entry != expected)
                return false;
            Entry = replacement;
            Writes++;
            return true;
        }

        public bool CompareExchangeTask(
            StartupTaskRegistration? expected,
            StartupTaskRegistration? replacement
        )
        {
            if (Task != expected)
                return false;
            if (replacement is not null && FailTaskCreation)
            {
                FailTaskCreation = false;
                throw new IOException("Injected task creation failure.");
            }
            Task = replacement is null
                ? null
                : replacement with
                {
                    Fingerprint = string.IsNullOrEmpty(replacement.Fingerprint)
                        ? new string('b', 64)
                        : replacement.Fingerprint,
                };
            Writes++;
            TaskWrites++;
            return true;
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new StartupInspection(true, [], []));
    }

    sealed class Owner : IEffectOwner
    {
        internal bool HeldValue = true;
        public bool Held => HeldValue;
        public bool Trusted => HeldValue;
        public string Sid => StartupTransferChecks.Sid;
        public string LoginKey => Sid + "|transfer-fixture";

        public bool Allows(string script, string interpreter) => true;

        public void Dispose() { }
    }

    sealed class Processes : IProcessManager
    {
        internal int Effects;
        public bool IsAdministrator => true;
        public event EventHandler? Invalidated
        {
            add { }
            remove { }
        }

        public bool Supports(ScriptOperation operation) => true;

        public Task<ObservedProcesses> ObserveAsync(
            IReadOnlyCollection<string> knownInterpreters,
            CancellationToken ct = default
        ) => Task.FromResult(new ObservedProcesses([], true, []));

        public Task<LaunchResult> StartAsync(
            string interpreter,
            string script,
            bool manuallyElevated,
            CancellationToken ct = default
        )
        {
            Effects++;
            throw new InvalidOperationException("Startup transfer must never start scripts.");
        }

        public Task<OperationResult> ControlAsync(
            ProcessIdentity expected,
            ScriptOperation operation,
            CancellationToken ct = default
        )
        {
            Effects++;
            throw new InvalidOperationException("Startup transfer must never control scripts.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
