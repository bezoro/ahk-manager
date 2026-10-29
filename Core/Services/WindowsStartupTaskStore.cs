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

internal static class WindowsStartupTaskStore
{
    internal static string TaskName(string sid) => "Bezoro.AhkManager.Electron." + sid;

    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    // COM interop can translate ERROR_FILE_NOT_FOUND to FileNotFoundException.
    // Scheduler account/security failures do not establish that a task is absent.
    internal static bool Missing(Exception ex) => ex.HResult == unchecked((int)0x80070002);

    internal static StartupTaskRegistration? Read(string sid)
    {
        object? service = null,
            folder = null,
            task = null;
        try
        {
            service = Activator.CreateInstance(
                Type.GetTypeFromProgID("Schedule.Service")
                    ?? throw new InvalidOperationException("Agendador de Tarefas indisponível.")
            );
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            try
            {
                task = ((dynamic)folder).GetTask(TaskName(sid));
            }
            catch (Exception ex) when (Missing(ex))
            {
                return null;
            }
            return Parse((string)((dynamic)task).Xml);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    internal static bool CompareExchange(
        string sid,
        StartupTaskRegistration? expected,
        StartupTaskRegistration? replacement
    )
    {
        object? service = null,
            folder = null,
            task = null;
        try
        {
            service = Activator.CreateInstance(
                Type.GetTypeFromProgID("Schedule.Service")
                    ?? throw new InvalidOperationException("Agendador de Tarefas indisponível.")
            );
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            try
            {
                task = ((dynamic)folder).GetTask(TaskName(sid));
            }
            catch (Exception ex) when (Missing(ex)) { }
            var current = task is null ? null : Parse((string)((dynamic)task).Xml);
            if (current != expected)
                return false;
            if (expected is not null)
                ((dynamic)folder).DeleteTask(TaskName(sid), 0);
            if (replacement is not null)
            {
                if (
                    replacement.UserSid != sid
                    || replacement.Source != UserStartupService.TaskSource
                    || !replacement.InteractiveToken
                    || !replacement.UserLogonOnly
                )
                    throw new InvalidDataException("Especificação de tarefa inválida.");
                // TASK_CREATE (2), never CREATE_OR_UPDATE: another entry wins a race.
                object registered = ((dynamic)folder).RegisterTask(
                    TaskName(sid),
                    Xml(replacement),
                    2,
                    sid,
                    null,
                    3,
                    null
                );
                Release(registered);
            }
            return true;
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    internal static string Xml(StartupTaskRegistration registration)
    {
        var args = ScriptPaths.SplitCommand(registration.Command);
        if (args.Length != 3 || args[1] != "--login" || args[2] != "--background")
            throw new InvalidDataException("Comando de início inválido.");
        string executable = ScriptPaths.LocalPath(args[0]);
        XElement E(string name, object? content) => new(Ns + name, content);
        var xml = new XDocument(
            new XElement(
                Ns + "Task",
                new XAttribute("version", "1.4"),
                E("RegistrationInfo", E("Source", registration.Source)),
                E(
                    "Triggers",
                    E(
                        "LogonTrigger",
                        new object[]
                        {
                            E("Enabled", registration.Enabled),
                            E("UserId", registration.UserSid),
                        }
                    )
                ),
                E(
                    "Principals",
                    new XElement(
                        Ns + "Principal",
                        new XAttribute("id", "CurrentUser"),
                        E("UserId", registration.UserSid),
                        E("LogonType", "InteractiveToken"),
                        E(
                            "RunLevel",
                            registration.HighestPrivileges ? "HighestAvailable" : "LeastPrivilege"
                        )
                    )
                ),
                E(
                    "Settings",
                    new object[]
                    {
                        E("MultipleInstancesPolicy", "IgnoreNew"),
                        E("DisallowStartIfOnBatteries", false),
                        E("StopIfGoingOnBatteries", false),
                        E("AllowHardTerminate", false),
                        E("StartWhenAvailable", true),
                        E("Enabled", registration.Enabled),
                        E("ExecutionTimeLimit", "PT0S"),
                    }
                ),
                new XElement(
                    Ns + "Actions",
                    new XAttribute("Context", "CurrentUser"),
                    E(
                        "Exec",
                        new object[]
                        {
                            E("Command", executable),
                            E("Arguments", "--login --background"),
                            E("WorkingDirectory", Path.GetDirectoryName(executable)),
                        }
                    )
                )
            )
        );
        return xml.ToString(SaveOptions.DisableFormatting);
    }

    internal static StartupTaskRegistration Parse(string xml)
    {
        string hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)))
            .ToLowerInvariant();
        StartupTaskRegistration foreign = new("", "", false, false, false, false, "", hash);
        if (xml.Length > 65536)
            return foreign;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 65536,
                }
            );
            var root = XDocument.Load(reader).Root;
            if (root?.Name != Ns + "Task")
                return foreign;
            var actions = root.Element(Ns + "Actions")?.Elements().ToArray();
            var principals = root.Element(Ns + "Principals")?.Elements().ToArray();
            var triggers = root.Element(Ns + "Triggers")?.Elements().ToArray();
            if (
                actions is not { Length: 1 }
                || actions[0].Name != Ns + "Exec"
                || principals is not { Length: 1 }
                || principals[0].Name != Ns + "Principal"
                || triggers is not { Length: 1 }
                || triggers[0].Name != Ns + "LogonTrigger"
            )
                return foreign;
            string sid = principals[0].Element(Ns + "UserId")?.Value ?? "";
            string executable = actions[0].Element(Ns + "Command")?.Value ?? "";
            string arguments = actions[0].Element(Ns + "Arguments")?.Value ?? "";
            string work = actions[0].Element(Ns + "WorkingDirectory")?.Value ?? "";
            bool validPath =
                Path.IsPathFullyQualified(executable)
                && ScriptPaths.Equal(Path.GetDirectoryName(executable), work);
            string command = validPath
                ? ScriptPaths.QuoteArgument(ScriptPaths.LocalPath(executable)) + " " + arguments
                : "";
            static bool Enabled(XElement? value) => value?.Value is not ("false" or "0");
            bool enabled =
                Enabled(root.Element(Ns + "Settings")?.Element(Ns + "Enabled"))
                && Enabled(triggers[0].Element(Ns + "Enabled"));
            return new(
                command,
                sid,
                enabled,
                principals[0].Element(Ns + "RunLevel")?.Value == "HighestAvailable",
                principals[0].Element(Ns + "LogonType")?.Value == "InteractiveToken",
                triggers[0].Element(Ns + "UserId")?.Value == sid && sid.Length > 0,
                root.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source")?.Value ?? "",
                hash
            );
        }
        catch (Exception ex) when (ex is XmlException or IOException or ArgumentException)
        {
            return foreign;
        }
    }
}
