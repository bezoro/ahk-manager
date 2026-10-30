using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class PortableProfileChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string fixture = Path.Combine(root, "portable-profiles");
        Directory.CreateDirectory(fixture);
        Layouts(fixture, check);
        DirectoryOwnership(fixture, check);
        await ImportAsync(fixture, check);
        await ImportRefusalsAsync(fixture, check);
        await DirectoryRequestsAsync(fixture, check);
    }

    static void Layouts(string fixture, Action<bool, string> check)
    {
        var (layout, root) = Create(fixture, "layout");
        check(
            layout.Portable
                && layout.DataRoot == Path.Combine(root, "Data")
                && layout.ProfileRoot == Path.Combine(root, "Data", "Profile")
                && layout.ChromiumRoot == Path.Combine(root, "Data", "Chromium")
                && layout.UpdatesRoot == Path.Combine(root, "Data", "Updates")
                && layout.StartupHostExecutable == Path.Combine(root, "Bezoro.AhkManager.exe")
                && layout.HostExecutable == Path.Combine(root, "App", "Bezoro.AhkManager.exe"),
            "portable storage and startup resolve from the fixed marker while process authority retains the actual App host"
        );
        var installed = RuntimeProfileLayout.ForHost(
            Path.Combine(fixture, "installed", "Bezoro.AhkManager.exe"),
            Path.Combine(fixture, "local"),
            Path.Combine(fixture, "roaming")
        );
        check(
            !installed.Portable
                && installed.ProfileRoot
                    == Path.Combine(fixture, "local", "Bezoro", "AhkManagerElectron")
                && installed.ChromiumRoot
                    == Path.Combine(fixture, "roaming", "Bezoro", "AhkManagerElectron", "Chromium")
                && installed.UpdatesRoot == Path.Combine(installed.ProfileRoot, "Updates")
                && installed.StartupHostExecutable == installed.HostExecutable,
            "installed profiles, Chromium storage, update history and startup paths retain their existing locations"
        );
        check(
            Refuses(() =>
                RuntimeProfileLayout.ResolveDataDirectory(Path.GetPathRoot(root)!, "Data")
            ),
            "a portable package cannot claim an entire drive as its application container"
        );
        string external = Path.Combine(fixture, "selected external data");
        WriteMarker(root, external);
        var selected = Read(layout.HostExecutable, fixture, "layout");
        check(
            selected.DataRoot == external
                && selected.ProfileRoot == Path.Combine(external, "Profile")
                && ElevationReceiptStore.ProfileRootsMatch(
                    selected.ProfileRoot,
                    selected.ChromiumRoot,
                    layout.HostExecutable
                )
                && !ElevationReceiptStore.ProfileRootsMatch(
                    installed.ProfileRoot,
                    installed.ChromiumRoot,
                    layout.HostExecutable
                ),
            "custom portable data is selected consistently by the host and authenticated elevation checks"
        );
        WriteMarker(root, @"Personal Data\Manager");
        check(
            Read(layout.HostExecutable, fixture, "layout").DataRoot
                == Path.Combine(root, "Personal Data", "Manager"),
            "normalized nested relative data locations retain their portable root"
        );
        foreach (
            string invalid in new[]
            {
                "",
                ".",
                "..",
                @"..\outside",
                @"Data\..\other",
                @"Data\\nested",
                "Data/alias",
                "Data.",
                "Data ",
                "NUL",
                "CON.txt",
                "C:relative",
                "Data:stream",
                "App",
                @"App\Profile",
                "portable.json",
                @"portable.json\nested",
                "portable-package.json",
                "Bezoro.AhkManager.exe",
                ".app-stage-future-operation",
                @".app-previous-future-operation\nested",
                ".APP-STAGE-uppercase",
                ".portable-data-config-12345678-1234-1234-1234-123456789abc.tmp",
                @".portable-data-move-12345678-1234-1234-1234-123456789abc.tmp\nested",
                root,
                Path.GetDirectoryName(root)!,
                Path.GetPathRoot(root)!,
            }
        )
        {
            WriteMarker(root, invalid);
            check(
                Refuses(() => Read(layout.HostExecutable, fixture, "layout")),
                "portable data rejects aliases, reserved names and application or ancestor overlap: "
                    + invalid
            );
        }
        foreach (
            string invalid in new[]
            {
                "{invalid",
                "[]",
                "{\"schemaVersion\":\"1\",\"applicationId\":\"Bezoro.AhkManager.Electron\",\"dataDirectory\":\"Data\"}",
                "{\"schemaVersion\":1,\"applicationId\":\"another.app\",\"dataDirectory\":\"Data\"}",
                "{\"schemaVersion\":1,\"applicationId\":\"Bezoro.AhkManager.Electron\",\"dataDirectory\":\"Data\",\"dataDirectory\":\"Other\"}",
                "{\"schemaVersion\":1,\"applicationId\":\"Bezoro.AhkManager.Electron\",\"dataDirectory\":\"Data\",\"extra\":true}",
                new string(' ', RuntimeProfileLayout.MaximumMarkerBytes + 1),
            }
        )
        {
            File.WriteAllText(Path.Combine(root, "portable.json"), invalid);
            check(
                Refuses(() => Read(layout.HostExecutable, fixture, "layout")),
                "malformed, ambiguous or oversized portable markers never fall back to the installed profile"
            );
        }
        WriteMarker(root, "Data");
        File.WriteAllText(Path.Combine(root, "portable-package.json"), "inert descriptor");
        File.Delete(Path.Combine(root, "portable.json"));
        check(
            Refuses(() => Read(layout.HostExecutable, fixture, "layout")),
            "a portable package with missing data settings cannot silently open an installed profile"
        );
        File.Delete(Path.Combine(root, "portable-package.json"));
        WriteMarker(root, "Data");
        string marker = Path.Combine(root, "portable.json");
        string markerLink = Path.Combine(root, "portable-settings-alias.json");
        if (!CreateHardLink(markerLink, marker, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        check(
            Refuses(() => Read(layout.HostExecutable, fixture, "layout"))
                && File.ReadAllText(marker) == File.ReadAllText(markerLink),
            "multiply linked portable settings are rejected without rewriting either linked file"
        );
        File.Delete(markerLink);
        string fileData = Path.Combine(root, "data-is-file");
        File.WriteAllText(fileData, "preserve");
        WriteMarker(root, "data-is-file");
        check(
            Refuses(() => Read(layout.HostExecutable, fixture, "layout"))
                && File.ReadAllText(fileData) == "preserve",
            "the selected portable data location must be a directory without replacing a regular file"
        );
        string redirected = Path.Combine(root, "redirected");
        Directory.CreateDirectory(external);
        Directory.CreateSymbolicLink(redirected, external);
        WriteMarker(root, "redirected");
        check(
            Refuses(() => Read(layout.HostExecutable, fixture, "layout")),
            "portable data cannot be redirected through a directory link"
        );
        Directory.Delete(redirected);
    }

    static void DirectoryOwnership(string fixture, Action<bool, string> check)
    {
        string own = Path.Combine(fixture, "private directory");
        TrustedProfileDirectory.CreateNew(own);
        TrustedProfileDirectory.Verify(own);
        string sentinel = Path.Combine(own, "foreign.txt");
        File.WriteAllText(sentinel, "preserve");
        check(
            Refuses(() => TrustedProfileDirectory.CreateNew(own))
                && File.ReadAllText(sentinel) == "preserve",
            "fresh private creation refuses an existing directory without altering its files"
        );
        var info = new DirectoryInfo(own);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Modify,
                AccessControlType.Allow
            )
        );
        info.SetAccessControl(acl);
        const AccessControlSections sections =
            AccessControlSections.Owner | AccessControlSections.Access;
        string before = info.GetAccessControl().GetSecurityDescriptorSddlForm(sections);
        check(
            Refuses(() => TrustedProfileDirectory.Ensure(own))
                && info.GetAccessControl().GetSecurityDescriptorSddlForm(sections) == before,
            "existing shared data permissions are rejected without rewriting their ACL"
        );
        string missingParent = Path.Combine(fixture, "absent-parent", "data");
        check(
            Refuses(() => TrustedProfileDirectory.CreateNew(missingParent))
                && !Directory.Exists(Path.GetDirectoryName(missingParent)),
            "private data preparation never creates or changes missing ancestors"
        );
    }

    static async Task ImportAsync(string fixture, Action<bool, string> check)
    {
        var (layout, _) = Create(fixture, "import");
        CreateSource(layout);
        string externalScript = Path.Combine(fixture, "existing-script.ahk");
        await File.WriteAllTextAsync(externalScript, "; existing inert external script\n");
        string otherDriveScript = Path.GetPathRoot(fixture)!
            .StartsWith("Q:", StringComparison.OrdinalIgnoreCase)
            ? @"R:\External scripts\preserved-reference.ahk"
            : @"Q:\External scripts\preserved-reference.ahk";
        var document = new SettingsDocument
        {
            LastLoginKey = "same-user-logon",
            Scripts =
            [
                new()
                {
                    Path = externalScript,
                    FriendlyName = "Existing script",
                    RunAtLogin = true,
                },
                new()
                {
                    Path = otherDriveScript,
                    FriendlyName = "Existing script on another drive",
                },
            ],
            Settings = new() { StartInTray = true, Window = new(12, 24, 1024, 768, true, 144) },
        };
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(
            document,
            ManagerJson.Default.SettingsDocument
        );
        await File.WriteAllBytesAsync(
            Path.Combine(layout.LegacyProfileRoot, "settings.json"),
            source
        );
        var pending = new OperationRecord(
            Guid.NewGuid(),
            document.Scripts[0].Id,
            ScriptOperation.Start,
            externalScript,
            "",
            "",
            "",
            "S-1-fixture",
            document.LastLoginKey,
            DateTimeOffset.UtcNow,
            Outcome.Unknown
        );
        await File.WriteAllBytesAsync(
            Path.Combine(layout.LegacyProfileRoot, "operations.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new JournalDocument { Operations = [pending] },
                ManagerJson.Default.JournalDocument
            )
        );
        TrustedProfileDirectory.Ensure(layout.DataRoot);
        Directory.CreateDirectory(layout.ChromiumRoot);
        await File.WriteAllTextAsync(
            Path.Combine(layout.ChromiumRoot, "existing-cache.txt"),
            "chromium"
        );
        var session = new PortableProfileSession(layout, () => true, () => ManagerSnapshot.Empty);
        check(
            (await session.InitializeAsync(CancellationToken.None)).Succeeded,
            "portable initialization atomically imports the existing operational profile beside a separate Chromium directory"
        );
        var imported = await new SettingsStore(
            Path.Combine(layout.ProfileRoot, "settings.json")
        ).LoadAsync();
        check(
            !imported.ReadOnly
                && imported.Document.LastLoginKey == document.LastLoginKey
                && imported.Document.Settings.Window == document.Settings.Window
                && imported.Document.Settings.StartInTray
                && imported.Document.Scripts[0].Path == externalScript
                && File.ReadAllBytes(Path.Combine(layout.ProfileRoot, "settings.json"))
                    .SequenceEqual(source)
                && File.ReadAllBytes(Path.Combine(layout.LegacyProfileRoot, "settings.json"))
                    .SequenceEqual(source)
                && File.ReadAllText(externalScript) == "; existing inert external script\n",
            "profile import preserves source bytes, window placement, login marker and external script references without moving scripts"
        );
        check(
            imported.Document.Scripts[1].Path == otherDriveScript
                && !Directory
                    .EnumerateFiles(layout.DataRoot, "*.ahk", SearchOption.AllDirectories)
                    .Any(),
            "profile import preserves script references on another local drive without copying scripts into portable data"
        );
        var journal = new OperationJournal(Path.Combine(layout.ProfileRoot, "operations.json"));
        await journal.LoadAsync();
        check(
            journal.HasPending && journal.Records.Single() == pending,
            "unknown operation evidence is imported intact without replaying an operation"
        );
        check(
            Directory
                .GetFiles(
                    Path.Combine(layout.ProfileRoot, "migration-backup"),
                    "manifest.json",
                    SearchOption.AllDirectories
                )
                .Length == 1,
            "portable migration retains an exact source backup and migration receipt"
        );
        await File.WriteAllTextAsync(
            Path.Combine(layout.LegacyProfileRoot, "settings.json"),
            "{changed-source"
        );
        check(
            (await session.InitializeAsync(CancellationToken.None)).Succeeded
                && File.ReadAllBytes(Path.Combine(layout.ProfileRoot, "settings.json"))
                    .SequenceEqual(source),
            "portable initialization is idempotent and never overwrites an existing imported profile"
        );
    }

    static async Task ImportRefusalsAsync(string fixture, Action<bool, string> check)
    {
        var (owned, _) = Create(fixture, "other-manager");
        CreateSource(owned);
        await File.WriteAllBytesAsync(
            Path.Combine(owned.LegacyProfileRoot, "settings.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SettingsDocument(),
                ManagerJson.Default.SettingsDocument
            )
        );
        var denied = await new PortableProfileSession(
            owned,
            () => false,
            () => ManagerSnapshot.Empty
        ).InitializeAsync(CancellationToken.None);
        check(
            denied.Code == ReasonCode.EffectOwnerUnavailable
                && !File.Exists(Path.Combine(owned.ProfileRoot, "settings.json")),
            "another effect owner prevents automatic import without creating blank settings"
        );
        var (corrupt, _) = Create(fixture, "corrupt-import");
        CreateSource(corrupt);
        await File.WriteAllTextAsync(
            Path.Combine(corrupt.LegacyProfileRoot, "settings.json"),
            "{invalid"
        );
        var result = await new PortableProfileSession(
            corrupt,
            () => true,
            () => ManagerSnapshot.Empty
        ).InitializeAsync(CancellationToken.None);
        check(
            !result.Succeeded
                && result.Code == ReasonCode.ImportInvalid
                && !File.Exists(Path.Combine(corrupt.ProfileRoot, "settings.json"))
                && File.ReadAllText(Path.Combine(corrupt.LegacyProfileRoot, "settings.json"))
                    == "{invalid",
            "invalid source settings stop initialization with no empty replacement or source modification"
        );
        var (continuation, _) = Create(fixture, "pending-continuation");
        CreateSource(continuation);
        await File.WriteAllBytesAsync(
            Path.Combine(continuation.LegacyProfileRoot, "settings.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SettingsDocument(),
                ManagerJson.Default.SettingsDocument
            )
        );
        var ticket = new StartupContinuationTicket(
            1,
            Guid.NewGuid(),
            continuation.LegacyProfileRoot,
            new string('a', 64),
            "S-1-fixture",
            1,
            "same-logon",
            DateTimeOffset.UtcNow.AddMinutes(5),
            [
                new(
                    Guid.NewGuid(),
                    "existing",
                    Path.Combine(fixture, "existing.ahk"),
                    Path.Combine(fixture, "AutoHotkey.exe"),
                    new string('b', 64),
                    new string('c', 64)
                ),
            ]
        );
        await new StartupContinuationStore(continuation.LegacyProfileRoot).WriteAsync(
            new(
                ticket,
                StartupContinuationStore.Hash(ticket),
                StartupContinuationPhase.Accepted,
                [StartupContinuationItemState.Uncertain]
            ),
            null
        );
        result = await new PortableProfileSession(
            continuation,
            () => true,
            () => ManagerSnapshot.Empty
        ).InitializeAsync(CancellationToken.None);
        check(
            result.Code == ReasonCode.PendingOperation
                && !File.Exists(Path.Combine(continuation.ProfileRoot, "settings.json")),
            "unfinished startup continuation evidence cannot be dropped by automatic profile import"
        );
    }

    static async Task DirectoryRequestsAsync(string fixture, Action<bool, string> check)
    {
        var (layout, root) = Create(fixture, "directory-requests");
        var manager = new FakeManager
        {
            Snapshot = ManagerSnapshot.Empty with { EffectsAvailable = true },
        };
        bool owned = true;
        await using var session = new BackendSession(
            manager,
            layout.ProfileRoot,
            layout.StartupHostExecutable,
            () => owned,
            new FakeStartup(),
            layout: layout
        );
        var initial = await session.ExecuteAsync(
            "initializeProfile",
            Payload("{}"),
            CancellationToken.None
        );
        check(
            initial.Deserialize(Protocol.Json.OperationResult)!.Succeeded,
            "authenticated profile initialization returns an operation result before profile load"
        );
        string stage = Path.Combine(
            root,
            ".Selected Data.ahk-manager-move-" + Guid.NewGuid().ToString("D")
        );
        bool premature = false;
        try
        {
            await session.ExecuteAsync(
                "preparePortableDataDirectory",
                PathPayload(stage),
                CancellationToken.None
            );
        }
        catch (ProtocolException error)
        {
            premature = error.Code == "InvalidRequest";
        }
        check(
            premature && !Directory.Exists(stage),
            "data staging requires a loaded authenticated portable session"
        );
        await session.ExecuteAsync("load", Payload("{}"), CancellationToken.None);
        var prepared = await session.ExecuteAsync(
            "preparePortableDataDirectory",
            PathPayload(stage),
            CancellationToken.None
        );
        check(
            prepared.Deserialize(Protocol.Json.OperationResult)!.Succeeded
                && Directory.Exists(stage),
            "confirmed portable data staging creates a fresh private UUID sibling directory"
        );
        TrustedProfileDirectory.Verify(stage);
        File.WriteAllText(Path.Combine(stage, "sentinel.txt"), "preserve");
        prepared = await session.ExecuteAsync(
            "preparePortableDataDirectory",
            PathPayload(stage),
            CancellationToken.None
        );
        check(
            !prepared.Deserialize(Protocol.Json.OperationResult)!.Succeeded
                && File.ReadAllText(Path.Combine(stage, "sentinel.txt")) == "preserve",
            "data staging refuses preexisting entries without rewriting their content or permissions"
        );
        owned = false;
        string denied = Path.Combine(
            root,
            ".Denied.ahk-manager-move-" + Guid.NewGuid().ToString("D")
        );
        prepared = await session.ExecuteAsync(
            "preparePortableDataDirectory",
            PathPayload(denied),
            CancellationToken.None
        );
        check(
            prepared.Deserialize(Protocol.Json.OperationResult)!.Code
                == ReasonCode.EffectOwnerUnavailable
                && !Directory.Exists(denied),
            "data staging has no filesystem effects after effect ownership is lost"
        );
        owned = true;
        manager.Snapshot = manager.Snapshot with { Issues = [new(ReasonCode.PendingOperation)] };
        prepared = await session.ExecuteAsync(
            "preparePortableDataDirectory",
            PathPayload(denied),
            CancellationToken.None
        );
        check(
            prepared.Deserialize(Protocol.Json.OperationResult)!.Code == ReasonCode.PendingOperation
                && !Directory.Exists(denied),
            "unknown operation evidence prevents moving portable data"
        );
        manager.Snapshot = manager.Snapshot with { Issues = [] };
        string overlap = Path.Combine(
            layout.DataRoot,
            ".Nested.ahk-manager-move-" + Guid.NewGuid().ToString("D")
        );
        prepared = await session.ExecuteAsync(
            "preparePortableDataDirectory",
            PathPayload(overlap),
            CancellationToken.None
        );
        check(
            !prepared.Deserialize(Protocol.Json.OperationResult)!.Succeeded
                && !Directory.Exists(overlap),
            "data staging cannot target the current data directory or its descendants"
        );
        await using var installed = new BackendSession(
            manager,
            Path.Combine(fixture, "installed-profile"),
            Path.Combine(fixture, "Manager.exe"),
            () => true,
            new FakeStartup()
        );
        await installed.ExecuteAsync("load", Payload("{}"), CancellationToken.None);
        prepared = await installed.ExecuteAsync(
            "preparePortableDataDirectory",
            PathPayload(denied),
            CancellationToken.None
        );
        check(
            !prepared.Deserialize(Protocol.Json.OperationResult)!.Succeeded
                && !Directory.Exists(denied),
            "installed sessions cannot use the portable directory creation command"
        );
    }

    static (RuntimeProfileLayout Layout, string Root) Create(string fixture, string name)
    {
        string root = Path.Combine(fixture, name);
        Directory.CreateDirectory(Path.Combine(root, "App"));
        File.WriteAllText(Path.Combine(root, "App", "Bezoro.AhkManager.exe"), "inert host");
        File.WriteAllText(Path.Combine(root, "Bezoro.AhkManager.exe"), "inert launcher");
        WriteMarker(root, "Data");
        return (Read(Path.Combine(root, "App", "Bezoro.AhkManager.exe"), fixture, name), root);
    }

    static RuntimeProfileLayout Read(string host, string fixture, string name) =>
        RuntimeProfileLayout.ForHost(
            host,
            Path.Combine(fixture, name + "-local"),
            Path.Combine(fixture, name + "-roaming")
        );

    static void CreateSource(RuntimeProfileLayout layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(layout.LegacyProfileRoot)!);
        TrustedProfileDirectory.CreateNew(layout.LegacyProfileRoot);
    }

    static void WriteMarker(string root, string dataDirectory) =>
        File.WriteAllText(
            Path.Combine(root, "portable.json"),
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["applicationId"] = RuntimeProfileLayout.ApplicationId,
                ["dataDirectory"] = dataDirectory,
            }.ToJsonString()
        );

    static JsonElement PathPayload(string path) =>
        Payload(new JsonObject { ["path"] = path }.ToJsonString());

    static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    static bool Refuses(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception error)
            when (SafeJsonFiles.Known(error)
                || error is System.ComponentModel.Win32Exception or InvalidOperationException
            )
        {
            return true;
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string link, string existing, nint reserved);
}
