namespace Bezoro.AhkManager.Core.Models;

public sealed record StartupRepairPreview(
    bool CanRepair,
    string? PreviousExecutable,
    string? CurrentExecutable,
    string? Fingerprint,
    ReasonCode Reason,
    string? Detail = null
);

public sealed record StartupRepairBackup(
    DateTimeOffset Timestamp,
    string UserSid,
    string PreviousRunEntry,
    string CurrentCommand,
    string TaskFingerprint,
    bool LaunchManagerAtLogin,
    bool LaunchManagerAsAdministrator,
    StartupTaskBackup? PreviousTask = null
);

public sealed record StartupTaskBackup(
    string Command,
    string UserSid,
    bool Enabled,
    bool HighestPrivileges,
    bool InteractiveToken,
    bool UserLogonOnly,
    string Source,
    string Fingerprint,
    string? NonstandardWorkingDirectory,
    string ParseIssue
);
