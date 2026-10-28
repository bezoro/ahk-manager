using System.Collections.Immutable;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Microsoft.Win32;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed record StartupInspection(bool Complete, ImmutableArray<string> ScriptPaths, ImmutableArray<Issue> Issues);
public interface IUserStartupAdapter
{
    string? ReadEntry();
    bool CompareExchangeEntry(string? expected, string? replacement);
    Task<StartupInspection> InspectAsync(CancellationToken ct);
}

public sealed record StartupTaskRegistration(string Command, string UserSid, bool Enabled,
    bool HighestPrivileges, bool InteractiveToken, bool UserLogonOnly, string Source, string Fingerprint = "");

// Kept separate so existing read-only/fake registry adapters remain usable.
public interface IUserStartupTaskAdapter
{
    string UserSid { get; }
    StartupTaskRegistration? ReadTask();
    bool CompareExchangeTask(StartupTaskRegistration? expected, StartupTaskRegistration? replacement);
}

public sealed class RegistryStartupAdapter : IUserStartupAdapter, IUserStartupTaskAdapter
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string EntryName = "Bezoro.AhkManager.WinUI";
    public string UserSid { get { using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query); return identity.User?.Value ?? throw new UnauthorizedAccessException("Não foi possível identificar o usuário atual."); } }
    public StartupTaskRegistration? ReadTask() => WindowsStartupTaskStore.Read(UserSid);
    public bool CompareExchangeTask(StartupTaskRegistration? expected, StartupTaskRegistration? replacement) => WindowsStartupTaskStore.CompareExchange(UserSid, expected, replacement);
    public string? ReadEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : value is string text ? text : "<non-string>";
    }
    public bool CompareExchangeEntry(string? expected, string? replacement)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        var current = key.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (current is not null && current is not string || (string?)current != expected) return false;
        if (replacement is null) key.DeleteValue(EntryName, false);
        else key.SetValue(EntryName, replacement, RegistryValueKind.String);
        return true;
    }
    public Task<StartupInspection> InspectAsync(CancellationToken ct) => InspectAsync(ct, null);
    internal Task<StartupInspection> InspectAsync(CancellationToken ct, string? trustedManagerCommand) => Task.Run(() =>
    {
        var paths = ImmutableArray.CreateBuilder<string>(); var issues = ImmutableArray.CreateBuilder<Issue>(); bool complete = true;
        string origin = "Entradas de início do Windows";
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Command FROM Win32_StartupCommand"); search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var rows = search.Get();
            foreach (ManagementObject row in rows) using (row) { ct.ThrowIfCancellationRequested(); if (row["Command"] is string command) Inspect(command); }
            origin = "Pastas de inicialização do Windows";
            foreach (var directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.ahk")) { ct.ThrowIfCancellationRequested(); paths.Add(ScriptPaths.Canonical(file)); }
            }
            origin = "Tarefas agendadas do Windows";
            using var scheduled = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\TaskScheduler"), new ObjectQuery("SELECT Actions, Triggers, Settings FROM MSFT_ScheduledTask"));
            scheduled.Options.Timeout = TimeSpan.FromSeconds(5);
            using var tasks = scheduled.Get();
            string userSid = UserSid;
            foreach (ManagementObject task in tasks)
            using (task)
            {
                ct.ThrowIfCancellationRequested();
                if (!ShouldInspectScheduledTask(task, userSid)) continue;
                if (task["Actions"] is not Array actions) continue;
                foreach (var value in actions)
                {
                    if (value is not ManagementBaseObject action) { complete = false; AddIssue("Uma ação agendada não pôde ser interpretada. Revise as ações de início automático."); continue; }
                    using (action)
                    {
                        if (ReadActionProperty(action, "Execute") is not string execute) continue;
                        string arguments = ReadActionProperty(action, "Arguments") ?? "";
                        Inspect(ScriptPaths.QuoteArgument(execute) + " " + arguments);
                    }
                }
            }
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is ManagementException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            complete = false;
            string reason = ex switch
            {
                UnauthorizedAccessException => "acesso negado",
                ManagementException management => $"consulta WMI: {management.ErrorCode}",
                System.Runtime.InteropServices.COMException native => $"erro do Windows: 0x{native.ErrorCode:X8}",
                IOException => "falha de leitura",
                _ => "dados indisponíveis ou inválidos"
            };
            AddIssue($"Não foi possível concluir a consulta ({reason}). Confira o acesso a essa origem e tente novamente.");
        }
        return new StartupInspection(complete, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), issues.ToImmutable());
        void Inspect(string command)
        {
            var inspected = InspectCommand(command, trustedManagerCommand);
            complete &= inspected.Complete;
            paths.AddRange(inspected.ScriptPaths);
            foreach (var issue in inspected.Issues) AddIssue(issue.Detail ?? "Uma configuração de início não pôde ser interpretada.");
        }
        void AddIssue(string detail) { if (issues.Count < 8) issues.Add(new(ReasonCode.ExternalStartupConflict, $"{origin}: {detail}")); }
    }, ct);

    internal static string? ReadActionProperty(ManagementBaseObject action, string name)
    {
        // WMI may return a PropertyData wrapper whose Value throws NotFound for
        // an absent property. COM-handler actions do not expose Execute/Arguments.
        foreach (PropertyData property in action.Properties)
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value as string;
        return null;
    }

    internal static bool RelevantLoginTask(bool? enabled, IEnumerable<(string Type, bool? Enabled, string? UserId)> triggers, string userSid)
    {
        if (enabled == false) return false;
        foreach (var trigger in triggers)
        {
            if (trigger.Enabled == false || !trigger.Type.Equals("MSFT_TaskLogonTrigger", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(trigger.UserId) || trigger.UserId.Equals(userSid, StringComparison.OrdinalIgnoreCase)) return true;
            if (trigger.UserId.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) continue;
            try { if (new NTAccount(trigger.UserId).Translate(typeof(SecurityIdentifier)).Value == userSid) return true; }
            catch (IdentityNotMappedException) { return true; } // Unknown ownership cannot prove absence.
            catch (SystemException ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return true; }
        }
        return false;
    }
    internal static bool ShouldInspectScheduledTask(ManagementBaseObject task, string userSid)
    {
        object? Value(ManagementBaseObject item, string name) { foreach (PropertyData property in item.Properties) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value; return null; }
        bool? enabled = null;
        if (Value(task, "Settings") is ManagementBaseObject settings) using (settings) enabled = Value(settings, "Enabled") as bool?;
        if (enabled == false) return false;
        if (Value(task, "Triggers") is not Array values) return false;
        var triggers = new List<(string Type, bool? Enabled, string? UserId)>();
        foreach (var value in values)
        {
            if (value is not ManagementBaseObject trigger) return true;
            using (trigger) triggers.Add((trigger.ClassPath.ClassName, Value(trigger, "Enabled") as bool?, Value(trigger, "UserId") as string));
        }
        return RelevantLoginTask(enabled, triggers, userSid);
    }

    internal static StartupInspection InspectCommand(string command) => InspectCommand(command, null);
    internal static StartupInspection InspectCommand(string command, string? trustedManagerCommand)
    {
        // Only the host-derived exact registration is excluded. A basename or an
        // AutoHotkey-containing installation directory does not establish identity.
        if (trustedManagerCommand is not null && command.Equals(trustedManagerCommand, StringComparison.OrdinalIgnoreCase)) return new(true, [], []);
        bool related = command.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase);
        for (int start = 0; !related;)
        {
            int index = command.IndexOf(".ahk", start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            int end = index + 4;
            // A name such as Bezoro.AhkManager.exe is not an AHK extension.
            // Punctuation remains relevant so malformed/unsupported forms fail closed.
            related = end == command.Length || !char.IsLetterOrDigit(command[end]) && command[end] != '_';
            start = end;
        }
        if (!related) return new(true, [], []);
        if (command.Contains('%')) return Unresolved();
        var args = ScriptPaths.SplitCommand(command);
        if (args.Length == 1 && args[0].EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) return new(true, [ScriptPaths.Canonical(args[0])], []);
        var script = args.Length > 0 ? ScriptPaths.ScriptFromCommand(command, args[0]) : null;
        return script is null ? Unresolved() : new(true, [script], []);
        static StartupInspection Unresolved() => new(false, [], [new(ReasonCode.ExternalStartupConflict, "Uma entrada relacionada ao AutoHotkey não tem um caminho de script verificável. Revise a entrada e use caminhos explícitos para evitar duplicação.")]);
    }
}

public sealed class UserStartupService(IUserStartupAdapter adapter, string managerExecutable)
{
    public const string TaskSource = "Bezoro.AhkManager.Electron.Startup.v1";
    public string Command { get; } = ScriptPaths.QuoteArgument(ScriptPaths.LocalPath(managerExecutable)) + " --login --background";
    public Task<StartupInspection> InspectAsync(CancellationToken ct) => adapter is RegistryStartupAdapter registry ? registry.InspectAsync(ct, Command) : adapter.InspectAsync(ct);
    bool Owned(StartupTaskRegistration task, IUserStartupTaskAdapter tasks) => task.Source == TaskSource && task.Command == Command && task.UserSid == tasks.UserSid && task.InteractiveToken && task.UserLogonOnly;
    bool Desired(StartupTaskRegistration task) => task.Enabled && task.HighestPrivileges;
    public ReasonCode RegistrationStatus(bool expectedEnabled) => RegistrationStatus(expectedEnabled, false);
    public ReasonCode RegistrationStatus(bool expectedEnabled, bool elevated)
    {
        try
        {
            string? existing = adapter.ReadEntry();
            var tasks = adapter as IUserStartupTaskAdapter;
            var task = tasks?.ReadTask();
            if (existing is not null && existing != Command || task is not null && (tasks is null || !Owned(task, tasks))) return ReasonCode.StartupEntryConflict;
            if (!expectedEnabled) return existing is null && task is null ? ReasonCode.None : ReasonCode.StartupEntryConflict;
            if (elevated && tasks is null) return ReasonCode.CapabilityUnverified;
            return elevated ? existing is null && task is not null && Desired(task) ? ReasonCode.None : ReasonCode.StartupInactive
                : existing == Command && task is null ? ReasonCode.None : ReasonCode.StartupInactive;
        }
        catch (Exception ex) when (RegistrationException(ex)) { return ReasonCode.AccessDenied; }
    }
    public Task<OperationResult> SetEnabledAsync(bool enabled, bool consent, CancellationToken ct = default) => SetEnabledAsync(enabled, consent, false, ct);
    public Task<OperationResult> SetEnabledAsync(bool enabled, bool consent, bool elevated, CancellationToken ct = default) => Task.Run(() =>
    {
        string? originalEntry = null;
        StartupTaskRegistration? originalTask = null;
        var tasks = adapter as IUserStartupTaskAdapter;
        bool changed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            originalEntry = adapter.ReadEntry(); originalTask = tasks?.ReadTask();
            if (originalEntry is not null && originalEntry != Command || originalTask is not null && (tasks is null || !Owned(originalTask, tasks))) return new OperationResult(Outcome.Failed, ReasonCode.StartupEntryConflict);
            if (enabled && elevated && tasks is null) return new(Outcome.Failed, ReasonCode.CapabilityUnverified, "O adaptador de início não oferece tarefas administrativas.");
            bool correct = enabled ? elevated ? originalEntry is null && originalTask is not null && Desired(originalTask) : originalEntry == Command && originalTask is null : originalEntry is null && originalTask is null;
            if (correct) return new(Outcome.Confirmed);
            if (enabled && !consent) return new(Outcome.Failed, ReasonCode.StartupConsentRequired);

            // Remove the previous mode before creating the replacement. TASK_CREATE
            // and registry compare/exchange refuse a concurrently supplied entry.
            if (originalEntry is not null && (!enabled || elevated))
            {
                if (!adapter.CompareExchangeEntry(originalEntry, null)) return new(Outcome.Unknown, ReasonCode.StartupEntryConflict);
                changed = true;
            }
            if (originalTask is not null && (!enabled || !elevated || !Desired(originalTask)))
            {
                if (!tasks!.CompareExchangeTask(originalTask, null)) throw new StartupRegistrationConflictException();
                changed = true;
            }
            if (enabled && elevated && (originalTask is null || !Desired(originalTask)))
            {
                var desired = new StartupTaskRegistration(Command, tasks!.UserSid, true, true, true, true, TaskSource);
                if (!tasks.CompareExchangeTask(null, desired)) throw new StartupRegistrationConflictException();
                changed = true;
            }
            else if (enabled && !elevated && originalEntry is null)
            {
                if (!adapter.CompareExchangeEntry(null, Command)) throw new StartupRegistrationConflictException();
                changed = true;
            }
            var final = RegistrationStatus(enabled, elevated);
            return final == ReasonCode.None ? new(Outcome.Confirmed) : new(Outcome.Unknown, final, "O registro de início mudou durante a operação. Confira as preferências.");
        }
        catch (OperationCanceledException) { return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled); }
        catch (Exception ex) when (RegistrationException(ex))
        {
            bool restored = !changed || RestorePrevious();
            bool denied = ex is UnauthorizedAccessException || ex is COMException native && native.ErrorCode == unchecked((int)0x80070005);
            var code = ex is StartupRegistrationConflictException ? ReasonCode.StartupEntryConflict : denied ? elevated || originalTask is not null ? ReasonCode.ManualElevationRequired : ReasonCode.AccessDenied : ReasonCode.StorageFailure;
            string detail = code is ReasonCode.AccessDenied or ReasonCode.ManualElevationRequired ? "O Windows negou a alteração do início automático. Reabra o Manager como administrador para configurar a tarefa com privilégios mais altos." : "Não foi possível concluir a alteração do início automático.";
            detail += restored ? " O registro anterior foi preservado." : " O registro precisa ser conferido; a alteração ficou parcial.";
            return new(restored ? Outcome.Failed : Outcome.Partial, code, detail);
        }

        bool RestorePrevious()
        {
            try
            {
                string? currentEntry = adapter.ReadEntry(); var currentTask = tasks?.ReadTask();
                if (originalEntry is not null && originalTask is null)
                {
                    if (currentEntry == originalEntry && currentTask is null) return true;
                    if (currentEntry is null && currentTask is null) return adapter.CompareExchangeEntry(null, originalEntry);
                }
                else if (originalTask is not null && originalEntry is null)
                {
                    if (currentEntry is null && currentTask == originalTask) return true;
                    if (currentEntry is null && currentTask is null) return tasks!.CompareExchangeTask(null, originalTask);
                }
                else if (originalEntry is null && originalTask is null) return currentEntry is null && currentTask is null;
                return false;
            }
            catch (Exception rollback) when (RegistrationException(rollback)) { return false; }
        }
    }, ct);
    static bool RegistrationException(Exception ex) => SafeJsonFiles.Known(ex) || ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception;
    sealed class StartupRegistrationConflictException : IOException;
}

internal static class WindowsStartupTaskStore
{
    internal static string TaskName(string sid) => "Bezoro.AhkManager.Electron." + sid;
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    static bool Missing(COMException ex) => ex.ErrorCode is unchecked((int)0x80070002) or unchecked((int)0x8004130F);

    internal static StartupTaskRegistration? Read(string sid)
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Agendador de Tarefas indisponível."));
            ((dynamic)service!).Connect(); folder = ((dynamic)service).GetFolder("\\");
            try { task = ((dynamic)folder).GetTask(TaskName(sid)); }
            catch (COMException ex) when (Missing(ex)) { return null; }
            return Parse((string)((dynamic)task).Xml);
        }
        finally { Release(task); Release(folder); Release(service); }
    }
    internal static bool CompareExchange(string sid, StartupTaskRegistration? expected, StartupTaskRegistration? replacement)
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Agendador de Tarefas indisponível."));
            ((dynamic)service!).Connect(); folder = ((dynamic)service).GetFolder("\\");
            try { task = ((dynamic)folder).GetTask(TaskName(sid)); }
            catch (COMException ex) when (Missing(ex)) { }
            var current = task is null ? null : Parse((string)((dynamic)task).Xml);
            if (current != expected) return false;
            if (expected is not null) ((dynamic)folder).DeleteTask(TaskName(sid), 0);
            if (replacement is not null)
            {
                if (replacement.UserSid != sid || replacement.Source != UserStartupService.TaskSource || !replacement.InteractiveToken || !replacement.UserLogonOnly) throw new InvalidDataException("Especificação de tarefa inválida.");
                // TASK_CREATE (2), never CREATE_OR_UPDATE: another entry wins a race.
                object registered = ((dynamic)folder).RegisterTask(TaskName(sid), Xml(replacement), 2, sid, null, 3, null);
                Release(registered);
            }
            return true;
        }
        finally { Release(task); Release(folder); Release(service); }
    }
    internal static string Xml(StartupTaskRegistration registration)
    {
        var args = ScriptPaths.SplitCommand(registration.Command);
        if (args.Length != 3 || args[1] != "--login" || args[2] != "--background") throw new InvalidDataException("Comando de início inválido.");
        string executable = ScriptPaths.LocalPath(args[0]);
        XElement E(string name, object? content) => new(Ns + name, content);
        var xml = new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.4"),
            E("RegistrationInfo", E("Source", registration.Source)),
            E("Triggers", E("LogonTrigger", new object[] { E("Enabled", registration.Enabled), E("UserId", registration.UserSid) })),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "CurrentUser"), E("UserId", registration.UserSid), E("LogonType", "InteractiveToken"), E("RunLevel", registration.HighestPrivileges ? "HighestAvailable" : "LeastPrivilege"))),
            E("Settings", new object[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false), E("AllowHardTerminate", false), E("StartWhenAvailable", true), E("Enabled", registration.Enabled), E("ExecutionTimeLimit", "PT0S") }),
            new XElement(Ns + "Actions", new XAttribute("Context", "CurrentUser"), E("Exec", new object[] { E("Command", executable), E("Arguments", "--login --background"), E("WorkingDirectory", Path.GetDirectoryName(executable)) }))));
        return xml.ToString(SaveOptions.DisableFormatting);
    }
    internal static StartupTaskRegistration Parse(string xml)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
        StartupTaskRegistration foreign = new("", "", false, false, false, false, "", hash);
        if (xml.Length > 65536) return foreign;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 65536 });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != Ns + "Task") return foreign;
            var actions = root.Element(Ns + "Actions")?.Elements().ToArray();
            var principals = root.Element(Ns + "Principals")?.Elements().ToArray();
            var triggers = root.Element(Ns + "Triggers")?.Elements().ToArray();
            if (actions is not { Length: 1 } || actions[0].Name != Ns + "Exec" || principals is not { Length: 1 } || principals[0].Name != Ns + "Principal" || triggers is not { Length: 1 } || triggers[0].Name != Ns + "LogonTrigger") return foreign;
            string sid = principals[0].Element(Ns + "UserId")?.Value ?? "";
            string executable = actions[0].Element(Ns + "Command")?.Value ?? "";
            string arguments = actions[0].Element(Ns + "Arguments")?.Value ?? "";
            string work = actions[0].Element(Ns + "WorkingDirectory")?.Value ?? "";
            bool validPath = Path.IsPathFullyQualified(executable) && ScriptPaths.Equal(Path.GetDirectoryName(executable), work);
            string command = validPath ? ScriptPaths.QuoteArgument(ScriptPaths.LocalPath(executable)) + " " + arguments : "";
            static bool Enabled(XElement? value) => value?.Value is not ("false" or "0");
            bool enabled = Enabled(root.Element(Ns + "Settings")?.Element(Ns + "Enabled")) && Enabled(triggers[0].Element(Ns + "Enabled"));
            return new(command, sid, enabled, principals[0].Element(Ns + "RunLevel")?.Value == "HighestAvailable", principals[0].Element(Ns + "LogonType")?.Value == "InteractiveToken", triggers[0].Element(Ns + "UserId")?.Value == sid && sid.Length > 0, root.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source")?.Value ?? "", hash);
        }
        catch (Exception ex) when (ex is XmlException or IOException or ArgumentException) { return foreign; }
    }
}
