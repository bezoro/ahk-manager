using System.Collections.Immutable;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32;

namespace Bezoro.AhkManager.Core.Services;

public sealed class RegistryStartupAdapter : IUserStartupAdapter, IUserStartupTaskAdapter
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string EntryName = "Bezoro.AhkManager.WinUI";
    public string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return identity.User?.Value
                ?? throw new UnauthorizedAccessException(
                    "Não foi possível identificar o usuário atual."
                );
        }
    }

    public StartupTaskRegistration? ReadTask() => WindowsStartupTaskStore.Read(UserSid);

    public bool CompareExchangeTask(
        StartupTaskRegistration? expected,
        StartupTaskRegistration? replacement
    ) => WindowsStartupTaskStore.CompareExchange(UserSid, expected, replacement);

    public string? ReadEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(
            EntryName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames
        );
        return value is null ? null
            : value is string text ? text
            : "<non-string>";
    }

    public bool CompareExchangeEntry(string? expected, string? replacement)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        var current = key.GetValue(
            EntryName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames
        );
        if (current is not null && current is not string || (string?)current != expected)
            return false;
        if (replacement is null)
            key.DeleteValue(EntryName, false);
        else
            key.SetValue(EntryName, replacement, RegistryValueKind.String);
        return true;
    }

    public Task<StartupInspection> InspectAsync(CancellationToken ct) => InspectAsync(ct, null);

    internal Task<StartupInspection> InspectAsync(
        CancellationToken ct,
        string? trustedManagerCommand
    ) =>
        Task.Run(
            () =>
            {
                var paths = ImmutableArray.CreateBuilder<string>();
                var issues = ImmutableArray.CreateBuilder<Issue>();
                bool complete = true;
                string origin = "Entradas de início do Windows";
                try
                {
                    using var search = new ManagementObjectSearcher(
                        "SELECT Command FROM Win32_StartupCommand"
                    );
                    search.Options.Timeout = TimeSpan.FromSeconds(5);
                    using var rows = search.Get();
                    foreach (ManagementObject row in rows)
                        using (row)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (row["Command"] is string command)
                                Inspect(command);
                        }
                    origin = "Pastas de inicialização do Windows";
                    foreach (
                        var directory in new[]
                        {
                            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                        }
                    )
                    {
                        if (!Directory.Exists(directory))
                            continue;
                        foreach (var file in Directory.EnumerateFiles(directory, "*.ahk"))
                        {
                            ct.ThrowIfCancellationRequested();
                            paths.Add(ScriptPaths.Canonical(file));
                        }
                    }
                    origin = "Tarefas agendadas do Windows";
                    using var scheduled = new ManagementObjectSearcher(
                        new ManagementScope(@"\\.\root\Microsoft\Windows\TaskScheduler"),
                        new ObjectQuery(
                            "SELECT Actions, Triggers, Settings FROM MSFT_ScheduledTask"
                        )
                    );
                    scheduled.Options.Timeout = TimeSpan.FromSeconds(5);
                    using var tasks = scheduled.Get();
                    string userSid = UserSid;
                    foreach (ManagementObject task in tasks)
                        using (task)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!ShouldInspectScheduledTask(task, userSid))
                                continue;
                            if (task["Actions"] is not Array actions)
                                continue;
                            foreach (var value in actions)
                            {
                                if (value is not ManagementBaseObject action)
                                {
                                    complete = false;
                                    AddIssue(
                                        "Uma ação agendada não pôde ser interpretada. Revise as ações de início automático."
                                    );
                                    continue;
                                }
                                using (action)
                                {
                                    if (ReadActionProperty(action, "Execute") is not string execute)
                                        continue;
                                    string arguments =
                                        ReadActionProperty(action, "Arguments") ?? "";
                                    Inspect(ScriptPaths.QuoteArgument(execute) + " " + arguments);
                                }
                            }
                        }
                }
                catch (Exception ex)
                    when (SafeJsonFiles.Known(ex)
                        || ex
                            is ManagementException
                                or System.Runtime.InteropServices.COMException
                                or InvalidOperationException
                    )
                {
                    complete = false;
                    string reason = ex switch
                    {
                        UnauthorizedAccessException => "acesso negado",
                        ManagementException management => $"consulta WMI: {management.ErrorCode}",
                        System.Runtime.InteropServices.COMException native =>
                            $"erro do Windows: 0x{native.ErrorCode:X8}",
                        IOException => "falha de leitura",
                        _ => "dados indisponíveis ou inválidos",
                    };
                    AddIssue(
                        $"Não foi possível concluir a consulta ({reason}). Confira o acesso a essa origem e tente novamente."
                    );
                }
                return new StartupInspection(
                    complete,
                    paths.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
                    issues.ToImmutable()
                );
                void Inspect(string command)
                {
                    var inspected = InspectCommand(command, trustedManagerCommand);
                    complete &= inspected.Complete;
                    paths.AddRange(inspected.ScriptPaths);
                    foreach (var issue in inspected.Issues)
                        AddIssue(
                            issue.Detail ?? "Uma configuração de início não pôde ser interpretada."
                        );
                }
                void AddIssue(string detail)
                {
                    if (issues.Count < 8)
                        issues.Add(new(ReasonCode.ExternalStartupConflict, $"{origin}: {detail}"));
                }
            },
            ct
        );

    internal static string? ReadActionProperty(ManagementBaseObject action, string name)
    {
        // WMI may return a PropertyData wrapper whose Value throws NotFound for
        // an absent property. COM-handler actions do not expose Execute/Arguments.
        foreach (PropertyData property in action.Properties)
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value as string;
        return null;
    }

    internal static bool RelevantLoginTask(
        bool? enabled,
        IEnumerable<(string Type, bool? Enabled, string? UserId)> triggers,
        string userSid
    )
    {
        if (enabled == false)
            return false;
        foreach (var trigger in triggers)
        {
            if (
                trigger.Enabled == false
                || !trigger.Type.Equals("MSFT_TaskLogonTrigger", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (
                string.IsNullOrEmpty(trigger.UserId)
                || trigger.UserId.Equals(userSid, StringComparison.OrdinalIgnoreCase)
            )
                return true;
            if (trigger.UserId.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (
                    new NTAccount(trigger.UserId).Translate(typeof(SecurityIdentifier)).Value
                    == userSid
                )
                    return true;
            }
            catch (IdentityNotMappedException)
            {
                return true;
            } // Unknown ownership cannot prove absence.
            catch (SystemException ex)
                when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool ShouldInspectScheduledTask(ManagementBaseObject task, string userSid)
    {
        object? Value(ManagementBaseObject item, string name)
        {
            foreach (PropertyData property in item.Properties)
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            return null;
        }
        bool? enabled = null;
        if (Value(task, "Settings") is ManagementBaseObject settings)
            using (settings)
                enabled = Value(settings, "Enabled") as bool?;
        if (enabled == false)
            return false;
        if (Value(task, "Triggers") is not Array values)
            return false;
        var triggers = new List<(string Type, bool? Enabled, string? UserId)>();
        foreach (var value in values)
        {
            if (value is not ManagementBaseObject trigger)
                return true;
            using (trigger)
                triggers.Add(
                    (
                        trigger.ClassPath.ClassName,
                        Value(trigger, "Enabled") as bool?,
                        Value(trigger, "UserId") as string
                    )
                );
        }
        return RelevantLoginTask(enabled, triggers, userSid);
    }

    internal static StartupInspection InspectCommand(string command) =>
        InspectCommand(command, null);

    internal static StartupInspection InspectCommand(string command, string? trustedManagerCommand)
    {
        // Only the host-derived exact registration is excluded. A basename or an
        // AutoHotkey-containing installation directory does not establish identity.
        if (
            trustedManagerCommand is not null
            && command.Equals(trustedManagerCommand, StringComparison.OrdinalIgnoreCase)
        )
            return new(true, [], []);
        bool related = command.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase);
        for (int start = 0; !related; )
        {
            int index = command.IndexOf(".ahk", start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                break;
            int end = index + 4;
            // A name such as Bezoro.AhkManager.exe is not an AHK extension.
            // Punctuation remains relevant so malformed/unsupported forms fail closed.
            related =
                end == command.Length || !char.IsLetterOrDigit(command[end]) && command[end] != '_';
            start = end;
        }
        if (!related)
            return new(true, [], []);
        if (command.Contains('%'))
            return Unresolved();
        var args = ScriptPaths.SplitCommand(command);
        if (args.Length == 1 && args[0].EndsWith(".ahk", StringComparison.OrdinalIgnoreCase))
            return new(true, [ScriptPaths.Canonical(args[0])], []);
        var script = args.Length > 0 ? ScriptPaths.ScriptFromCommand(command, args[0]) : null;
        return script is null ? Unresolved() : new(true, [script], []);
        static StartupInspection Unresolved() =>
            new(
                false,
                [],
                [
                    new(
                        ReasonCode.ExternalStartupConflict,
                        "Uma entrada relacionada ao AutoHotkey não tem um caminho de script verificável. Revise a entrada e use caminhos explícitos para evitar duplicação."
                    ),
                ]
            );
    }
}
