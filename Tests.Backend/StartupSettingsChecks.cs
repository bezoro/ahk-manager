using System.Collections.Immutable;
using System.Text.Json;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupSettingsChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string data = Path.Combine(root, "startup-settings-transaction");
        var store = new SettingsStore(Path.Combine(data, "settings.json"));
        var seed = await store.LoadAsync();
        await store.SaveAsync(
            seed.Document with
            {
                Settings = new()
                {
                    LaunchManagerAtLogin = false,
                    LaunchManagerAsAdministrator = false,
                },
            }
        );
        var adapter = new Adapter();
        var processes = new Processes();
        await using var manager = new ManagerService(
            data,
            new Owner(),
            processes,
            new UserStartupService(adapter, Path.Combine(root, "Manager.exe"))
        );
        await manager.LoadAsync();
        var normal = manager.Snapshot.Settings with { LaunchManagerAtLogin = true };
        adapter.FailNextWrite = true;
        var failed = await manager.SaveSettingsAsync(normal, true);
        check(
            failed.Outcome == Outcome.Partial
                && manager.Snapshot.Settings.LaunchManagerAtLogin
                && adapter.Entry is null,
            "startup write failure preserves explicit intent and reports partial instead of confirmed registration"
        );
        check(
            (await manager.SaveSettingsAsync(normal)).Succeeded && adapter.Entry is not null,
            "saving unchanged enabled intent repairs an earlier failed Windows registration"
        );
        adapter.Entry = null;
        check(
            (await manager.SaveSettingsAsync(normal)).Succeeded && adapter.Entry is not null,
            "saving unchanged enabled intent repairs externally missing registration"
        );
        var administrative = normal with { LaunchManagerAsAdministrator = true };
        check(
            (await manager.SaveSettingsAsync(administrative)).Code
                == ReasonCode.StartupConsentRequired
                && adapter.Task is null,
            "changing registered privilege mode needs consent before persistence or effects"
        );
        processes.Administrator = false;
        var pending = await manager.SaveSettingsAsync(administrative, true);
        check(
            pending.Outcome == Outcome.Partial
                && pending.Code == ReasonCode.ManualElevationRequired
                && manager.Snapshot.Settings.LaunchManagerAsAdministrator
                && adapter.Task is null,
            "normal manager saves administrative intent and explicitly requests Windows permission"
        );
        processes.Administrator = true;
        check(
            (await manager.SaveSettingsAsync(manager.Snapshot.Settings, true)).Succeeded
                && adapter.Task is { HighestPrivileges: true }
                && adapter.Entry is null,
            "authenticated administrative replacement applies saved intent through one logon task"
        );
        check(
            (
                await manager.SaveSettingsAsync(
                    administrative with
                    {
                        LaunchManagerAtLogin = false,
                    }
                )
            ).Succeeded
                && adapter.Task is null
                && adapter.Entry is null,
            "disabling manager startup removes the owned elevated registration"
        );

        var launch = ManagerElevation.CommittedHostStartInfo(
            Path.Combine(root, "Manager.exe"),
            resumeLogin: true
        );
        check(
            launch.ArgumentList.SequenceEqual(["--login", "--background"]),
            "authenticated automatic elevation preserves login intent without making a foreground-only replacement"
        );
        bool refused = false;
        try
        {
            _ = ManagerElevation.CommittedHostStartInfo(
                Path.Combine(root, "Manager.exe"),
                "descriptor",
                true
            );
        }
        catch (ProtocolException)
        {
            refused = true;
        }
        check(refused, "a scoped continuation cannot also request the general login loop");
        var scoped = ManagerElevation.CommittedHostStartInfo(
            Path.Combine(root, "Manager.exe"),
            "descriptor",
            backgroundStartup: true
        );
        check(
            scoped.ArgumentList.SequenceEqual(["--background", "--startup-adoption=descriptor"]),
            "quiet scoped replacement preserves presentation without general login or foreground activation"
        );
        var manual = ManagerElevation.CommittedHostStartInfo(
            Path.Combine(root, "Manager.exe"),
            "descriptor"
        );
        check(
            manual.ArgumentList.SequenceEqual(["--foreground", "--startup-adoption=descriptor"]),
            "manual scoped replacement retains explicit foreground intent"
        );
        refused = false;
        try
        {
            _ = ManagerElevation.CommittedHostStartInfo(
                Path.Combine(root, "Manager.exe"),
                backgroundStartup: true
            );
        }
        catch (ProtocolException)
        {
            refused = true;
        }
        check(refused, "background presentation alone never grants a startup continuation");
        var legacy = JsonSerializer.Deserialize(
            """
            {"operationId":"11111111-1111-1111-1111-111111111111","continuationId":"22222222-2222-2222-2222-222222222222"}
            """,
            Protocol.Json.StartupElevationPayload
        );
        check(
            legacy is { BackgroundStartup: false },
            "existing continuation payloads default to manual foreground presentation"
        );
        var background = legacy! with { BackgroundStartup = true };
        check(
            JsonSerializer.Deserialize(
                JsonSerializer.Serialize(background, Protocol.Json.StartupElevationPayload),
                Protocol.Json.StartupElevationPayload
            ) == background,
            "quiet continuation presentation survives only the authenticated request wire"
        );
        var legacyFrame = JsonSerializer.Deserialize(
            """
            {"kind":"Commit","operationId":"11111111-1111-1111-1111-111111111111","nonce":"fixture"}
            """,
            Protocol.Json.ElevationFrame
        );
        check(
            legacyFrame is { BackgroundStartup: false },
            "existing private commit frames retain foreground presentation"
        );
        var backgroundFrame = legacyFrame! with { BackgroundStartup = true };
        check(
            JsonSerializer.Deserialize(
                JsonSerializer.Serialize(backgroundFrame, Protocol.Json.ElevationFrame),
                Protocol.Json.ElevationFrame
            ) == backgroundFrame,
            "quiet presentation is bound to the authenticated bootstrap commit frame"
        );
        foreach (bool quietStartup in new[] { false, true })
        foreach (int result in new[] { 0, 75 })
        foreach (bool normalExited in new[] { false, true })
        foreach (bool hostExited in new[] { false, true })
        {
            int notifications = 0;
            ElevationActivation.ReportActivationFailure(
                result,
                normalExited,
                hostExited,
                quietStartup,
                () => notifications++
            );
            check(
                notifications
                    == (result != 0 && normalExited && hostExited && !quietStartup ? 1 : 0),
                "bootstrap recovery remains observable without Manager dialogs during quiet login"
            );
        }
        check(processes.Effects == 0, "startup preference setup never starts or stops scripts");
    }

    sealed class Owner : IEffectOwner
    {
        public bool Held => true;
        public bool Trusted => true;
        public string Sid => "S-1-startup-settings-fixture";
        public string LoginKey => Sid + "|login";

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
        public bool Administrator = true;
        public bool IsAdministrator => Administrator;
        public int Effects;

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
            throw new InvalidOperationException("No process effects in settings fixtures.");
        }

        public Task<OperationResult> ControlAsync(
            ProcessIdentity expected,
            ScriptOperation operation,
            CancellationToken ct = default
        )
        {
            Effects++;
            throw new InvalidOperationException("No process effects in settings fixtures.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Adapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string UserSid => "S-1-startup-settings-fixture";
        public string? Entry;
        public StartupTaskRegistration? Task;
        public bool FailNextWrite;

        public string? ReadEntry() => Entry;

        public StartupTaskRegistration? ReadTask() => Task;

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Injected registration failure.");
            }
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
