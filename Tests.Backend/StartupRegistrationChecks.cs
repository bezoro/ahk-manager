using System.Collections.Immutable;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Services;

namespace Tests.Backend;

internal static class StartupRegistrationChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string host = Path.Combine(root, "manager with spaces.exe");
        var adapter = new TaskAdapter();
        var startup = new UserStartupService(adapter, host);
        check(startup.RegistrationStatus(false, false) == ReasonCode.None, "startup empty registration is really disabled");
        check((await startup.SetEnabledAsync(true, false, true)).Code == ReasonCode.StartupConsentRequired && adapter.Writes == 0, "administrative startup requires explicit registration consent");
        check((await startup.SetEnabledAsync(true, true, false)).Succeeded && adapter.Entry == startup.Command && adapter.Task is null, "normal startup registers exactly the owned HKCU command");
        int writes = adapter.Writes;
        check((await startup.SetEnabledAsync(true, false, false)).Succeeded && adapter.Writes == writes, "already correct normal startup needs no rewrite or repeat consent");
        check((await startup.SetEnabledAsync(true, true, true)).Succeeded && adapter.Entry is null && adapter.Task is { Enabled: true, HighestPrivileges: true, InteractiveToken: true, UserLogonOnly: true } task && task.UserSid == adapter.UserSid && task.Command == startup.Command && task.Source == UserStartupService.TaskSource, "normal to administrative transition creates one current-user highest interactive logon task");
        check(startup.RegistrationStatus(true, true) == ReasonCode.None && startup.RegistrationStatus(true, false) == ReasonCode.StartupInactive, "startup status verifies actual registration mode");
        writes = adapter.Writes;
        check((await startup.SetEnabledAsync(true, false, true)).Succeeded && adapter.Writes == writes, "correct administrative registration is idempotent");
        check((await startup.SetEnabledAsync(true, true, false)).Succeeded && adapter.Task is null && adapter.Entry == startup.Command && !adapter.DuplicateObserved, "administrative to normal transition removes the task before writing Run");

        adapter.Entry = null;
        check(startup.RegistrationStatus(true, false) == ReasonCode.StartupInactive && (await startup.SetEnabledAsync(true, true, false)).Succeeded && adapter.Entry == startup.Command, "unchanged enabled preference repairs missing normal registration");
        check((await startup.SetEnabledAsync(true, true, true)).Succeeded, "administrative fixture is registered for repair");
        adapter.Task = adapter.Task! with { Enabled = false };
        check(startup.RegistrationStatus(true, true) == ReasonCode.StartupInactive && (await startup.SetEnabledAsync(true, true, true)).Succeeded && adapter.Task!.Enabled, "disabled owned task is repaired without duplicate registration");
        adapter.Task = adapter.Task! with { HighestPrivileges = false };
        check((await startup.SetEnabledAsync(true, true, true)).Succeeded && adapter.Task!.HighestPrivileges, "owned task with wrong run level is repaired");
        adapter.Task = null;
        check((await startup.SetEnabledAsync(true, true, true)).Succeeded && startup.RegistrationStatus(true, true) == ReasonCode.None, "unchanged enabled preference repairs missing administrative registration");
        check((await startup.SetEnabledAsync(false, false, true)).Succeeded && adapter.Task is null && adapter.Entry is null, "disabling startup removes owned registration");

        adapter.Entry = "foreign command"; writes = adapter.Writes;
        check((await startup.SetEnabledAsync(true, true, true)).Code == ReasonCode.StartupEntryConflict && (await startup.SetEnabledAsync(false, true, false)).Code == ReasonCode.StartupEntryConflict && adapter.Entry == "foreign command" && adapter.Writes == writes, "foreign Run entry is neither overwritten nor removed");
        adapter.Entry = null;
        adapter.Task = new(startup.Command, adapter.UserSid, true, true, true, true, "foreign source"); writes = adapter.Writes;
        check((await startup.SetEnabledAsync(true, true, false)).Code == ReasonCode.StartupEntryConflict && adapter.Writes == writes && adapter.Entry is null, "foreign task with matching action does not become owned from its path alone");
        foreach (var foreign in new[] { adapter.Task with { Source = UserStartupService.TaskSource, UserSid = "S-1-5-21-other" }, adapter.Task with { Source = UserStartupService.TaskSource, UserLogonOnly = false }, adapter.Task with { Source = UserStartupService.TaskSource, InteractiveToken = false }, adapter.Task with { Source = UserStartupService.TaskSource, Command = "foreign command" } })
        {
            adapter.Task = foreign; writes = adapter.Writes;
            check((await startup.SetEnabledAsync(false, true, true)).Code == ReasonCode.StartupEntryConflict && adapter.Writes == writes, "task ownership binds current SID exact action logon trigger and interactive token");
        }

        adapter.Task = null; adapter.Entry = startup.Command; adapter.DenyTaskCreate = true;
        var denied = await startup.SetEnabledAsync(true, true, true);
        check(denied.Outcome == Outcome.Failed && denied.Code == ReasonCode.ManualElevationRequired && denied.Detail?.Contains("administrador") == true && adapter.Entry == startup.Command && adapter.Task is null && !adapter.DuplicateObserved, "denied task registration requests elevation and restores the previous normal registration");
        adapter.DenyTaskCreate = false;
        check((await startup.SetEnabledAsync(true, true, true)).Succeeded, "administrative setup succeeds after access is available");
        adapter.DenyRunCreate = true;
        var deniedNormal = await startup.SetEnabledAsync(true, true, false);
        check(deniedNormal.Outcome == Outcome.Failed && adapter.Task is not null && adapter.Entry is null && !adapter.DuplicateObserved, "failed normal replacement restores the previous task without a duplicate");
        adapter.DenyRunCreate = false;
        adapter.Task = null; adapter.Entry = startup.Command; adapter.SupplyForeignTask = true;
        var raced = await startup.SetEnabledAsync(true, true, true);
        check(raced.Outcome == Outcome.Partial && raced.Code == ReasonCode.StartupEntryConflict && adapter.Task?.Source == "concurrent foreign" && adapter.Entry is null && !adapter.DuplicateObserved, "concurrent foreign task is preserved without creating a duplicate rollback entry");

        var legacy = new LegacyAdapter(); var legacyService = new UserStartupService(legacy, host);
        check((await legacyService.SetEnabledAsync(true, true)).Succeeded && legacy.Entry == legacyService.Command, "registry-only adapters preserve the original normal-startup API");
        check((await legacyService.SetEnabledAsync(true, true, true)).Code == ReasonCode.CapabilityUnverified && legacy.Entry == legacyService.Command, "registry-only adapters refuse unsupported administrative registration without effects");

        string sid = adapter.UserSid;
        check(!RegistryStartupAdapter.RelevantLoginTask(false, [("MSFT_TaskLogonTrigger", true, sid)], sid), "disabled scheduled tasks cannot create startup conflicts");
        check(!RegistryStartupAdapter.RelevantLoginTask(true, [("MSFT_TaskDailyTrigger", true, null)], sid), "non-logon scheduled tasks cannot create startup conflicts");
        check(!RegistryStartupAdapter.RelevantLoginTask(true, [("MSFT_TaskLogonTrigger", false, sid)], sid), "disabled logon triggers cannot create startup conflicts");
        check(!RegistryStartupAdapter.RelevantLoginTask(true, [("MSFT_TaskLogonTrigger", true, "S-1-5-21-other")], sid), "another user's known SID logon task is outside the current startup scope");
        check(RegistryStartupAdapter.RelevantLoginTask(true, [("MSFT_TaskDailyTrigger", true, null), ("MSFT_TaskLogonTrigger", true, sid)], sid), "enabled current-user logon task remains a relevant conflict");
        check(RegistryStartupAdapter.RelevantLoginTask(null, [("MSFT_TaskLogonTrigger", null, null)], sid), "unknown enablement of an all-user logon trigger remains conservative");
        string ownInAhkDirectory = new UserStartupService(adapter, Path.Combine(root, "AutoHotkey", "Manager", "Bezoro.AhkManager.exe")).Command;
        check(!RegistryStartupAdapter.InspectCommand(ownInAhkDirectory).Complete && RegistryStartupAdapter.InspectCommand(ownInAhkDirectory, ownInAhkDirectory) is { Complete: true, ScriptPaths.IsEmpty: true }, "host-derived exact startup command is excluded even inside an AutoHotkey-named installation directory");
        check(!RegistryStartupAdapter.InspectCommand(ownInAhkDirectory + " --unexpected", ownInAhkDirectory).Complete, "extra arguments cannot impersonate the trusted manager startup command");
        string ahkPath = Path.Combine(root, "startup-relevance.ahk"); await File.WriteAllTextAsync(ahkPath, "; inert startup-inspection fixture");
        string actualScriptCommand = ScriptPaths.QuoteArgument(Path.Combine(root, "AutoHotkey64.exe")) + " /script " + ScriptPaths.QuoteArgument(ahkPath);
        var actualScript = RegistryStartupAdapter.InspectCommand(actualScriptCommand, ownInAhkDirectory);
        check(actualScript.Complete && actualScript.ScriptPaths.Length == 1 && ScriptPaths.Equal(actualScript.ScriptPaths[0], ScriptPaths.Canonical(ahkPath)), "actual AutoHotkey script commands remain inspected alongside the exact manager exclusion");

        var specification = new StartupTaskRegistration(startup.Command, sid, true, true, true, true, UserStartupService.TaskSource);
        string xml = WindowsStartupTaskStore.Xml(specification);
        var parsed = WindowsStartupTaskStore.Parse(xml);
        check(parsed with { Fingerprint = "" } == specification && parsed.Fingerprint.Length == 64, "task XML preserves exact highest interactive current-user action and immutable identity");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = XDocument.Parse(xml);
        check(document.Root!.Element(ns + "Triggers")!.Elements().Count() == 1 && document.Root.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!.Element(ns + "UserId")!.Value == sid && document.Root.Element(ns + "Settings")!.Element(ns + "ExecutionTimeLimit")!.Value == "PT0S", "task definition contains only the user's logon trigger and no runtime limit");
        document.Root.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value = "0";
        check(!WindowsStartupTaskStore.Parse(document.ToString()).Enabled, "XML boolean zero is treated as a disabled task");
        document = XDocument.Parse(xml); document.Root!.Element(ns + "Triggers")!.Add(new XElement(ns + "BootTrigger"));
        check(!WindowsStartupTaskStore.Parse(document.ToString()).UserLogonOnly, "additional boot trigger prevents a task from being treated as an owned login-only registration");
        document = XDocument.Parse(xml); document.Root!.Element(ns + "Actions")!.Add(new XElement(ns + "Exec"));
        check(WindowsStartupTaskStore.Parse(document.ToString()).Command.Length == 0, "additional task actions prevent ownership");
        check(WindowsStartupTaskStore.Parse("<!DOCTYPE x [<!ENTITY a 'expanded'>]><x>&a;</x>").Command.Length == 0, "task parser refuses DTD input without expanding entities");
        string installedHost = StartupUninstallInspection.HostPath(Path.Combine(root, "installed", "backend"));
        check(ScriptPaths.Equal(installedHost, Path.Combine(root, "installed", "Bezoro.AhkManager.exe")), "uninstall inspection fixes the host to the backend's parent directory");
        var ownedForRemoval = specification with { Command = ScriptPaths.QuoteArgument(installedHost) + " --login --background" };
        check(StartupUninstallInspection.Classify(null, installedHost, sid) == 0 && StartupUninstallInspection.Classify(ownedForRemoval, installedHost, sid) == 2, "uninstall inspection distinguishes absent and owned exact current-user task");
        check(StartupUninstallInspection.Classify(ownedForRemoval with { Enabled = false, HighestPrivileges = false }, installedHost, sid) == 2, "disabled owned task still blocks uninstall so no orphan definition is left behind");
        check(StartupUninstallInspection.Classify(ownedForRemoval, installedHost.ToUpperInvariant(), sid) == 2, "uninstall ownership respects Windows path casing without relaxing arguments");
        foreach (var foreign in new[] { ownedForRemoval with { Source = "foreign source" }, ownedForRemoval with { UserSid = "S-1-5-21-other" }, ownedForRemoval with { Command = ownedForRemoval.Command + " --extra" }, ownedForRemoval with { InteractiveToken = false }, ownedForRemoval with { UserLogonOnly = false }, ownedForRemoval with { Command = ScriptPaths.QuoteArgument(Path.Combine(root, "other-install.exe")) + " --login --background" } })
            check(StartupUninstallInspection.Classify(foreign, installedHost, sid) == 0, "read-only uninstall inspection preserves foreign identity source path or trigger");
        if (OperatingSystem.IsWindows())
        {
            InspectEmbeddedTaskProperties(sid, check);
            ValidateNativeTaskDefinition(specification, check);
        }
    }

    static void ValidateNativeTaskDefinition(StartupTaskRegistration specification, Action<bool, string> check)
    {
        object? service = null, definition = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            specification = specification with { UserSid = identity.User!.Value };
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect();
            definition = ((dynamic)service).NewTask(0);
            // XmlText validates the real Task Scheduler schema on an in-memory
            // definition. Deliberately never call RegisterTask or RegisterTaskDefinition.
            ((dynamic)definition).XmlText = WindowsStartupTaskStore.Xml(specification);
            var parsed = WindowsStartupTaskStore.Parse((string)((dynamic)definition).XmlText);
            check(parsed with { Fingerprint = "" } == specification, "native Task Scheduler accepts the highest interactive logon XML without registering it");
        }
        finally
        {
            if (definition is not null && Marshal.IsComObject(definition)) Marshal.ReleaseComObject(definition);
            if (service is not null && Marshal.IsComObject(service)) Marshal.ReleaseComObject(service);
        }
    }

    static void InspectEmbeddedTaskProperties(string sid, Action<bool, string> check)
    {
        // WMI class instances stay in memory. There is no Put, task registration,
        // process launch or mutation of the machine's startup configuration.
        const string scope = @"\\.\root\Microsoft\Windows\TaskScheduler:";
        using var taskClass = new ManagementClass(scope + "MSFT_ScheduledTask");
        using var settingsClass = new ManagementClass(scope + "MSFT_TaskSettings3");
        using var logonClass = new ManagementClass(scope + "MSFT_TaskLogonTrigger");
        using var dailyClass = new ManagementClass(scope + "MSFT_TaskDailyTrigger");
        using var task = taskClass.CreateInstance()!;
        using var settings = settingsClass.CreateInstance()!;
        using var logon = logonClass.CreateInstance()!;
        using var daily = dailyClass.CreateInstance()!;
        settings["Enabled"] = true; logon["Enabled"] = true; logon["UserId"] = sid;
        task["Settings"] = settings; task["Triggers"] = new ManagementBaseObject[] { logon };
        check(RegistryStartupAdapter.ShouldInspectScheduledTask(task, sid), "WMI embedded enabled current-user logon task is inspected");
        settings["Enabled"] = false; task["Settings"] = settings;
        check(!RegistryStartupAdapter.ShouldInspectScheduledTask(task, sid), "WMI embedded disabled task is filtered before actions");
        settings["Enabled"] = true; task["Settings"] = settings; logon["Enabled"] = false; task["Triggers"] = new ManagementBaseObject[] { logon };
        check(!RegistryStartupAdapter.ShouldInspectScheduledTask(task, sid), "WMI embedded disabled logon trigger is filtered");
        daily["Enabled"] = true; task["Triggers"] = new ManagementBaseObject[] { daily };
        check(!RegistryStartupAdapter.ShouldInspectScheduledTask(task, sid), "WMI embedded daily-only task is outside login scope");
        task["Triggers"] = Array.Empty<ManagementBaseObject>();
        check(!RegistryStartupAdapter.ShouldInspectScheduledTask(task, sid), "WMI embedded demand-only task is outside login scope");
    }

    sealed class TaskAdapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string UserSid => "S-1-5-21-startup-fixture";
        public string? Entry;
        public StartupTaskRegistration? Task;
        public int Writes;
        public bool DuplicateObserved, DenyTaskCreate, DenyRunCreate, SupplyForeignTask;
        public string? ReadEntry() => Entry;
        public StartupTaskRegistration? ReadTask() => Task;
        void Written() { Writes++; DuplicateObserved |= Entry is not null && Task is not null; }
        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            if (Entry != expected) return false;
            if (DenyRunCreate && replacement is not null) throw new IOException("Injected registry write refusal.");
            Entry = replacement; Written(); return true;
        }
        public bool CompareExchangeTask(StartupTaskRegistration? expected, StartupTaskRegistration? replacement)
        {
            if (SupplyForeignTask && replacement is not null) { SupplyForeignTask = false; Task = replacement with { Source = "concurrent foreign" }; return false; }
            if (Task != expected) return false;
            if (DenyTaskCreate && replacement is not null) throw new UnauthorizedAccessException("Injected task permission refusal.");
            Task = replacement; Written(); return true;
        }
        public Task<StartupInspection> InspectAsync(CancellationToken ct) => System.Threading.Tasks.Task.FromResult(new StartupInspection(true, [], []));
    }
    sealed class LegacyAdapter : IUserStartupAdapter
    {
        public string? Entry;
        public string? ReadEntry() => Entry;
        public bool CompareExchangeEntry(string? expected, string? replacement) { if (expected != Entry) return false; Entry = replacement; return true; }
        public Task<StartupInspection> InspectAsync(CancellationToken ct) => System.Threading.Tasks.Task.FromResult(new StartupInspection(true, [], []));
    }
}
