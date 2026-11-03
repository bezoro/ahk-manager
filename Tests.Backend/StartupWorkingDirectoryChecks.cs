using System.Xml.Linq;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupWorkingDirectoryChecks
{
    const string Sid = "S-1-5-21-100-200-300-1001";
    const string OtherSid = "S-1-5-21-100-200-300-1002";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var adapter = new SerializedAdapter();
        string host = Path.Combine(root, "startup working directory", "Bezoro.AhkManager.exe");
        var startup = new UserStartupService(adapter, host);
        var specification = new StartupTaskRegistration(
            startup.Command,
            Sid,
            true,
            true,
            true,
            true,
            UserStartupService.TaskSource
        );
        string directory = Path.GetDirectoryName(host)!;
        string canonical = WindowsStartupTaskStore.Xml(specification);
        foreach (
            string workingDirectory in new[]
            {
                directory,
                directory.ToUpperInvariant(),
                directory + Path.DirectorySeparatorChar,
            }
        )
        {
            adapter.Reset(WorkingDirectory(canonical, workingDirectory));
            var observed = adapter.ReadTask()!;
            check(
                observed.WorkingDirectoryMatches
                    && observed.NonstandardWorkingDirectory is null
                    && observed with { Fingerprint = "" } == specification
                    && startup.RegistrationStatus(true, true) == ReasonCode.None,
                "canonical case and trailing-separator working directories remain healthy owned tasks"
            );
            check(
                (await startup.SetEnabledAsync(true, false, true)).Succeeded && adapter.Writes == 0,
                "equivalent working directories need no rewrite or repeated consent"
            );
        }

        string stale = Path.Combine(root, "previous working directory", "App");
        foreach (string? workingDirectory in new string?[] { null, "", stale, "relative" })
        {
            string xml = WorkingDirectory(canonical, workingDirectory);
            adapter.Reset(xml);
            var observed = adapter.ReadTask()!;
            check(
                observed.ParseIssue == StartupTaskParseIssue.None
                    && observed.Command == startup.Command
                    && !observed.WorkingDirectoryMatches
                    && observed.NonstandardWorkingDirectory == (workingDirectory ?? "")
                    && UserStartupService.OwnsTask(observed, startup.Command, Sid)
                    && startup.RegistrationStatus(true, true) == ReasonCode.StartupInactive,
                "a proven task with missing stale or relative working directory needs repair rather than foreign ownership"
            );
            var roundtrip = WindowsStartupTaskStore.Parse(WindowsStartupTaskStore.Xml(observed));
            check(
                roundtrip with { Fingerprint = observed.Fingerprint } == observed,
                "serializing an observed unhealthy task preserves its original working directory for rollback"
            );
            check(
                (await startup.SetEnabledAsync(true, true, true)).Succeeded
                    && adapter.ReadTask()!.WorkingDirectoryMatches
                    && adapter.ReadTask()!.Command == startup.Command
                    && adapter.Removed.Single() == observed
                    && adapter.Writes == 2
                    && !adapter.DuplicateObserved
                    && startup.RegistrationStatus(true, true) == ReasonCode.None,
                "repair replaces only the exact observed task and confirms the canonical directory without duplicate startup"
            );

            foreach (bool elevated in new[] { false, true })
            {
                adapter.Reset(xml);
                adapter.FailNextTaskCreation = elevated;
                adapter.FailNextEntryCreation = !elevated;
                var failed = await startup.SetEnabledAsync(true, true, elevated);
                var restored = adapter.ReadTask();
                check(
                    failed.Outcome == Outcome.Failed
                        && failed.Code == ReasonCode.StorageFailure
                        && failed.Detail?.Contains("previous registration was preserved") == true
                        && restored is not null
                        && restored with { Fingerprint = observed.Fingerprint } == observed
                        && !restored.WorkingDirectoryMatches
                        && adapter.Entry is null
                        && !adapter.DuplicateObserved,
                    "failed replacement faithfully restores the nonstandard directory through actual XML serialization and parsing"
                );
            }
        }

        string previousHost = Path.Combine(root, "previous startup host", "Bezoro.AhkManager.exe");
        var previous = new UserStartupService(adapter, previousHost);
        string relocated = WorkingDirectory(
            WindowsStartupTaskStore.Xml(specification with { Command = previous.Command }),
            Path.GetDirectoryName(previousHost) + Path.DirectorySeparatorChar + "App"
        );
        adapter.Reset(relocated);
        var old = adapter.ReadTask()!;
        check(
            startup.RegistrationStatus(true, true) == ReasonCode.StartupInactive
                && (await startup.SetEnabledAsync(true, true, true)).Succeeded
                && adapter.ReadTask()!.Command == startup.Command
                && adapter.ReadTask()!.WorkingDirectoryMatches
                && adapter.Removed.Single() == old,
            "relocation and working-directory repair use the same proven source account action and observed fingerprint"
        );

        string unhealthy = WorkingDirectory(canonical, stale);
        string concurrent = Change(
            unhealthy,
            document =>
                document
                    .Root!.Element(Ns + "RegistrationInfo")!
                    .Add(new XElement(Ns + "Description", "Concurrent task edit"))
        );
        adapter.Reset(unhealthy);
        adapter.ReplaceBeforeRemoval = concurrent;
        check(
            (await startup.SetEnabledAsync(true, true, true)).Code
                == ReasonCode.StartupEntryConflict
                && adapter.TaskXml == concurrent
                && adapter.Writes == 0,
            "working-directory repair preserves a concurrent XML change even when projected identity is unchanged"
        );
        string foreignSource = Change(
            unhealthy,
            document =>
                document.Root!.Element(Ns + "RegistrationInfo")!.Element(Ns + "Source")!.Value =
                    "another product"
        );
        adapter.Reset(unhealthy);
        adapter.ReplaceBeforeCreation = foreignSource;
        var raced = await startup.SetEnabledAsync(true, true, true);
        check(
            raced.Outcome == Outcome.Partial
                && raced.Code == ReasonCode.StartupEntryConflict
                && adapter.TaskXml == foreignSource
                && adapter.Entry is null,
            "repair rollback preserves a concurrently supplied foreign task instead of overwriting its registration"
        );

        foreach (string xml in ForeignDefinitions(unhealthy))
        {
            adapter.Reset(xml);
            check(
                startup.RegistrationStatus(true, true) == ReasonCode.StartupEntryConflict
                    && (await startup.SetEnabledAsync(true, true, true)).Code
                        == ReasonCode.StartupEntryConflict
                    && (await startup.SetEnabledAsync(false, true, true)).Code
                        == ReasonCode.StartupEntryConflict
                    && adapter.TaskXml == xml
                    && adapter.Writes == 0,
                "a working-directory defect cannot authorize foreign sources accounts actions triggers or executable paths"
            );
        }
        CheckParseIssues(canonical, specification, check);
    }

    static IEnumerable<string> ForeignDefinitions(string xml)
    {
        yield return Change(
            xml,
            document =>
                document.Root!.Element(Ns + "RegistrationInfo")!.Element(Ns + "Source")!.Remove()
        );
        yield return Change(
            xml,
            document =>
                document.Root!.Element(Ns + "RegistrationInfo")!.Element(Ns + "Source")!.Value =
                    "another product"
        );
        yield return Change(
            xml,
            document =>
                document
                    .Root!.Element(Ns + "Principals")!
                    .Element(Ns + "Principal")!
                    .Element(Ns + "UserId")!
                    .Value = OtherSid
        );
        yield return Change(
            xml,
            document =>
                document
                    .Root!.Element(Ns + "Triggers")!
                    .Element(Ns + "LogonTrigger")!
                    .Element(Ns + "UserId")!
                    .Value = OtherSid
        );
        yield return Change(
            xml,
            document =>
                document
                    .Root!.Element(Ns + "Principals")!
                    .Element(Ns + "Principal")!
                    .Element(Ns + "LogonType")!
                    .Value = "S4U"
        );
        yield return Change(
            xml,
            document => document.Root!.Element(Ns + "Actions")!.Add(new XElement(Ns + "Exec"))
        );
        yield return Change(
            xml,
            document =>
                document.Root!.Element(Ns + "Triggers")!.Add(new XElement(Ns + "BootTrigger"))
        );
        yield return Change(
            xml,
            document =>
                document
                    .Root!.Element(Ns + "Actions")!
                    .Element(Ns + "Exec")!
                    .Element(Ns + "Arguments")!
                    .Value += " --extra"
        );
        yield return Change(
            xml,
            document =>
                document
                    .Root!.Element(Ns + "Actions")!
                    .Element(Ns + "Exec")!
                    .Element(Ns + "Command")!
                    .Value = @"relative\Bezoro.AhkManager.exe"
        );
    }

    static void CheckParseIssues(
        string xml,
        StartupTaskRegistration specification,
        Action<bool, string> check
    )
    {
        foreach (
            var sample in new[]
            {
                (Xml: "<invalid", Issue: StartupTaskParseIssue.XmlShape),
                (
                    Xml: Change(
                        xml,
                        document =>
                            document
                                .Root!.Element(Ns + "Principals")!
                                .Element(Ns + "Principal")!
                                .Element(Ns + "UserId")!
                                .Value = "unresolved account"
                    ),
                    Issue: StartupTaskParseIssue.PrincipalAccountUnresolved
                ),
                (
                    Xml: Change(
                        xml,
                        document =>
                            document
                                .Root!.Element(Ns + "Triggers")!
                                .Element(Ns + "LogonTrigger")!
                                .Element(Ns + "UserId")!
                                .Value = "unresolved account"
                    ),
                    Issue: StartupTaskParseIssue.TriggerAccountUnresolved
                ),
                (
                    Xml: Change(
                        xml,
                        document =>
                            document
                                .Root!.Element(Ns + "Actions")!
                                .Element(Ns + "Exec")!
                                .Element(Ns + "Command")!
                                .Value = @"\\server\share\Bezoro.AhkManager.exe"
                    ),
                    Issue: StartupTaskParseIssue.Executable
                ),
            }
        )
        {
            var parsed = WindowsStartupTaskStore.Parse(sample.Xml, _ => null);
            check(
                parsed.ParseIssue == sample.Issue
                    && parsed.Command.Length == 0
                    && parsed.Fingerprint.Length == 64
                    && !UserStartupService.OwnsTask(parsed, specification.Command, Sid),
                "task parser retains finite refusal categories while preserving the unmodified XML fingerprint"
            );
        }
        check(
            !UserStartupService.OwnsTask(
                specification with
                {
                    ParseIssue = StartupTaskParseIssue.XmlShape,
                },
                specification.Command,
                Sid
            ),
            "a projected parse refusal never establishes task ownership"
        );
    }

    static string WorkingDirectory(string xml, string? directory) =>
        Change(
            xml,
            document =>
            {
                var element = document
                    .Root!.Element(Ns + "Actions")!
                    .Element(Ns + "Exec")!
                    .Element(Ns + "WorkingDirectory")!;
                if (directory is null)
                    element.Remove();
                else
                    element.Value = directory;
            }
        );

    static string Change(string xml, Action<XDocument> change)
    {
        var document = XDocument.Parse(xml);
        change(document);
        return document.ToString(SaveOptions.DisableFormatting);
    }

    // Reify every write through production XML serialization/parsing so a fake
    // adapter cannot conceal changed working directories during rollback.
    sealed class SerializedAdapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string UserSid => Sid;
        public string? Entry;
        public string? TaskXml;
        public string? ReplaceBeforeRemoval,
            ReplaceBeforeCreation;
        public bool FailNextEntryCreation,
            FailNextTaskCreation,
            DuplicateObserved;
        public int Writes;
        public readonly List<StartupTaskRegistration> Removed = [];

        public void Reset(string xml)
        {
            TaskXml = xml;
            Entry = null;
            ReplaceBeforeRemoval = ReplaceBeforeCreation = null;
            FailNextEntryCreation = FailNextTaskCreation = DuplicateObserved = false;
            Writes = 0;
            Removed.Clear();
        }

        public string? ReadEntry() => Entry;

        public StartupTaskRegistration? ReadTask() =>
            TaskXml is null ? null : WindowsStartupTaskStore.Parse(TaskXml);

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            if (replacement is not null && FailNextEntryCreation)
            {
                FailNextEntryCreation = false;
                throw new IOException("Injected Run creation failure.");
            }
            if (Entry != expected)
                return false;
            Entry = replacement;
            Writes++;
            DuplicateObserved |= Entry is not null && TaskXml is not null;
            return true;
        }

        public bool CompareExchangeTask(
            StartupTaskRegistration? expected,
            StartupTaskRegistration? replacement
        )
        {
            if (expected is not null && ReplaceBeforeRemoval is { } removalRace)
            {
                TaskXml = removalRace;
                ReplaceBeforeRemoval = null;
            }
            if (replacement is not null && ReplaceBeforeCreation is { } creationRace)
            {
                TaskXml = creationRace;
                ReplaceBeforeCreation = null;
            }
            if (ReadTask() != expected)
                return false;
            if (replacement is not null && FailNextTaskCreation)
            {
                FailNextTaskCreation = false;
                throw new IOException("Injected task creation failure.");
            }
            if (expected is not null)
                Removed.Add(expected);
            TaskXml = replacement is null ? null : WindowsStartupTaskStore.Xml(replacement);
            Writes++;
            DuplicateObserved |= Entry is not null && TaskXml is not null;
            return true;
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
            Task.FromResult(new StartupInspection(true, [], []));
    }
}
