using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class UserStartupService
{
    sealed record LegacyRepairState(
        string Entry,
        StartupTaskRegistration Task,
        string UserSid,
        string PreviousExecutable,
        string CurrentExecutable,
        string Fingerprint
    );

    public StartupRepairPreview PreviewLegacyRunRepair(bool enabled, bool elevated)
    {
        try
        {
            var state = ReadLegacyRepairState(enabled, elevated, out var reason, out var detail);
            return state is null
                ? new(false, null, null, null, reason, detail)
                : new(
                    true,
                    state.PreviousExecutable,
                    state.CurrentExecutable,
                    state.Fingerprint,
                    ReasonCode.None,
                    "Review the previous Run entry before removing it. The verified current Manager task will be preserved."
                );
        }
        catch (Exception error) when (RegistrationException(error))
        {
            return new(
                false,
                null,
                null,
                null,
                RepairDenied(error) ? ReasonCode.AccessDenied : ReasonCode.StorageFailure,
                "Windows startup could not be inspected. Existing entries were preserved."
            );
        }
    }

    public Task<OperationResult> RepairLegacyRunEntryAsync(
        string fingerprint,
        bool confirmed,
        bool enabled,
        bool elevated,
        Func<StartupRepairBackup, CancellationToken, Task<OperationResult>> backup,
        CancellationToken ct = default
    ) =>
        Task.Run(
            async () =>
            {
                bool removed = false;
                bool removalDispatched = false;
                LegacyRepairState? original = null;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (!confirmed)
                        return new OperationResult(
                            Outcome.Failed,
                            ReasonCode.StartupConsentRequired,
                            "Review and explicitly approve the previous startup entry before removal."
                        );
                    if (!RepairFingerprint(fingerprint))
                        return StaleLegacyRepair();

                    original = ReadLegacyRepairState(enabled, elevated, out _, out _);
                    if (
                        original is null
                        || !SameRepairFingerprint(fingerprint, original.Fingerprint)
                    )
                        return StaleLegacyRepair();

                    var saved = await backup(
                            new(
                                DateTimeOffset.UtcNow,
                                original.UserSid,
                                original.Entry,
                                Command,
                                original.Task.Fingerprint,
                                enabled,
                                elevated
                            ),
                            ct
                        )
                        .ConfigureAwait(false);
                    if (!saved.Succeeded)
                        return saved.Code == ReasonCode.Cancelled
                            ? new(
                                Outcome.CancelledBeforeDispatch,
                                ReasonCode.Cancelled,
                                "The startup repair was cancelled before changing Windows."
                            )
                            : new(
                                Outcome.Failed,
                                saved.Code == ReasonCode.None
                                    ? ReasonCode.StorageFailure
                                    : saved.Code,
                                "The startup repair backup could not be confirmed. Existing entries were preserved."
                            );

                    ct.ThrowIfCancellationRequested();
                    var current = ReadLegacyRepairState(enabled, elevated, out _, out _);
                    if (current is null || !SameRepairFingerprint(fingerprint, current.Fingerprint))
                        return StaleLegacyRepair();

                    // The exact reviewed Run value is the only mutable resource here.
                    // There is no create/update/delete operation on the Scheduler task.
                    removalDispatched = true;
                    if (!adapter.CompareExchangeEntry(original.Entry, null))
                        return StaleLegacyRepair();
                    removed = true;

                    var tasks = (IUserStartupTaskAdapter)adapter;
                    if (
                        adapter.ReadEntry() is not null
                        || tasks.UserSid != original.UserSid
                        || tasks.ReadTask() != original.Task
                    )
                        return new(
                            Outcome.Partial,
                            ReasonCode.StartupEntryConflict,
                            "The reviewed Run entry was removed, but Windows startup changed concurrently. The current registration was preserved and needs review."
                        );
                    return new(
                        Outcome.Confirmed,
                        ReasonCode.None,
                        "The reviewed previous Run entry was removed. The current Manager task was preserved."
                    );
                }
                catch (OperationCanceledException)
                {
                    return removalDispatched
                        ? new(
                            removed ? Outcome.Partial : Outcome.Unknown,
                            ReasonCode.Cancelled,
                            "The startup repair was cancelled after removal was dispatched. The final registration needs review."
                        )
                        : new(
                            Outcome.CancelledBeforeDispatch,
                            ReasonCode.Cancelled,
                            "The startup repair was cancelled before changing Windows."
                        );
                }
                catch (Exception error) when (RegistrationException(error))
                {
                    var observation = removalDispatched
                        ? ObserveLegacyRepairFailure(original)
                        : (Known: true, Unchanged: true);
                    return new(
                        !removalDispatched ? Outcome.Failed
                            : removed || observation.Known && !observation.Unchanged
                                ? Outcome.Partial
                            : Outcome.Unknown,
                        RepairDenied(error) ? ReasonCode.AccessDenied : ReasonCode.StorageFailure,
                        !removalDispatched
                                ? "Windows could not complete the startup repair. The previous registration was preserved."
                            : observation.Unchanged
                                ? "Windows reported an error after removal was dispatched. The current Run value still matches the reviewed entry; review startup before trying again."
                            : "Windows reported an error after startup removal was dispatched. The final registration needs review; no entries were restored over concurrent changes."
                    );
                }
            },
            CancellationToken.None
        );

    LegacyRepairState? ReadLegacyRepairState(
        bool enabled,
        bool elevated,
        out ReasonCode reason,
        out string? detail
    )
    {
        reason = ReasonCode.None;
        detail = null;
        string? entry = adapter.ReadEntry();
        if (entry is null || entry == Command)
            return null;
        if (
            adapter is not IUserStartupTaskAdapter tasks
            || !StandardManagerCommand(entry, out string previous)
            || !StandardManagerCommand(Command, out string current)
        )
        {
            reason = ReasonCode.StartupEntryConflict;
            detail =
                "The Run entry cannot be verified as a standard Manager command. It was preserved.";
            return null;
        }
        string sid = tasks.UserSid;
        var task = tasks.ReadTask();
        if (
            task is null
            || !OwnsTask(task, Command, sid)
            || !Desired(task, tasks)
            || !RepairFingerprint(task.Fingerprint)
        )
        {
            reason = ReasonCode.StartupEntryConflict;
            detail =
                "A verified current Manager task is required before reviewing a previous Run entry. Existing entries were preserved.";
            return null;
        }
        return new(
            entry,
            task,
            sid,
            previous,
            current,
            LegacyRepairFingerprint(entry, task, sid, enabled, elevated)
        );
    }

    (bool Known, bool Unchanged) ObserveLegacyRepairFailure(LegacyRepairState? original)
    {
        if (original is null || adapter is not IUserStartupTaskAdapter tasks)
            return (false, false);
        try
        {
            return (
                true,
                adapter.ReadEntry() == original.Entry
                    && tasks.UserSid == original.UserSid
                    && tasks.ReadTask() == original.Task
            );
        }
        catch (Exception error) when (RegistrationException(error))
        {
            return (false, false);
        }
    }

    static bool StandardManagerCommand(string command, out string executable)
    {
        executable = "";
        if (command.Contains('%'))
            return false;
        var arguments = ScriptPaths.SplitCommand(command);
        if (arguments.Length != 3 || arguments[1] != "--login" || arguments[2] != "--background")
            return false;
        try
        {
            string canonical = ScriptPaths.LocalPath(arguments[0]);
            if (
                !ScriptPaths.Equal(arguments[0], canonical)
                || !Path.GetFileName(canonical)
                    .Equals("Bezoro.AhkManager.exe", StringComparison.OrdinalIgnoreCase)
            )
                return false;
            executable = canonical;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    string LegacyRepairFingerprint(
        string entry,
        StartupTaskRegistration task,
        string sid,
        bool enabled,
        bool elevated
    )
    {
        using var bytes = new MemoryStream();
        // Length-prefixed strict UTF-8 prevents delimiter and invalid-text collisions.
        using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), true))
        {
            writer.Write(1);
            writer.Write(sid);
            writer.Write(Command);
            writer.Write(entry);
            writer.Write(task.Command);
            writer.Write(task.UserSid);
            writer.Write(task.Enabled);
            writer.Write(task.HighestPrivileges);
            writer.Write(task.InteractiveToken);
            writer.Write(task.UserLogonOnly);
            writer.Write(task.Source);
            writer.Write(task.Fingerprint);
            writer.Write(task.NonstandardWorkingDirectory is not null);
            writer.Write(task.NonstandardWorkingDirectory ?? "");
            writer.Write((int)task.ParseIssue);
            writer.Write(enabled);
            writer.Write(elevated);
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    static bool RepairFingerprint(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    static bool SameRepairFingerprint(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual)
        );

    static OperationResult StaleLegacyRepair() =>
        new(
            Outcome.Failed,
            ReasonCode.StaleSelection,
            "The startup entry, task or saved preferences changed. Review the current startup entry again."
        );

    static bool RepairDenied(Exception error) =>
        error is UnauthorizedAccessException
        || error is COMException native && native.ErrorCode == unchecked((int)0x80070005);
}
