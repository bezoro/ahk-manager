using System.Xml.Linq;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupRelocationChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string previousHost = Path.Combine(root, "previous-install", "Bezoro.AhkManager.exe");
        string currentHost = Path.Combine(root, "current-install", "Bezoro.AhkManager.exe");
        var adapter = new Adapter();
        var previous = new UserStartupService(adapter, previousHost);
        var current = new UserStartupService(adapter, currentHost);
        var original = WindowsStartupTaskStore.Parse(
            WindowsStartupTaskStore.Xml(
                new(
                    previous.Command,
                    adapter.UserSid,
                    true,
                    true,
                    true,
                    true,
                    UserStartupService.TaskSource
                )
            )
        );
        adapter.Task = original;
        check(
            current.RegistrationStatus(true, true) == ReasonCode.StartupInactive
                && adapter.Writes == 0
                && !UserStartupService.OwnsTask(original, current.Command, adapter.UserSid),
            "a proven previous Manager task needs relocation without widening exact-path uninstall ownership"
        );
        check(
            (await current.SetEnabledAsync(true, false, true)).Code
                == ReasonCode.StartupConsentRequired
                && adapter.Task == original
                && adapter.Writes == 0,
            "relocating an owned task retains startup consent requirements"
        );
        check(
            (await current.SetEnabledAsync(true, true, true)).Succeeded
                && adapter.Task?.Command == current.Command
                && adapter.Entry is null
                && adapter.Expected.Single() == original
                && !adapter.DuplicateObserved
                && current.RegistrationStatus(true, true) == ReasonCode.None,
            "relocation replaces an already enabled highest task using its complete observed fingerprint"
        );
        int writes = adapter.Writes;
        check(
            (await current.SetEnabledAsync(true, false, true)).Succeeded
                && adapter.Writes == writes,
            "the relocated registration is idempotent"
        );

        adapter.Reset(original);
        adapter.FailNextTaskCreation = true;
        var failed = await current.SetEnabledAsync(true, true, true);
        check(
            failed.Outcome == Outcome.Failed
                && failed.Code == ReasonCode.StorageFailure
                && adapter.Task == original
                && adapter.Entry is null
                && failed.Detail?.Contains("previous registration was preserved") == true,
            "failed relocation restores the original path and complete task observation"
        );

        adapter.Reset(original);
        adapter.ReplaceBeforeRemoval = original with { Source = "another product" };
        var removalRace = await current.SetEnabledAsync(true, true, true);
        check(
            removalRace.Code == ReasonCode.StartupEntryConflict
                && adapter.Task?.Source == "another product"
                && adapter.Writes == 0,
            "relocation preserves a task whose observed identity changes before removal"
        );
        adapter.Reset(original);
        adapter.ReplaceBeforeCreation = original with { Source = "another product" };
        var creationRace = await current.SetEnabledAsync(true, true, true);
        check(
            creationRace.Outcome == Outcome.Partial
                && creationRace.Code == ReasonCode.StartupEntryConflict
                && adapter.Task?.Source == "another product"
                && adapter.Entry is null,
            "a concurrently supplied task is never overwritten or obscured by rollback"
        );

        foreach (
            var foreign in new[]
            {
                original with
                {
                    Source = "another product",
                },
                original with
                {
                    UserSid = "S-1-5-21-other",
                },
                original with
                {
                    InteractiveToken = false,
                },
                original with
                {
                    UserLogonOnly = false,
                },
                original with
                {
                    Command = previous.Command + " --extra",
                },
                original with
                {
                    Command = previous.Command.Replace("--background", "--foreground"),
                },
                original with
                {
                    Command =
                        ScriptPaths.QuoteArgument(Path.Combine(root, "other.exe"))
                        + " --login --background",
                },
                original with
                {
                    Command = "\"relative\\Bezoro.AhkManager.exe\" --login --background",
                },
                original with
                {
                    Command = "\"\\\\server\\share\\Bezoro.AhkManager.exe\" --login --background",
                },
                original with
                {
                    Command =
                        ScriptPaths.QuoteArgument(
                            Path.Combine(root, "previous-install", "..", "Bezoro.AhkManager.exe")
                        ) + " --login --background",
                },
            }
        )
        {
            adapter.Reset(foreign);
            check(
                current.RegistrationStatus(true, true) == ReasonCode.StartupEntryConflict
                    && (await current.SetEnabledAsync(true, true, true)).Code
                        == ReasonCode.StartupEntryConflict
                    && (await current.SetEnabledAsync(false, true, true)).Code
                        == ReasonCode.StartupEntryConflict
                    && adapter.Task == foreign
                    && adapter.Writes == 0,
                "relocation refuses foreign source user trigger arguments executable or unverifiable paths"
            );
        }
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var unnormalized = XDocument.Parse(WindowsStartupTaskStore.Xml(original));
        string ambiguousPath = Path.Combine(
            root,
            "previous-install",
            "..",
            "Bezoro.AhkManager.exe"
        );
        var execution = unnormalized.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!;
        execution.Element(ns + "Command")!.Value = ambiguousPath;
        execution.Element(ns + "WorkingDirectory")!.Value = Path.GetDirectoryName(ambiguousPath)!;
        check(
            WindowsStartupTaskStore.Parse(unnormalized.ToString()).Command.Length == 0,
            "task inspection preserves uncertainty rather than normalizing an ambiguous action into ownership"
        );
        foreach (string element in new[] { "Actions", "Triggers" })
        {
            var xml = XDocument.Parse(WindowsStartupTaskStore.Xml(original));
            xml.Root!.Element(ns + element)!
                .Add(new XElement(ns + (element == "Actions" ? "Exec" : "BootTrigger")));
            var foreign = WindowsStartupTaskStore.Parse(xml.ToString());
            adapter.Reset(foreign);
            check(
                (await current.SetEnabledAsync(true, true, true)).Code
                    == ReasonCode.StartupEntryConflict
                    && adapter.Task == foreign
                    && adapter.Writes == 0,
                "extra task actions or triggers cannot establish Manager relocation ownership"
            );
        }
        var otherHost = new UserStartupService(adapter, Path.Combine(root, "other.exe"));
        adapter.Reset(original);
        check(
            (await otherHost.SetEnabledAsync(true, true, true)).Code
                == ReasonCode.StartupEntryConflict
                && adapter.Writes == 0,
            "a different current host cannot claim a previous Manager task"
        );

        adapter.Reset(null);
        adapter.Entry = previous.Command;
        check(
            (await current.SetEnabledAsync(true, true, false)).Code
                == ReasonCode.StartupEntryConflict
                && adapter.Entry == previous.Command
                && adapter.Writes == 0,
            "an old Run command alone has no ownership proof and remains untouched"
        );
        adapter.Reset(original);
        adapter.Entry = previous.Command;
        check(
            (await current.SetEnabledAsync(true, true, false)).Succeeded
                && adapter.Entry == current.Command
                && adapter.Task is null,
            "a proven product task can corroborate only its exact old Run command during relocation"
        );
        foreach (bool elevated in new[] { false, true })
        {
            adapter.Reset(original);
            adapter.Entry = previous.Command;
            adapter.FailNextTaskCreation = elevated;
            adapter.FailNextEntryCreation = !elevated;
            var dualFailure = await current.SetEnabledAsync(true, true, elevated);
            check(
                dualFailure.Outcome == Outcome.Failed
                    && dualFailure.Code == ReasonCode.StorageFailure
                    && adapter.Entry == previous.Command
                    && adapter.Task == original,
                "failed relocation restores both corroborated original registrations when their slots remain available"
            );
        }
        adapter.Reset(original);
        adapter.Entry = previous.Command;
        adapter.FailNextTaskCreation = true;
        adapter.ReplaceBeforeEntryCreation = "another product command";
        var entryRestoreRace = await current.SetEnabledAsync(true, true, true);
        check(
            entryRestoreRace.Outcome == Outcome.Partial
                && adapter.Entry == "another product command"
                && adapter.Task == original,
            "dual rollback preserves a concurrent Run entry and leaves the observed task available for recovery"
        );
        var changedXml = XDocument.Parse(WindowsStartupTaskStore.Xml(original));
        changedXml
            .Root!.Element(ns + "RegistrationInfo")!
            .Add(new XElement(ns + "Description", "Concurrent task definition"));
        var changedTask = WindowsStartupTaskStore.Parse(changedXml.ToString());
        check(
            changedTask.Fingerprint != original.Fingerprint
                && changedTask with { Fingerprint = original.Fingerprint } == original,
            "concurrent XML changes can retain the same projected startup definition"
        );
        foreach (bool throws in new[] { false, true })
        {
            adapter.Reset(original);
            adapter.Entry = previous.Command;
            adapter.FailNextTaskCreation = true;
            adapter.ReplaceAfterCreationBeforeRead = changedTask;
            adapter.FailNextEntryCreation = throws;
            if (!throws)
                adapter.ReplaceBeforeEntryCreation = "another product command";
            var changedTaskRace = await current.SetEnabledAsync(true, true, true);
            check(
                changedTaskRace.Outcome == Outcome.Partial
                    && adapter.Task == changedTask
                    && adapter.Expected.Count == 1
                    && adapter.Expected[0] == original,
                "failed Run rollback never deletes task XML replaced between creation and observation"
            );
        }
        adapter.Reset(original);
        adapter.Entry = previous.Command;
        adapter.FailNextEntryCreation = true;
        adapter.ReplaceBeforeCreation = original with { Source = "another product" };
        var taskRestoreRace = await current.SetEnabledAsync(true, true, false);
        check(
            taskRestoreRace.Outcome == Outcome.Partial
                && adapter.Entry is null
                && adapter.Task?.Source == "another product",
            "dual rollback preserves a concurrent task without restoring an additional Run entry"
        );
        adapter.Reset(original);
        adapter.Entry = previous.Command + " --extra";
        check(
            (await current.SetEnabledAsync(true, true, true)).Code
                == ReasonCode.StartupEntryConflict
                && adapter.Task == original
                && adapter.Writes == 0,
            "a mismatched Run command cannot borrow ownership from a genuine Manager task"
        );
        adapter.Reset(original);
        check(
            (await current.SetEnabledAsync(false, false, true)).Succeeded
                && adapter.Task is null
                && adapter.Entry is null,
            "a saved disabled preference removes the proven old registration without enabling startup"
        );
    }

    sealed class Adapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string UserSid => "S-1-5-21-relocation-fixture";
        public string? Entry;
        public StartupTaskRegistration? Task;
        public StartupTaskRegistration? ReplaceBeforeRemoval,
            ReplaceBeforeCreation,
            ReplaceAfterCreationBeforeRead;
        StartupTaskRegistration? pendingTaskRead;
        public string? ReplaceBeforeEntryCreation;
        public readonly List<StartupTaskRegistration> Expected = [];
        public bool FailNextTaskCreation,
            FailNextEntryCreation,
            DuplicateObserved;
        public int Writes;

        public void Reset(StartupTaskRegistration? task)
        {
            Task = task;
            Entry = null;
            ReplaceBeforeRemoval = ReplaceBeforeCreation = ReplaceAfterCreationBeforeRead = null;
            pendingTaskRead = null;
            ReplaceBeforeEntryCreation = null;
            FailNextTaskCreation = FailNextEntryCreation = DuplicateObserved = false;
            Expected.Clear();
            Writes = 0;
        }

        public string? ReadEntry() => Entry;

        public StartupTaskRegistration? ReadTask()
        {
            if (pendingTaskRead is { } replacement)
            {
                Task = replacement;
                pendingTaskRead = null;
            }
            return Task;
        }

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            if (replacement is not null)
            {
                if (ReplaceBeforeEntryCreation is { } other)
                {
                    Entry = other;
                    ReplaceBeforeEntryCreation = null;
                }
                if (FailNextEntryCreation)
                {
                    FailNextEntryCreation = false;
                    throw new IOException("Injected Run registration failure.");
                }
            }
            if (Entry != expected)
                return false;
            Entry = replacement;
            Writes++;
            DuplicateObserved |= Entry is not null && Task is not null;
            return true;
        }

        public bool CompareExchangeTask(
            StartupTaskRegistration? expected,
            StartupTaskRegistration? replacement
        )
        {
            if (expected is not null)
            {
                Expected.Add(expected);
                if (ReplaceBeforeRemoval is { } other)
                {
                    Task = other;
                    ReplaceBeforeRemoval = null;
                }
            }
            if (replacement is not null)
            {
                if (ReplaceBeforeCreation is { } other)
                {
                    Task = other;
                    ReplaceBeforeCreation = null;
                }
                if (FailNextTaskCreation)
                {
                    FailNextTaskCreation = false;
                    throw new IOException("Injected task registration failure.");
                }
            }
            if (Task != expected)
                return false;
            Task = replacement;
            if (expected is null && replacement is not null)
            {
                pendingTaskRead = ReplaceAfterCreationBeforeRead;
                ReplaceAfterCreationBeforeRead = null;
            }
            Writes++;
            DuplicateObserved |= Entry is not null && Task is not null;
            return true;
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new StartupInspection(true, [], []));
    }
}
