using System.Collections.Immutable;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupRepairManagerChecks
{
    const string Sid = "S-1-5-21-100-200-300-1001";

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        await CheckConfirmedRepair(root, check);
        await CheckRefusalGates(root, check);
        await CheckUnavailableBackup(root, check);
        await CheckSavedModes(root, check);
        await CheckConcurrentSettings(root, check);
    }

    static async Task CheckConfirmedRepair(string root, Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync(root, "confirmed");
        check(
            fixture.Manager.Snapshot.Issues.Any(issue =>
                issue.Code == ReasonCode.StartupEntryConflict
            )
                && fixture.Manager.Snapshot.RunningCount == 3,
            "manager repair fixture reproduces a valid current task plus a stale Run entry and three external running scripts"
        );
        var preview = await fixture.Manager.PreviewStartupRepairAsync();
        check(
            preview.CanRepair
                && preview.Fingerprint is { Length: 64 }
                && preview.PreviousExecutable == fixture.PreviousHost
                && preview.CurrentExecutable == fixture.CurrentHost
                && fixture.Adapter.Writes == 0
                && !Directory.Exists(fixture.BackupDirectory),
            "manager repair preview identifies the reviewed old executable without startup writes or backups"
        );

        bool readableBeforeDeletion = false;
        string? backupPath = null;
        fixture.Adapter.BeforeRunRemoval = () =>
        {
            backupPath = Directory.GetFiles(fixture.BackupDirectory, "*.json").Single();
            // FileShare.None in the durable writer also makes this read prove
            // that backup writing and its flush/disposal completed first.
            using var backup = JsonDocument.Parse(File.ReadAllBytes(backupPath));
            var value = backup.RootElement;
            readableBeforeDeletion =
                value.GetProperty("schemaVersion").GetInt32() == 1
                && value.GetProperty("applicationId").GetString() == "Bezoro.AhkManager.Electron"
                && value.GetProperty("kind").GetString() == "startup-repair-backup"
                && value.GetProperty("userSid").GetString() == Sid
                && value.GetProperty("registryKey").GetString()
                    == @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
                && value.GetProperty("registryValueName").GetString() == "Bezoro.AhkManager.WinUI"
                && value.GetProperty("previousRunEntry").GetString() == fixture.PreviousEntry
                && value.GetProperty("currentCommand").GetString() == fixture.Startup.Command
                && value.GetProperty("taskFingerprint").GetString()
                    == fixture.OriginalTask.Fingerprint
                && value.GetProperty("launchManagerAtLogin").GetBoolean()
                && value.GetProperty("launchManagerAsAdministrator").GetBoolean();
            if (!readableBeforeDeletion)
                throw new InvalidDataException("The expected startup backup was not readable.");
        };
        var repaired = await fixture.Manager.RepairStartupAsync(preview.Fingerprint!, true);
        check(
            repaired.Succeeded
                && repaired.Code == ReasonCode.None
                && readableBeforeDeletion
                && backupPath is not null
                && File.Exists(backupPath)
                && repaired.Detail?.Contains(Path.GetFileName(backupPath)) == true
                && fixture.Adapter.RemovedRunEntries.SequenceEqual([fixture.PreviousEntry]),
            "manager repair durably backs up the exact selected Run entry before removing it and reports its recovery file"
        );
        check(
            fixture.Adapter.Entry is null
                && fixture.Adapter.Task == fixture.OriginalTask
                && fixture.Adapter.TaskWrites == 0
                && !fixture.Manager.Snapshot.Issues.Any(issue =>
                    issue.Code is ReasonCode.StartupEntryConflict or ReasonCode.StartupInactive
                ),
            "saved elevated startup keeps the canonical current task unchanged and clears the manager startup issue"
        );
        check(
            fixture.Preserved()
                && fixture.Manager.Snapshot.RunningCount == 3
                && fixture.Processes.Effects == 0,
            "startup repair preserves saved preference bytes catalog metadata external script bytes and independent running processes"
        );
    }

    static async Task CheckRefusalGates(string root, Action<bool, string> check)
    {
        await using (var fixture = await Fixture.CreateAsync(root, "unconfirmed"))
        {
            var preview = await fixture.Manager.PreviewStartupRepairAsync();
            var refused = await fixture.Manager.RepairStartupAsync(preview.Fingerprint!, false);
            check(
                refused.Code == ReasonCode.StartupConsentRequired && fixture.Unchanged(),
                "manager repair requires explicit reviewed consent without startup effects or backups"
            );
        }
        foreach (
            var gate in new[]
            {
                (Name: "not-loaded", Reason: ReasonCode.CapabilityUnverified),
                (Name: "owner-not-held", Reason: ReasonCode.EffectOwnerUnavailable),
                (Name: "owner-untrusted", Reason: ReasonCode.EffectOwnerUnavailable),
                (Name: "normal-authority", Reason: ReasonCode.ManualElevationRequired),
                (Name: "read-only", Reason: ReasonCode.ReadOnlySettings),
            }
        )
        {
            await using var fixture = await Fixture.CreateAsync(root, gate.Name, load: false);
            if (gate.Name == "owner-not-held")
                fixture.Owner.HeldValue = false;
            if (gate.Name == "owner-untrusted")
                fixture.Owner.TrustedValue = false;
            if (gate.Name == "normal-authority")
                fixture.Processes.Administrator = false;
            if (gate.Name == "read-only")
                fixture.MakeSettingsReadOnly();
            if (gate.Name != "not-loaded")
                await fixture.Manager.LoadAsync();
            var preview = await fixture.Manager.PreviewStartupRepairAsync();
            var repaired = await fixture.Manager.RepairStartupAsync(
                fixture.RepairFingerprint,
                true
            );
            check(
                !preview.CanRepair
                    && preview.Reason == gate.Reason
                    && repaired.Code == gate.Reason
                    && fixture.Unchanged(),
                "manager repair refuses unavailable profile or authority without startup effects or backups: "
                    + gate.Name
            );
        }
        await using (var fixture = await Fixture.CreateAsync(root, "cancelled"))
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = await fixture.Manager.RepairStartupAsync(
                fixture.RepairFingerprint,
                true,
                cancellation.Token
            );
            check(
                cancelled.Outcome == Outcome.CancelledBeforeDispatch
                    && cancelled.Code == ReasonCode.Cancelled
                    && fixture.Unchanged(),
                "manager repair cancelled before dispatch leaves startup and the profile unchanged"
            );
        }
    }

    static async Task CheckUnavailableBackup(string root, Action<bool, string> check)
    {
        await using var fixture = await Fixture.CreateAsync(root, "blocked-backup");
        File.WriteAllText(fixture.BackupDirectory, "Unrelated existing file must be preserved.");
        var preview = await fixture.Manager.PreviewStartupRepairAsync();
        var failed = await fixture.Manager.RepairStartupAsync(preview.Fingerprint!, true);
        check(
            failed.Code == ReasonCode.StorageFailure
                && !failed.Succeeded
                && fixture.Adapter.Entry == fixture.PreviousEntry
                && fixture.Adapter.Task == fixture.OriginalTask
                && fixture.Adapter.Writes == 0
                && File.ReadAllText(fixture.BackupDirectory)
                    == "Unrelated existing file must be preserved."
                && fixture.Preserved()
                && fixture.Processes.Effects == 0,
            "a real backup storage failure prevents Run deletion and preserves unrelated profile contents"
        );
    }

    static async Task CheckSavedModes(string root, Action<bool, string> check)
    {
        foreach (bool enabled in new[] { false, true })
        {
            await using var fixture = await Fixture.CreateAsync(
                root,
                enabled ? "saved-standard" : "saved-disabled",
                enabled: enabled,
                elevated: false
            );
            var preview = await fixture.Manager.PreviewStartupRepairAsync();
            var result = await fixture.Manager.RepairStartupAsync(preview.Fingerprint!, true);
            check(
                result.Succeeded
                    && fixture.Adapter.Task is null
                    && fixture.Adapter.Entry == (enabled ? fixture.Startup.Command : null)
                    && fixture.Adapter.TaskWrites == 1
                    && fixture.Adapter.RemovedRunEntries.SequenceEqual([fixture.PreviousEntry])
                    && Directory.GetFiles(fixture.BackupDirectory, "*.json").Length == 1
                    && fixture.Preserved()
                    && fixture.Processes.Effects == 0
                    && !fixture.Manager.Snapshot.Issues.Any(issue =>
                        issue.Code == ReasonCode.StartupEntryConflict
                    ),
                "manager repair reconciles the durable saved standard or disabled startup mode without changing profile intent"
            );
        }
    }

    static async Task CheckConcurrentSettings(string root, Action<bool, string> check)
    {
        await using (var fixture = await Fixture.CreateAsync(root, "changed-before-apply"))
        {
            var before = await fixture.Manager.PreviewStartupRepairAsync();
            byte[] changed = fixture.WriteExternalSettings(false, false);
            var current = await fixture.Manager.PreviewStartupRepairAsync();
            var refused = await fixture.Manager.RepairStartupAsync(before.Fingerprint!, true);
            check(
                !current.CanRepair
                    && current.Reason == ReasonCode.ConcurrentSettingsChange
                    && refused.Code == ReasonCode.ConcurrentSettingsChange
                    && fixture.Adapter.Entry == fixture.PreviousEntry
                    && fixture.Adapter.Task == fixture.OriginalTask
                    && fixture.Adapter.Writes == 0
                    && !Directory.Exists(fixture.BackupDirectory)
                    && File.ReadAllBytes(fixture.SettingsPath).AsSpan().SequenceEqual(changed)
                    && fixture.Processes.Effects == 0,
                "manager repair refuses externally changed saved preferences without overwriting them or changing startup"
            );
        }
        await using (
            var fixture = await Fixture.CreateAsync(
                root,
                "changed-after-removal",
                enabled: false,
                elevated: false
            )
        )
        {
            var preview = await fixture.Manager.PreviewStartupRepairAsync();
            byte[] changed = [];
            fixture.Adapter.BeforeRunRemoval = () =>
                changed = fixture.WriteExternalSettings(true, true);
            var partial = await fixture.Manager.RepairStartupAsync(preview.Fingerprint!, true);
            check(
                partial.Outcome == Outcome.Partial
                    && partial.Code == ReasonCode.ConcurrentSettingsChange
                    && fixture.Adapter.Entry is null
                    && fixture.Adapter.Task == fixture.OriginalTask
                    && fixture.Adapter.TaskWrites == 0
                    && fixture.Adapter.RemovedRunEntries.SequenceEqual([fixture.PreviousEntry])
                    && Directory.GetFiles(fixture.BackupDirectory, "*.json").Length == 1
                    && File.ReadAllBytes(fixture.SettingsPath).AsSpan().SequenceEqual(changed)
                    && fixture.Processes.Effects == 0,
                "a settings change after exact Run removal remains partial and preserves the task instead of applying stale disabled intent"
            );
        }
        await using (
            var fixture = await Fixture.CreateAsync(
                root,
                "changed-during-reconciliation",
                enabled: true,
                elevated: false
            )
        )
        {
            var preview = await fixture.Manager.PreviewStartupRepairAsync();
            byte[] changed = [];
            fixture.Adapter.BeforeRunCreation = () =>
                changed = fixture.WriteExternalSettings(false, false);
            const string concurrentEntry = "A concurrent external startup replacement";
            bool replaced = false;
            fixture.Adapter.AfterTaskRead = () =>
            {
                if (
                    !replaced
                    && fixture.Adapter.Entry == fixture.Startup.Command
                    && fixture.Adapter.Task is null
                )
                {
                    // The final observation has captured the normal-mode task
                    // absence; another writer replaces Run before it returns.
                    fixture.Adapter.Entry = concurrentEntry;
                    replaced = true;
                }
            };
            var partial = await fixture.Manager.RepairStartupAsync(preview.Fingerprint!, true);
            check(
                partial.Outcome == Outcome.Partial
                    && partial.Code == ReasonCode.ConcurrentSettingsChange
                    && changed.Length > 0
                    && replaced
                    && fixture.Adapter.Entry == concurrentEntry
                    && fixture.Adapter.Task is null
                    && fixture.Adapter.TaskWrites == 1
                    && Directory.GetFiles(fixture.BackupDirectory, "*.json").Length == 1
                    && File.ReadAllBytes(fixture.SettingsPath).AsSpan().SequenceEqual(changed)
                    && fixture.Processes.Effects == 0,
                "a settings change during awaited reconciliation prevents confirmed success and preserves concurrent startup entries and backup"
            );
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        internal string Profile { get; private init; } = "";
        internal string SettingsPath => Path.Combine(Profile, "settings.json");
        internal string BackupDirectory =>
            Path.Combine(Profile, StartupRepairBackupStore.DirectoryName);
        internal string CurrentHost { get; private init; } = "";
        internal string PreviousHost { get; private init; } = "";
        internal string PreviousEntry { get; private set; } = "";
        internal string RepairFingerprint { get; private set; } = "";
        internal Owner Owner { get; } = new();
        internal Processes Processes { get; } = new();
        internal Adapter Adapter { get; } = new();
        internal UserStartupService Startup { get; private set; } = null!;
        internal ManagerService Manager { get; private set; } = null!;
        internal StartupTaskRegistration OriginalTask { get; private set; } = null!;
        SettingsDocument original = new();
        ManagerSettings loadedSettings = new();
        byte[] settingsBytes = [];
        readonly Dictionary<string, byte[]> scripts = [];

        internal static async Task<Fixture> CreateAsync(
            string root,
            string name,
            bool load = true,
            bool enabled = true,
            bool elevated = true
        )
        {
            var fixture = new Fixture
            {
                Profile = Path.Combine(root, "manager-startup-repair", name, "Profile"),
                CurrentHost = Path.Combine(root, "current portable", "Bezoro.AhkManager.exe"),
                PreviousHost = Path.Combine(root, "previous publish", "Bezoro.AhkManager.exe"),
            };
            Directory.CreateDirectory(fixture.Profile);
            var collection = new CollectionSnapshot(Guid.NewGuid(), "Preserved scripts");
            string interpreter = Path.Combine(root, "inert interpreter", "AutoHotkey64.exe");
            for (int index = 0; index < 3; index++)
            {
                string file = Path.Combine(
                    root,
                    "external scripts",
                    name,
                    "folder-" + index,
                    "preserved.ahk"
                );
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                    "; External script fixture, never executed.\n#Requires AutoHotkey v2.0\n"
                );
                File.WriteAllBytes(file, bytes);
                fixture.scripts.Add(file, bytes);
                fixture.original.Scripts.Add(
                    new()
                    {
                        Path = file,
                        FriendlyName = "Preserved script " + index,
                        Description = "Keep metadata and external paths.",
                        CollectionId = collection.Id,
                        RunAtLogin = true,
                        RunAsAdministrator = index == 2,
                        InterpreterPath = interpreter,
                        ManualHotkeys = ["Ctrl+Alt+F" + (index + 1)],
                    }
                );
            }
            fixture.original = fixture.original with
            {
                Collections = [collection],
                Settings = new()
                {
                    LaunchManagerAtLogin = enabled,
                    LaunchManagerAsAdministrator = elevated,
                    StartInTray = true,
                    Theme = AppThemePreference.Dark,
                    InterpreterPath = interpreter,
                    EditorPath = Path.Combine(root, "preserved editor.exe"),
                    EditorArguments = ["--reuse-window"],
                },
                LastLoginKey = "preserved-login-marker",
            };
            var store = new SettingsStore(fixture.SettingsPath);
            await store.LoadAsync();
            if (!(await store.SaveAsync(fixture.original)).Succeeded)
                throw new IOException("The isolated repair profile could not be seeded.");
            fixture.settingsBytes = File.ReadAllBytes(fixture.SettingsPath);
            fixture.Startup = new(fixture.Adapter, fixture.CurrentHost);
            fixture.PreviousEntry = new UserStartupService(
                fixture.Adapter,
                fixture.PreviousHost
            ).Command;
            fixture.Adapter.Entry = fixture.PreviousEntry;
            fixture.OriginalTask = WindowsStartupTaskStore.Parse(
                WindowsStartupTaskStore.Xml(
                    new(
                        fixture.Startup.Command,
                        Sid,
                        true,
                        true,
                        true,
                        true,
                        UserStartupService.TaskSource
                    )
                )
            );
            fixture.Adapter.Task = fixture.OriginalTask;
            fixture.RepairFingerprint =
                fixture.Startup.PreviewLegacyRunRepair(enabled, elevated).Fingerprint
                ?? throw new InvalidOperationException(
                    "The isolated stale Run entry must be reviewable."
                );
            fixture.Processes.Current = new(
                fixture
                    .original.Scripts.Select(
                        (script, index) =>
                            new ProcessSnapshot(
                                new(index + 50000, index + 10000, interpreter, script.Path, Sid, 1),
                                ExecutionOrigin.External,
                                "2.0.30",
                                DateTimeOffset.UtcNow
                            )
                    )
                    .ToImmutableArray(),
                true,
                []
            );
            fixture.Manager = new(
                fixture.Profile,
                fixture.Owner,
                fixture.Processes,
                fixture.Startup
            );
            if (load)
            {
                await fixture.Manager.LoadAsync();
                fixture.loadedSettings = fixture.Manager.Snapshot.Settings;
            }
            return fixture;
        }

        internal void MakeSettingsReadOnly()
        {
            File.Copy(SettingsPath, SettingsPath + ".bak");
            File.WriteAllText(SettingsPath, "{invalid profile");
            settingsBytes = File.ReadAllBytes(SettingsPath);
        }

        internal byte[] WriteExternalSettings(bool enabled, bool elevated)
        {
            byte[] changed = JsonSerializer.SerializeToUtf8Bytes(
                original with
                {
                    Settings = original.Settings with
                    {
                        LaunchManagerAtLogin = enabled,
                        LaunchManagerAsAdministrator = elevated,
                    },
                },
                ManagerJson.Default.SettingsDocument
            );
            File.WriteAllBytes(SettingsPath, changed);
            return changed;
        }

        internal bool Preserved() =>
            File.ReadAllBytes(SettingsPath).AsSpan().SequenceEqual(settingsBytes)
            && scripts.All(entry =>
                File.ReadAllBytes(entry.Key).AsSpan().SequenceEqual(entry.Value)
            )
            && Manager
                .Snapshot.Scripts.Select(script => script.Path)
                .SequenceEqual(original.Scripts.Select(script => script.Path))
            && Manager
                .Snapshot.Scripts.Select(script => script.FriendlyName)
                .SequenceEqual(original.Scripts.Select(script => script.FriendlyName))
            && Manager.Snapshot.Collections.SequenceEqual(original.Collections)
            && Manager.Snapshot.Settings == loadedSettings;

        internal bool Unchanged() =>
            Adapter.Entry == PreviousEntry
            && Adapter.Task == OriginalTask
            && Adapter.Writes == 0
            && !Directory.Exists(BackupDirectory)
            && !File.Exists(BackupDirectory)
            && File.ReadAllBytes(SettingsPath).AsSpan().SequenceEqual(settingsBytes)
            && scripts.All(entry =>
                File.ReadAllBytes(entry.Key).AsSpan().SequenceEqual(entry.Value)
            )
            && Processes.Effects == 0;

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    sealed class Owner : IEffectOwner
    {
        internal bool HeldValue = true,
            TrustedValue = true;
        public bool Held => HeldValue;
        public bool Trusted => TrustedValue;
        public string Sid => StartupRepairManagerChecks.Sid;
        public string LoginKey => Sid + "|preserved-owner";

        public bool Allows(string script, string interpreter) => true;

        public void Dispose() { }
    }

    sealed class Adapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string UserSid => Sid;
        internal string? Entry;
        internal StartupTaskRegistration? Task;
        internal Action? BeforeRunRemoval,
            BeforeRunCreation,
            AfterTaskRead;
        internal readonly List<string> RemovedRunEntries = [];
        internal int Writes,
            TaskWrites;

        public string? ReadEntry() => Entry;

        public StartupTaskRegistration? ReadTask()
        {
            var observed = Task;
            AfterTaskRead?.Invoke();
            return observed;
        }

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            if (Entry != expected)
                return false;
            if (expected is not null && replacement is null)
            {
                BeforeRunRemoval?.Invoke();
                RemovedRunEntries.Add(expected);
            }
            if (expected is null && replacement is not null)
                BeforeRunCreation?.Invoke();
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
            Task = replacement is null
                ? null
                : WindowsStartupTaskStore.Parse(WindowsStartupTaskStore.Xml(replacement));
            Writes++;
            TaskWrites++;
            return true;
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new StartupInspection(true, [], []));
    }

    sealed class Processes : IProcessManager
    {
        public event EventHandler? Invalidated
        {
            add { }
            remove { }
        }
        internal bool Administrator = true;
        internal int Effects;
        internal ObservedProcesses Current = new([], true, []);
        public bool IsAdministrator => Administrator;

        public bool Supports(ScriptOperation operation) => true;

        public Task<ObservedProcesses> ObserveAsync(
            IReadOnlyCollection<string> knownInterpreters,
            CancellationToken ct = default
        ) => Task.FromResult(Current);

        public Task<LaunchResult> StartAsync(
            string interpreter,
            string script,
            bool manuallyElevated,
            CancellationToken ct = default
        )
        {
            Effects++;
            throw new InvalidOperationException(
                "Startup repair must never launch script processes."
            );
        }

        public Task<OperationResult> ControlAsync(
            ProcessIdentity expected,
            ScriptOperation operation,
            CancellationToken ct = default
        )
        {
            Effects++;
            throw new InvalidOperationException(
                "Startup repair must never control script processes."
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
