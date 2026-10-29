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

public sealed class UserStartupService(IUserStartupAdapter adapter, string managerExecutable)
{
    public const string TaskSource = "Bezoro.AhkManager.Electron.Startup.v1";
    public string Command { get; } =
        ScriptPaths.QuoteArgument(ScriptPaths.LocalPath(managerExecutable))
        + " --login --background";

    public Task<StartupInspection> InspectAsync(CancellationToken ct) =>
        adapter is RegistryStartupAdapter registry
            ? registry.InspectAsync(ct, Command)
            : adapter.InspectAsync(ct);

    bool Owned(StartupTaskRegistration task, IUserStartupTaskAdapter tasks) =>
        OwnsTask(task, Command, tasks.UserSid);

    internal static bool OwnsTask(StartupTaskRegistration task, string command, string userSid)
    {
        if (
            task.Source != TaskSource
            || task.UserSid != userSid
            || !task.InteractiveToken
            || !task.UserLogonOnly
        )
            return false;
        var actual = ScriptPaths.SplitCommand(task.Command);
        var expected = ScriptPaths.SplitCommand(command);
        return actual.Length == 3
            && expected.Length == 3
            && ScriptPaths.Equal(actual[0], expected[0])
            && actual[1] == expected[1]
            && actual[2] == expected[2];
    }

    bool Desired(StartupTaskRegistration task) => task.Enabled && task.HighestPrivileges;

    public ReasonCode RegistrationStatus(bool expectedEnabled) =>
        RegistrationStatus(expectedEnabled, false);

    public ReasonCode RegistrationStatus(bool expectedEnabled, bool elevated)
    {
        try
        {
            string? existing = adapter.ReadEntry();
            var tasks = adapter as IUserStartupTaskAdapter;
            var task = tasks?.ReadTask();
            if (
                existing is not null && existing != Command
                || task is not null && (tasks is null || !Owned(task, tasks))
            )
                return ReasonCode.StartupEntryConflict;
            if (!expectedEnabled)
                return existing is null && task is null
                    ? ReasonCode.None
                    : ReasonCode.StartupEntryConflict;
            if (elevated && tasks is null)
                return ReasonCode.CapabilityUnverified;
            return elevated
                ? existing is null && task is not null && Desired(task)
                    ? ReasonCode.None
                    : ReasonCode.StartupInactive
                : existing == Command && task is null
                    ? ReasonCode.None
                    : ReasonCode.StartupInactive;
        }
        catch (Exception ex) when (RegistrationException(ex))
        {
            return ReasonCode.AccessDenied;
        }
    }

    public Task<OperationResult> SetEnabledAsync(
        bool enabled,
        bool consent,
        CancellationToken ct = default
    ) => SetEnabledAsync(enabled, consent, false, ct);

    public Task<OperationResult> SetEnabledAsync(
        bool enabled,
        bool consent,
        bool elevated,
        CancellationToken ct = default
    ) =>
        Task.Run(
            () =>
            {
                string? originalEntry = null;
                StartupTaskRegistration? originalTask = null;
                var tasks = adapter as IUserStartupTaskAdapter;
                bool changed = false;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    originalEntry = adapter.ReadEntry();
                    originalTask = tasks?.ReadTask();
                    if (
                        originalEntry is not null && originalEntry != Command
                        || originalTask is not null
                            && (tasks is null || !Owned(originalTask, tasks))
                    )
                        return new OperationResult(Outcome.Failed, ReasonCode.StartupEntryConflict);
                    if (enabled && elevated && tasks is null)
                        return new(
                            Outcome.Failed,
                            ReasonCode.CapabilityUnverified,
                            "The startup adapter does not support administrator tasks."
                        );
                    bool correct = enabled
                        ? elevated
                            ? originalEntry is null
                                && originalTask is not null
                                && Desired(originalTask)
                            : originalEntry == Command && originalTask is null
                        : originalEntry is null && originalTask is null;
                    if (correct)
                        return new(Outcome.Confirmed);
                    if (enabled && !consent)
                        return new(Outcome.Failed, ReasonCode.StartupConsentRequired);

                    // Remove the previous mode before creating the replacement. TASK_CREATE
                    // and registry compare/exchange refuse a concurrently supplied entry.
                    if (originalEntry is not null && (!enabled || elevated))
                    {
                        if (!adapter.CompareExchangeEntry(originalEntry, null))
                            return new(Outcome.Unknown, ReasonCode.StartupEntryConflict);
                        changed = true;
                    }
                    if (
                        originalTask is not null
                        && (!enabled || !elevated || !Desired(originalTask))
                    )
                    {
                        if (!tasks!.CompareExchangeTask(originalTask, null))
                            throw new StartupRegistrationConflictException();
                        changed = true;
                    }
                    if (enabled && elevated && (originalTask is null || !Desired(originalTask)))
                    {
                        var desired = new StartupTaskRegistration(
                            Command,
                            tasks!.UserSid,
                            true,
                            true,
                            true,
                            true,
                            TaskSource
                        );
                        if (!tasks.CompareExchangeTask(null, desired))
                            throw new StartupRegistrationConflictException();
                        changed = true;
                    }
                    else if (enabled && !elevated && originalEntry is null)
                    {
                        if (!adapter.CompareExchangeEntry(null, Command))
                            throw new StartupRegistrationConflictException();
                        changed = true;
                    }
                    var final = RegistrationStatus(enabled, elevated);
                    return final == ReasonCode.None
                        ? new(Outcome.Confirmed)
                        : new(
                            Outcome.Unknown,
                            final,
                            "The startup registration changed during the operation. Review your preferences."
                        );
                }
                catch (OperationCanceledException)
                {
                    return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
                }
                catch (Exception ex) when (RegistrationException(ex))
                {
                    bool restored = !changed || RestorePrevious();
                    bool denied =
                        ex is UnauthorizedAccessException
                        || ex is COMException native
                            && native.ErrorCode == unchecked((int)0x80070005);
                    var code =
                        ex is StartupRegistrationConflictException ? ReasonCode.StartupEntryConflict
                        : denied
                            ? elevated || originalTask is not null
                                    ? ReasonCode.ManualElevationRequired
                                : ReasonCode.AccessDenied
                        : ReasonCode.StorageFailure;
                    string detail = code
                        is ReasonCode.AccessDenied
                            or ReasonCode.ManualElevationRequired
                        ? "Windows denied the automatic startup change. Reopen Manager as administrator to configure the task with elevated privileges."
                        : "The automatic startup change could not be completed.";
                    detail += restored
                        ? " The previous registration was preserved."
                        : " The registration needs review; the change was only partially completed.";
                    return new(restored ? Outcome.Failed : Outcome.Partial, code, detail);
                }

                bool RestorePrevious()
                {
                    try
                    {
                        string? currentEntry = adapter.ReadEntry();
                        var currentTask = tasks?.ReadTask();
                        if (originalEntry is not null && originalTask is null)
                        {
                            if (currentEntry == originalEntry && currentTask is null)
                                return true;
                            if (currentEntry is null && currentTask is null)
                                return adapter.CompareExchangeEntry(null, originalEntry);
                        }
                        else if (originalTask is not null && originalEntry is null)
                        {
                            if (currentEntry is null && currentTask == originalTask)
                                return true;
                            if (currentEntry is null && currentTask is null)
                                return tasks!.CompareExchangeTask(null, originalTask);
                        }
                        else if (originalEntry is null && originalTask is null)
                            return currentEntry is null && currentTask is null;
                        return false;
                    }
                    catch (Exception rollback) when (RegistrationException(rollback))
                    {
                        return false;
                    }
                }
            },
            ct
        );

    static bool RegistrationException(Exception ex) =>
        SafeJsonFiles.Known(ex)
        || ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception;

    sealed class StartupRegistrationConflictException : IOException;
}
