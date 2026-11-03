using Bezoro.AhkManager.Core.Services;

namespace Tests.Backend;

internal static class StartupDiagnosticChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string sid = "S-1-5-21-100-200-300-1002";
        const string expected = "\"C:\\Current\\Bezoro.AhkManager.exe\" --login --background";
        var task = new StartupTaskRegistration(
            expected,
            sid,
            true,
            true,
            true,
            true,
            UserStartupService.TaskSource
        );
        string registry = StartupRegistrationDiagnostics.ConflictDetail(true, task, expected, sid);
        check(
            registry.Contains("registry-entry") && !registry.Contains("task-"),
            "startup diagnostics distinguish an old Run value from a correct current task"
        );
        foreach (
            var (issue, label) in new[]
            {
                (StartupTaskParseIssue.XmlShape, "task-xml-shape"),
                (
                    StartupTaskParseIssue.PrincipalAccountUnresolved,
                    "task-principal-account-unresolved"
                ),
                (StartupTaskParseIssue.TriggerAccountUnresolved, "task-trigger-account-unresolved"),
                (StartupTaskParseIssue.Executable, "task-executable"),
            }
        )
        {
            string detail = StartupRegistrationDiagnostics.ConflictDetail(
                false,
                task with
                {
                    ParseIssue = issue,
                },
                expected,
                sid
            );
            check(
                detail.Contains(label),
                "startup diagnostics retain finite parser refusal " + label
            );
        }
        var foreign = task with
        {
            Source = "private-source-secret",
            UserSid = "private-account-secret",
            Command = "\"C:\\Private\\foreign.exe\" --token private-token-secret",
            InteractiveToken = false,
            UserLogonOnly = false,
        };
        string refused = StartupRegistrationDiagnostics.ConflictDetail(
            true,
            foreign,
            expected,
            sid
        );
        check(
            new[]
            {
                "registry-entry",
                "task-source",
                "task-account",
                "task-logon-kind",
                "task-trigger",
            }.All(refused.Contains)
                && !refused.Contains("private-")
                && !refused.Contains("C:\\Private")
                && refused.Length < 1024,
            "startup refusal details explain finite gates without leaking foreign commands accounts paths or source text"
        );
        string arguments = StartupRegistrationDiagnostics.ConflictDetail(
            false,
            task with
            {
                Command = foreign.Command,
            },
            expected,
            sid
        );
        check(
            arguments.Contains("task-arguments") && !arguments.Contains("private-"),
            "startup diagnostics identify argument mismatches independently of a valid task identity"
        );
        string unhealthy = StartupRegistrationDiagnostics.ConflictDetail(
            true,
            task with
            {
                NonstandardWorkingDirectory = "C:\\Previous",
            },
            expected,
            sid
        );
        check(
            unhealthy == registry,
            "owned working-directory health is not misreported as foreign ownership"
        );
    }
}
