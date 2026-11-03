using System.Collections.Immutable;
using System.Xml.Linq;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupLegacyRepairChecks
{
    const string Sid = "S-1-5-21-100-200-300-1001";
    const string OtherSid = "S-1-5-21-100-200-300-1002";
    const string PreviousExecutable = @"V:\Repos\Bezoro.AhkManager\publish\Bezoro.AhkManager.exe";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var fixture = Create(root);
        var task = fixture.Adapter.Task!;
        var preview = fixture.Service.PreviewLegacyRunRepair(true, true);
        check(
            fixture.Service.RegistrationStatus(true, true) == ReasonCode.StartupEntryConflict
                && (await fixture.Service.SetEnabledAsync(true, true, true)).Code
                    == ReasonCode.StartupEntryConflict
                && (await fixture.Service.SetEnabledAsync(false, true, false)).Code
                    == ReasonCode.StartupEntryConflict
                && fixture.Adapter.EntryWrites == 0
                && fixture.Adapter.TaskWrites == 0,
            "a current task does not silently adopt or delete a previous Run entry while saving preferences"
        );
        check(
            preview.CanRepair
                && preview.PreviousExecutable == PreviousExecutable
                && preview.CurrentExecutable == fixture.Executable
                && preview.Fingerprint is { Length: 64 }
                && fixture.Adapter.Task == task
                && fixture.Adapter.Entry == fixture.PreviousCommand,
            "the actual portable task plus old publish Run scenario produces a read-only explicit review"
        );

        var backups = new List<StartupRepairBackup>();
        Task<OperationResult> Backup(StartupRepairBackup value, CancellationToken _)
        {
            backups.Add(value);
            fixture.Adapter.Order.Add("backup");
            return Task.FromResult(new OperationResult(Outcome.Confirmed));
        }
        var unconfirmed = await fixture.Service.RepairLegacyRunEntryAsync(
            preview.Fingerprint!,
            false,
            true,
            true,
            Backup
        );
        check(
            unconfirmed.Code == ReasonCode.StartupConsentRequired
                && backups.Count == 0
                && fixture.Adapter.EntryWrites == 0
                && fixture.Adapter.TaskWrites == 0,
            "a repair preview never substitutes for the user's explicit removal approval"
        );
        foreach (
            string? fingerprint in new[] { "", new string('a', 63), new string('G', 64), null }
        )
        {
            var invalid = await fixture.Service.RepairLegacyRunEntryAsync(
                fingerprint!,
                true,
                true,
                true,
                Backup
            );
            check(
                invalid.Code == ReasonCode.StaleSelection
                    && backups.Count == 0
                    && fixture.Adapter.EntryWrites == 0,
                "malformed legacy repair fingerprints cannot dispatch registry changes"
            );
        }
        var repaired = await fixture.Service.RepairLegacyRunEntryAsync(
            preview.Fingerprint!,
            true,
            true,
            true,
            Backup
        );
        check(
            repaired.Succeeded
                && fixture.Adapter.Entry is null
                && fixture.Adapter.Task == task
                && fixture.Adapter.EntryWrites == 1
                && fixture.Adapter.TaskWrites == 0
                && fixture.Adapter.Order.SequenceEqual(["backup", "entry-removal"]),
            "explicit legacy repair durably backs up then removes only the exact reviewed Run value"
        );
        var recorded = backups.Single();
        check(
            recorded.Timestamp.Offset == TimeSpan.Zero
                && recorded.UserSid == Sid
                && recorded.PreviousRunEntry == fixture.PreviousCommand
                && recorded.CurrentCommand == fixture.Service.Command
                && recorded.TaskFingerprint == task.Fingerprint
                && recorded.LaunchManagerAtLogin
                && recorded.LaunchManagerAsAdministrator
                && fixture.Service.RegistrationStatus(true, true) == ReasonCode.None,
            "the pre-removal backup binds complete original Run text task fingerprint account command and saved startup mode"
        );
        var replay = await fixture.Service.RepairLegacyRunEntryAsync(
            preview.Fingerprint!,
            true,
            true,
            true,
            Backup
        );
        check(
            replay.Code == ReasonCode.StaleSelection
                && backups.Count == 1
                && fixture.Adapter.EntryWrites == 1
                && fixture.Adapter.TaskWrites == 0
                && !fixture.Service.PreviewLegacyRunRepair(true, true).CanRepair,
            "a consumed repair cannot replay against the now absent Run value"
        );

        await CheckForeignEntries(root, check);
        await CheckStalePreviews(root, check);
        await CheckBackupAndDispatchFailures(root, check);
    }

    static async Task CheckForeignEntries(string root, Action<bool, string> check)
    {
        var fixture = Create(root);
        string approved = fixture.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
        foreach (
            string entry in new[]
            {
                "<non-string>",
                "foreign command",
                fixture.PreviousCommand + " --extra",
                "\"relative\\Bezoro.AhkManager.exe\" --login --background",
                "\"\\\\server\\share\\Bezoro.AhkManager.exe\" --login --background",
                "\"V:\\Repos\\old\\..\\publish\\Bezoro.AhkManager.exe\" --login --background",
                "\"V:\\Repos\\Other.exe\" --login --background",
                "\"V:\\%USERNAME%\\Bezoro.AhkManager.exe\" --login --background",
            }
        )
        {
            fixture = Create(root);
            fixture.Adapter.Entry = entry;
            int backups = 0;
            var preview = fixture.Service.PreviewLegacyRunRepair(true, true);
            var refused = await fixture.Service.RepairLegacyRunEntryAsync(
                approved,
                true,
                true,
                true,
                (_, _) =>
                {
                    backups++;
                    return Task.FromResult(new OperationResult(Outcome.Confirmed));
                }
            );
            check(
                !preview.CanRepair
                    && refused.Code == ReasonCode.StaleSelection
                    && fixture.Adapter.Entry == entry
                    && fixture.Adapter.EntryWrites == 0
                    && fixture.Adapter.TaskWrites == 0
                    && backups == 0,
                "unknown nonstandard relative network variable and extra-argument Run entries remain untouched"
            );
        }
        fixture = Create(root);
        var original = fixture.Adapter.Task!;
        foreach (
            StartupTaskRegistration? task in new StartupTaskRegistration?[]
            {
                null,
                original with
                {
                    Source = "other product",
                },
                original with
                {
                    UserSid = OtherSid,
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
                    Enabled = false,
                },
                original with
                {
                    HighestPrivileges = false,
                },
                original with
                {
                    NonstandardWorkingDirectory = "",
                },
                original with
                {
                    ParseIssue = StartupTaskParseIssue.XmlShape,
                },
                original with
                {
                    Fingerprint = "",
                },
                original with
                {
                    Command = fixture.PreviousCommand,
                },
                original with
                {
                    Command = fixture.Service.Command + " --extra",
                },
            }
        )
        {
            fixture = Create(root);
            fixture.Adapter.Task = task;
            int backups = 0;
            var preview = fixture.Service.PreviewLegacyRunRepair(true, true);
            var refused = await fixture.Service.RepairLegacyRunEntryAsync(
                approved,
                true,
                true,
                true,
                (_, _) =>
                {
                    backups++;
                    return Task.FromResult(new OperationResult(Outcome.Confirmed));
                }
            );
            check(
                !preview.CanRepair
                    && refused.Code == ReasonCode.StaleSelection
                    && fixture.Adapter.Task == task
                    && fixture.Adapter.Entry == fixture.PreviousCommand
                    && fixture.Adapter.EntryWrites == 0
                    && fixture.Adapter.TaskWrites == 0
                    && backups == 0,
                "legacy Run removal requires a fully proven healthy current-host task and complete XML fingerprint"
            );
        }

        fixture = Create(root);
        fixture.Adapter.DenyTaskRead = true;
        var denied = fixture.Service.PreviewLegacyRunRepair(true, true);
        check(
            !denied.CanRepair
                && denied.Reason == ReasonCode.AccessDenied
                && fixture.Adapter.EntryWrites == 0
                && fixture.Adapter.TaskWrites == 0,
            "an unreadable Scheduler task never authorizes a previous Run removal"
        );
    }

    static async Task CheckStalePreviews(string root, Action<bool, string> check)
    {
        foreach (string change in new[] { "run", "task-xml", "account", "enabled", "elevated" })
        {
            var fixture = Create(root);
            string token = fixture.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
            bool enabled = true,
                elevated = true;
            switch (change)
            {
                case "run":
                    fixture.Adapter.Entry = fixture.PreviousCommand.Replace(
                        " --login",
                        "  --login",
                        StringComparison.Ordinal
                    );
                    break;
                case "task-xml":
                    var document = XDocument.Parse(
                        WindowsStartupTaskStore.Xml(fixture.Adapter.Task!)
                    );
                    document
                        .Root!.Element(Ns + "RegistrationInfo")!
                        .Add(new XElement(Ns + "Description", "Concurrent XML edit"));
                    fixture.Adapter.Task = WindowsStartupTaskStore.Parse(document.ToString());
                    break;
                case "account":
                    fixture.Adapter.Sid = OtherSid;
                    break;
                case "enabled":
                    enabled = false;
                    break;
                case "elevated":
                    elevated = false;
                    break;
            }
            string? entry = fixture.Adapter.Entry;
            var task = fixture.Adapter.Task;
            int backups = 0;
            var refused = await fixture.Service.RepairLegacyRunEntryAsync(
                token,
                true,
                enabled,
                elevated,
                (_, _) =>
                {
                    backups++;
                    return Task.FromResult(new OperationResult(Outcome.Confirmed));
                }
            );
            check(
                refused.Code == ReasonCode.StaleSelection
                    && backups == 0
                    && fixture.Adapter.EntryWrites == 0
                    && fixture.Adapter.TaskWrites == 0
                    && fixture.Adapter.Entry == entry
                    && fixture.Adapter.Task == task,
                "repair consent is bound to exact Run bytes full task XML current account and saved preference mode"
            );
        }

        foreach (bool replaceTask in new[] { false, true })
        {
            var fixture = Create(root);
            string token = fixture.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
            var concurrent = fixture.Adapter.Task! with { Fingerprint = new string('b', 64) };
            var result = await fixture.Service.RepairLegacyRunEntryAsync(
                token,
                true,
                true,
                true,
                (_, _) =>
                {
                    if (replaceTask)
                        fixture.Adapter.Task = concurrent;
                    else
                        fixture.Adapter.Entry = "concurrent foreign entry";
                    return Task.FromResult(new OperationResult(Outcome.Confirmed));
                }
            );
            check(
                result.Code == ReasonCode.StaleSelection
                    && fixture.Adapter.EntryWrites == 0
                    && fixture.Adapter.TaskWrites == 0
                    && (
                        replaceTask
                            ? fixture.Adapter.Task == concurrent
                            : fixture.Adapter.Entry == "concurrent foreign entry"
                    ),
                "a Run or task change while saving the backup invalidates consent before deletion"
            );
        }

        foreach (string race in new[] { "entry-before", "task-before", "entry-after" })
        {
            var fixture = Create(root);
            string token = fixture.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
            var original = fixture.Adapter.Task!;
            var concurrent = original with { Source = "concurrent product" };
            if (race == "entry-after")
                fixture.Adapter.AfterEntryWrite = () =>
                    fixture.Adapter.Entry = "concurrent foreign entry";
            else
                fixture.Adapter.BeforeEntryWrite = () =>
                {
                    if (race == "entry-before")
                        fixture.Adapter.Entry = "concurrent foreign entry";
                    else
                        fixture.Adapter.Task = concurrent;
                };
            var result = await fixture.Service.RepairLegacyRunEntryAsync(
                token,
                true,
                true,
                true,
                (_, _) => Task.FromResult(new OperationResult(Outcome.Confirmed))
            );
            check(
                result.Outcome == (race == "entry-before" ? Outcome.Failed : Outcome.Partial)
                    && (
                        race == "task-before"
                            ? fixture.Adapter.Task == concurrent && fixture.Adapter.Entry is null
                            : fixture.Adapter.Entry == "concurrent foreign entry"
                    )
                    && fixture.Adapter.TaskWrites == 0
                    && fixture.Adapter.EntryWrites == (race == "entry-before" ? 0 : 1),
                "concurrent entry or task replacements survive repair without a duplicate rollback registration"
            );
        }
    }

    static async Task CheckBackupAndDispatchFailures(string root, Action<bool, string> check)
    {
        foreach (bool throws in new[] { false, true })
        {
            var fixture = Create(root);
            string token = fixture.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
            var result = await fixture.Service.RepairLegacyRunEntryAsync(
                token,
                true,
                true,
                true,
                (_, _) =>
                    throws
                        ? throw new IOException("do-not-copy-this-secret")
                        : Task.FromResult(
                            new OperationResult(Outcome.Failed, ReasonCode.StorageFailure, "secret")
                        )
            );
            check(
                result.Outcome == Outcome.Failed
                    && result.Code == ReasonCode.StorageFailure
                    && result.Detail?.Contains("secret", StringComparison.Ordinal) != true
                    && fixture.Adapter.Entry == fixture.PreviousCommand
                    && fixture.Adapter.EntryWrites == 0
                    && fixture.Adapter.TaskWrites == 0,
                "an unconfirmed or failed durable backup prevents removal and excludes raw callback errors"
            );
        }

        foreach (bool cancelDuringBackup in new[] { false, true })
        {
            var fixture = Create(root);
            string token = fixture.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
            using var cancellation = new CancellationTokenSource();
            int backups = 0;
            if (!cancelDuringBackup)
                cancellation.Cancel();
            var result = await fixture.Service.RepairLegacyRunEntryAsync(
                token,
                true,
                true,
                true,
                (_, _) =>
                {
                    backups++;
                    cancellation.Cancel();
                    return Task.FromResult(new OperationResult(Outcome.Confirmed));
                },
                cancellation.Token
            );
            check(
                result.Outcome == Outcome.CancelledBeforeDispatch
                    && result.Code == ReasonCode.Cancelled
                    && backups == (cancelDuringBackup ? 1 : 0)
                    && fixture.Adapter.EntryWrites == 0
                    && fixture.Adapter.TaskWrites == 0,
                "cancellation before registry dispatch leaves the reviewed Run value and task unchanged"
            );
        }

        foreach (bool afterRemoval in new[] { false, true })
        {
            var fixture = Create(root);
            string token = fixture.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
            fixture.Adapter.ThrowBeforeEntryWrite = !afterRemoval;
            fixture.Adapter.ThrowAfterEntryWrite = afterRemoval;
            var original = fixture.Adapter.Task;
            var result = await fixture.Service.RepairLegacyRunEntryAsync(
                token,
                true,
                true,
                true,
                (_, _) => Task.FromResult(new OperationResult(Outcome.Confirmed))
            );
            check(
                result.Outcome == (afterRemoval ? Outcome.Partial : Outcome.Unknown)
                    && result.Code == ReasonCode.AccessDenied
                    && fixture.Adapter.Task == original
                    && fixture.Adapter.TaskWrites == 0
                    && fixture.Adapter.Entry == (afterRemoval ? null : fixture.PreviousCommand)
                    && fixture.Adapter.EntryWrites == (afterRemoval ? 1 : 0),
                "registry dispatch failures observe actual state and never claim an applied deletion was preserved"
            );
        }

        var unreadable = Create(root);
        string approved = unreadable.Service.PreviewLegacyRunRepair(true, true).Fingerprint!;
        unreadable.Adapter.AfterEntryWrite = () => unreadable.Adapter.DenyTaskRead = true;
        var unknown = await unreadable.Service.RepairLegacyRunEntryAsync(
            approved,
            true,
            true,
            true,
            (_, _) => Task.FromResult(new OperationResult(Outcome.Confirmed))
        );
        check(
            unknown.Outcome == Outcome.Partial
                && unreadable.Adapter.Entry is null
                && unreadable.Adapter.EntryWrites == 1
                && unreadable.Adapter.TaskWrites == 0,
            "a post-removal Scheduler read failure reports partial state without restoring a duplicate entry"
        );
    }

    static Fixture Create(string root)
    {
        var adapter = new Adapter();
        string executable = Path.Combine(
            root,
            "OneDrive",
            "05 Apps and Tools",
            "AHK Manager Portable",
            "Bezoro.AhkManager.exe"
        );
        var service = new UserStartupService(adapter, executable);
        var specification = new StartupTaskRegistration(
            service.Command,
            Sid,
            true,
            true,
            true,
            true,
            UserStartupService.TaskSource
        );
        adapter.Task = WindowsStartupTaskStore.Parse(WindowsStartupTaskStore.Xml(specification));
        string previous = ScriptPaths.QuoteArgument(PreviousExecutable) + " --login --background";
        adapter.Entry = previous;
        return new(adapter, service, executable, previous);
    }

    sealed record Fixture(
        Adapter Adapter,
        UserStartupService Service,
        string Executable,
        string PreviousCommand
    );

    sealed class Adapter : IUserStartupAdapter, IUserStartupTaskAdapter
    {
        public string Sid = StartupLegacyRepairChecks.Sid;
        public string UserSid => Sid;
        public string? Entry;
        public StartupTaskRegistration? Task;
        public int EntryWrites,
            TaskWrites;
        public bool DenyTaskRead,
            ThrowBeforeEntryWrite,
            ThrowAfterEntryWrite;
        public Action? BeforeEntryWrite,
            AfterEntryWrite;
        public readonly List<string> Order = [];

        public string? ReadEntry() => Entry;

        public StartupTaskRegistration? ReadTask() =>
            DenyTaskRead ? throw new UnauthorizedAccessException() : Task;

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            BeforeEntryWrite?.Invoke();
            BeforeEntryWrite = null;
            if (ThrowBeforeEntryWrite)
                throw new UnauthorizedAccessException("untrusted Windows error");
            if (Entry != expected)
                return false;
            Entry = replacement;
            EntryWrites++;
            Order.Add("entry-removal");
            AfterEntryWrite?.Invoke();
            AfterEntryWrite = null;
            if (ThrowAfterEntryWrite)
                throw new UnauthorizedAccessException("untrusted Windows error");
            return true;
        }

        public bool CompareExchangeTask(
            StartupTaskRegistration? expected,
            StartupTaskRegistration? replacement
        )
        {
            TaskWrites++;
            return false;
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new StartupInspection(true, [], []));
    }
}
