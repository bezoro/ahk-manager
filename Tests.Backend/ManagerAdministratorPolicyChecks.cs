using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class ManagerAdministratorPolicyChecks
{
    const string Sid = "S-1-5-21-100-200-300-1001";

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        foreach (bool login in new[] { false, true })
            await SettingsPolicy(root, login, check);
        await RecoveryPolicy(root, check);
        await StartupPolicy(root, check);
    }

    static SettingsDocument Legacy(string root, bool login) =>
        new()
        {
            Settings = new()
            {
                LaunchManagerAtLogin = login,
                LaunchManagerAsAdministrator = false,
                StartInTray = true,
                ShowWindowAtLogin = true,
                EditorPath = Path.Combine(root, "editor.exe"),
                EditorArguments = ["--reuse-window"],
                Theme = AppThemePreference.Dark,
            },
            Scripts =
            [
                new()
                {
                    Path = Path.Combine(root, "external-standard.ahk"),
                    FriendlyName = "Standard",
                    RunAsAdministrator = false,
                },
                new()
                {
                    Path = Path.Combine(root, "external-admin.ahk"),
                    FriendlyName = "Elevated",
                    RunAsAdministrator = true,
                },
            ],
            LastLoginKey = "preserved-login",
        };

    static byte[] Bytes(SettingsDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, ManagerJson.Default.SettingsDocument);

    static async Task SettingsPolicy(string root, bool login, Action<bool, string> check)
    {
        string directory = Path.Combine(
            root,
            "mandatory-manager-admin",
            login ? "enabled" : "disabled"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        var original = Legacy(root, login);
        byte[] originalBytes = Bytes(original);
        await File.WriteAllBytesAsync(path, originalBytes);
        var generic = await new SettingsStore(path).LoadAsync();
        check(
            !generic.Document.Settings.LaunchManagerAsAdministrator,
            "generic settings hosts retain their explicit non-administrator preference"
        );
        var store = new SettingsStore(path, managerRequiresAdministrator: true);
        var loaded = await store.LoadAsync();
        check(
            !loaded.ReadOnly
                && loaded.Document.Settings.LaunchManagerAsAdministrator
                && loaded.Document.Settings.LaunchManagerAtLogin == login
                && loaded.Document.Settings.EditorPath == original.Settings.EditorPath
                && loaded.Document.Settings.EditorArguments.SequenceEqual(
                    original.Settings.EditorArguments
                )
                && loaded.Document.Settings.Theme == original.Settings.Theme
                && loaded.Document.Settings.StartInTray == original.Settings.StartInTray
                && loaded.Document.Settings.ShowWindowAtLogin == original.Settings.ShowWindowAtLogin
                && loaded.Document.LastLoginKey == original.LastLoginKey
                && loaded
                    .Document.Scripts.Select(s => s.RunAsAdministrator)
                    .SequenceEqual([false, true]),
            "mandatory host normalizes legacy elevation while preserving login and individual script preferences"
        );
        check(
            await store.VerifyLoadedAsync()
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(originalBytes),
            "effective administrator normalization retains the exact raw-file concurrency fingerprint without a load-time write"
        );
        var saved = await store.SaveAsync(
            loaded.Document with
            {
                Settings = loaded.Document.Settings with { LaunchManagerAsAdministrator = false },
            }
        );
        var persisted = await new SettingsStore(path).LoadAsync();
        check(
            saved.Succeeded
                && persisted.Document.Settings.LaunchManagerAsAdministrator
                && persisted.Document.Settings.LaunchManagerAtLogin == login
                && File.ReadAllBytes(path + ".bak").AsSpan().SequenceEqual(originalBytes),
            "mandatory host cannot persist a requested normal-token mode and preserves the previous raw profile backup"
        );
        byte[] concurrent = Bytes(original with { LastLoginKey = "changed-by-another-writer" });
        await File.WriteAllBytesAsync(path, concurrent);
        check(
            !await store.VerifyLoadedAsync()
                && (await store.SaveAsync(loaded.Document)).Code
                    == ReasonCode.ConcurrentSettingsChange
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(concurrent),
            "mandatory elevation never weakens compare-and-replace protection against concurrent profile writes"
        );
    }

    static async Task RecoveryPolicy(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "mandatory-manager-admin", "recovery");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        byte[] backup = Bytes(Legacy(root, false));
        await File.WriteAllBytesAsync(path + ".bak", backup);
        await File.WriteAllTextAsync(path, "{broken-profile");
        var store = new SettingsStore(path, managerRequiresAdministrator: true);
        var recovered = await store.LoadAsync();
        check(
            recovered.ReadOnly
                && recovered.Document.Settings.LaunchManagerAsAdministrator
                && !recovered.Document.Settings.LaunchManagerAtLogin
                && (await store.SaveAsync(recovered.Document)).Code == ReasonCode.ReadOnlySettings
                && File.ReadAllText(path) == "{broken-profile"
                && File.ReadAllBytes(path + ".bak").AsSpan().SequenceEqual(backup),
            "backup recovery applies mandatory authority without making damaged profiles writable or enabling login"
        );
        var restored = await store.RestoreBackupAsync();
        recovered = await store.LoadAsync();
        check(
            restored.Succeeded
                && !recovered.ReadOnly
                && recovered.Document.Settings.LaunchManagerAsAdministrator
                && !recovered.Document.Settings.LaunchManagerAtLogin
                && await store.VerifyLoadedAsync()
                && Directory.GetFiles(directory, "settings.json.recovery-*").Length == 1,
            "explicit backup restoration keeps mandatory authority and preserves the damaged primary separately"
        );
    }

    static async Task StartupPolicy(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "mandatory-manager-admin", "startup");
        Directory.CreateDirectory(directory);
        var legacy = Legacy(root, true);
        await File.WriteAllBytesAsync(Path.Combine(directory, "settings.json"), Bytes(legacy));
        var adapter = new Adapter();
        var startup = new UserStartupService(adapter, Path.Combine(root, "Bezoro.AhkManager.exe"));
        adapter.Entry = startup.Command;
        var processes = new Processes();
        await using var manager = new ManagerService(
            directory,
            new Owner(),
            processes,
            startup,
            managerRequiresAdministrator: true
        );
        await manager.LoadAsync();
        check(
            manager.Snapshot.Settings.LaunchManagerAsAdministrator
                && manager.Snapshot.Settings.LaunchManagerAtLogin
                && adapter.Task is null
                && adapter.Entry == startup.Command,
            "mandatory Manager host recognizes a legacy normal Run registration without applying startup effects during profile load"
        );
        var migrated = await manager.SaveSettingsAsync(
            manager.Snapshot.Settings with
            {
                LaunchManagerAsAdministrator = false,
            }
        );
        check(
            migrated.Succeeded
                && adapter.Entry is null
                && adapter.Task
                    is {
                        HighestPrivileges: true,
                        InteractiveToken: true,
                        UserLogonOnly: true,
                        Enabled: true
                    }
                && adapter.Task.UserSid == Sid
                && adapter.Task.Command == startup.Command
                && adapter.Task.Source == UserStartupService.TaskSource
                && manager.Snapshot.Settings.LaunchManagerAsAdministrator,
            "a forged legacy false save migrates an owned normal Run entry to one highest-privilege task rather than restoring a normal launch"
        );
        var disabled = await manager.SaveSettingsAsync(
            manager.Snapshot.Settings with
            {
                LaunchManagerAtLogin = false,
                LaunchManagerAsAdministrator = false,
            }
        );
        check(
            disabled.Succeeded
                && !manager.Snapshot.Settings.LaunchManagerAtLogin
                && manager.Snapshot.Settings.LaunchManagerAsAdministrator
                && adapter.Task is null
                && adapter.Entry is null
                && manager
                    .Snapshot.Scripts.Select(s => s.RunAsAdministrator)
                    .SequenceEqual([false, true])
                && processes.Effects == 0,
            "mandatory Manager authority preserves startup-off and independent per-script authority without starting or stopping scripts"
        );
        var stored = await new SettingsStore(Path.Combine(directory, "settings.json")).LoadAsync();
        check(
            stored.Document.Settings.LaunchManagerAsAdministrator
                && !stored.Document.Settings.LaunchManagerAtLogin
                && stored
                    .Document.Scripts.Select(s => s.RunAsAdministrator)
                    .SequenceEqual([false, true]),
            "the durable production profile agrees with effective Manager authority and retains script flags"
        );
    }

    sealed class Owner : IEffectOwner
    {
        public bool Held => true;
        public bool Trusted => true;
        public string Sid => ManagerAdministratorPolicyChecks.Sid;
        public string LoginKey => Sid + "|policy";

        public bool Allows(string script, string interpreter) => true;

        public void Dispose() { }
    }

    sealed class Processes : IProcessManager
    {
        public event EventHandler? Invalidated
        {
            add { }
            remove { }
        }
        public bool IsAdministrator => true;
        internal int Effects;

        public bool Supports(ScriptOperation operation) => true;

        public Task<ObservedProcesses> ObserveAsync(
            IReadOnlyCollection<string> interpreters,
            CancellationToken ct = default
        ) => Task.FromResult(new ObservedProcesses([], true, []));

        public Task<LaunchResult> StartAsync(
            string interpreter,
            string script,
            bool elevated,
            CancellationToken ct = default
        )
        {
            Effects++;
            throw new InvalidOperationException(
                "No script process may be launched by this fixture."
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
                "No script process may be controlled by this fixture."
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Adapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string UserSid => Sid;
        internal string? Entry;
        internal StartupTaskRegistration? Task;

        public string? ReadEntry() => Entry;

        public StartupTaskRegistration? ReadTask() => Task;

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            if (Entry != expected)
                return false;
            Entry = replacement;
            return true;
        }

        public bool CompareExchangeTask(
            StartupTaskRegistration? expected,
            StartupTaskRegistration? replacement
        )
        {
            if (Task != expected)
                return false;
            Task = replacement;
            return true;
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new StartupInspection(true, [], []));
    }
}
