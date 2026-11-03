using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.Core.Services;

// Only finite gate names enter diagnostics. Unknown commands and task XML stay private.
internal static class StartupRegistrationDiagnostics
{
    internal const string Changed =
        "The Windows startup registration changed during the operation (registration-changed).";

    internal static string ConflictDetail(
        bool entryConflict,
        StartupTaskRegistration? task,
        string expectedCommand,
        string? userSid
    )
    {
        var failed = new List<string>();
        if (entryConflict)
            failed.Add("registry-entry");
        if (task is not null)
        {
            if (task.ParseIssue != StartupTaskParseIssue.None)
                failed.Add(
                    task.ParseIssue switch
                    {
                        StartupTaskParseIssue.PrincipalAccountUnresolved =>
                            "task-principal-account-unresolved",
                        StartupTaskParseIssue.TriggerAccountUnresolved =>
                            "task-trigger-account-unresolved",
                        StartupTaskParseIssue.Executable => "task-executable",
                        _ => "task-xml-shape",
                    }
                );
            else
            {
                if (task.Source != UserStartupService.TaskSource)
                    failed.Add("task-source");
                if (userSid is null || task.UserSid != userSid)
                    failed.Add("task-account");
                if (!task.InteractiveToken)
                    failed.Add("task-logon-kind");
                if (!task.UserLogonOnly)
                    failed.Add("task-trigger");
                if (
                    userSid is not null
                    && task.Source == UserStartupService.TaskSource
                    && task.UserSid == userSid
                    && task.InteractiveToken
                    && task.UserLogonOnly
                    && !UserStartupService.OwnsTask(task, expectedCommand, userSid)
                    && !UserStartupService.OwnsRelocatableTask(task, expectedCommand, userSid)
                )
                {
                    var arguments = ScriptPaths.SplitCommand(task.Command);
                    failed.Add(
                        arguments.Length != 3
                        || arguments[1] != "--login"
                        || arguments[2] != "--background"
                            ? "task-arguments"
                            : "task-executable"
                    );
                }
            }
        }
        if (failed.Count == 0)
            failed.Add("registration-snapshot");
        return "Failed startup ownership checks: "
            + string.Join(", ", failed.Distinct())
            + ". Existing entries were preserved.";
    }
}
