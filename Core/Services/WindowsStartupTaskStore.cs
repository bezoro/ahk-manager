using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
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
                    ?? throw new InvalidOperationException("Task Scheduler is unavailable.")
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
                    ?? throw new InvalidOperationException("Task Scheduler is unavailable.")
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
                    replacement.ParseIssue != StartupTaskParseIssue.None
                    || replacement.UserSid != sid
                    || replacement.Source != UserStartupService.TaskSource
                    || !replacement.InteractiveToken
                    || !replacement.UserLogonOnly
                )
                    throw new InvalidDataException("Invalid task specification.");
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
            throw new InvalidDataException("Invalid startup command.");
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
                        new object?[]
                        {
                            E("Command", executable),
                            E("Arguments", "--login --background"),
                            registration.NonstandardWorkingDirectory == ""
                                ? null
                                : E(
                                    "WorkingDirectory",
                                    registration.NonstandardWorkingDirectory
                                        ?? Path.GetDirectoryName(executable)
                                ),
                        }
                    )
                )
            )
        );
        return xml.ToString(SaveOptions.DisableFormatting);
    }

    internal static StartupTaskRegistration Parse(
        string xml,
        Func<string, string?>? resolveAccountSid = null
    )
    {
        string hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)))
            .ToLowerInvariant();
        StartupTaskRegistration Foreign(StartupTaskParseIssue issue) =>
            new("", "", false, false, false, false, "", hash, ParseIssue: issue);
        if (xml.Length > 65536)
            return Foreign(StartupTaskParseIssue.XmlShape);
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
                return Foreign(StartupTaskParseIssue.XmlShape);
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
                return Foreign(StartupTaskParseIssue.XmlShape);
            // Scheduler may persist the same user as a SID in the principal and
            // an NTAccount in the trigger. Ownership follows the resolved SID;
            // the fingerprint still binds the complete, unmodified XML.
            string? sid = ResolveSid(
                principals[0].Element(Ns + "UserId")?.Value,
                resolveAccountSid ?? AccountSid
            );
            if (sid is null)
                return Foreign(StartupTaskParseIssue.PrincipalAccountUnresolved);
            string? triggerSid = ResolveSid(
                triggers[0].Element(Ns + "UserId")?.Value,
                resolveAccountSid ?? AccountSid
            );
            if (triggerSid is null)
                return Foreign(StartupTaskParseIssue.TriggerAccountUnresolved);
            string? executable = CanonicalExecutable(
                actions[0].Element(Ns + "Command")?.Value ?? ""
            );
            string arguments = actions[0].Element(Ns + "Arguments")?.Value ?? "";
            string work = actions[0].Element(Ns + "WorkingDirectory")?.Value ?? "";
            if (executable is null)
                return Foreign(StartupTaskParseIssue.Executable);
            string command = ScriptPaths.QuoteArgument(executable) + " " + arguments;
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
                triggerSid == sid,
                root.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source")?.Value ?? "",
                hash,
                MatchesWorkingDirectory(executable, work) ? null : work
            );
        }
        catch (Exception ex) when (ex is XmlException or IOException or ArgumentException)
        {
            return Foreign(StartupTaskParseIssue.XmlShape);
        }
    }

    static string? CanonicalExecutable(string executable)
    {
        try
        {
            string canonical = ScriptPaths.LocalPath(executable);
            return ScriptPaths.Equal(executable, canonical) ? canonical : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static bool MatchesWorkingDirectory(string executable, string directory)
    {
        try
        {
            string canonical = ScriptPaths.LocalPath(directory);
            // A case change or trailing directory separator does not change the
            // startup context. Dot segments, aliases and relative paths need repair.
            return ScriptPaths.Equal(directory, canonical)
                && ScriptPaths.Equal(
                    Path.TrimEndingDirectorySeparator(canonical),
                    Path.GetDirectoryName(executable)
                );
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static string? ResolveSid(string? identity, Func<string, string?> resolveAccountSid)
    {
        if (
            string.IsNullOrWhiteSpace(identity)
            || identity.Length > 512
            || identity != identity.Trim()
            || identity.Any(char.IsControl)
        )
            return null;
        string[] parts = identity.Split('\\');
        if (parts.Length > 2 || parts.Any(part => part.Length == 0))
            return null;
        try
        {
            string? sid = IsSidText(identity) ? identity : resolveAccountSid(identity);
            return sid is not null && TryDecodeSid(sid, out byte[] binary)
                ? new SecurityIdentifier(binary, 0).Value
                : null;
        }
        catch (Exception error)
            when (error
                    is ArgumentException
                        or IdentityNotMappedException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException
                        or System.ComponentModel.Win32Exception
            )
        {
            return null;
        }
    }

    static bool IsSidText(string identity) =>
        !identity.Contains('\\')
        && !identity.Contains('@')
        && Regex.IsMatch(
            identity,
            @"\AS-[0-9]+(?:-|\z)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

    // Windows' string SID conversion can accept overflowing numeric fields.
    // Decode bounded values first so canonicalization cannot change their meaning.
    internal static bool TryDecodeSid(string? text, out byte[] binary)
    {
        binary = [];
        if (string.IsNullOrEmpty(text) || text.Length > 512 || text.Any(char.IsControl))
            return false;
        string[] parts = text.Split('-');
        if (
            parts.Length is < 3 or > 18
            || parts[0] is not ("S" or "s")
            || !byte.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out byte revision
            )
            || revision != 1
        )
            return false;
        string authorityText = parts[2];
        ulong authority;
        bool authorityRead = authorityText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(
                authorityText.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out authority
            )
            : ulong.TryParse(
                authorityText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out authority
            );
        if (!authorityRead || authority > 0xFFFFFFFFFFFF)
            return false;
        var subauthorities = new uint[parts.Length - 3];
        for (int index = 0; index < subauthorities.Length; index++)
            if (
                !uint.TryParse(
                    parts[index + 3],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out subauthorities[index]
                )
            )
                return false;
        binary = new byte[8 + 4 * subauthorities.Length];
        binary[0] = revision;
        binary[1] = checked((byte)subauthorities.Length);
        for (int index = 0; index < 6; index++)
            binary[index + 2] = (byte)(authority >> ((5 - index) * 8) & 0xFF);
        for (int index = 0; index < subauthorities.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(
                binary.AsSpan(8 + 4 * index, 4),
                subauthorities[index]
            );
        return true;
    }

    static string AccountSid(string account) =>
        ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
}
