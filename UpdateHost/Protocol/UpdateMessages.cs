namespace Bezoro.AhkManager.UpdateHost;

internal sealed record CachedInstaller(
    string InstallerPath,
    string InstallerSha256,
    string Version
);

internal sealed record UpdatePlan(
    int SchemaVersion,
    Guid OperationId,
    int ParentPid,
    DateTimeOffset ParentStartedAtUtc,
    string InstallDirectory,
    string InstallerPath,
    string InstallerSha256,
    string Version,
    CachedInstaller? Fallback = null,
    string? Deployment = null
);

internal sealed record UpdateReceipt(
    int SchemaVersion,
    int UpdateProtocol,
    string Version,
    string Sha256,
    long? Bytes = null
);

internal sealed record UpdateReady(
    int SchemaVersion,
    Guid OperationId,
    int ParentPid,
    DateTimeOffset ParentStartedAtUtc
);

internal sealed record UpdateCancel(int SchemaVersion, Guid OperationId);

internal sealed record UpdateCommit(
    int SchemaVersion,
    Guid OperationId,
    int ParentPid,
    DateTimeOffset ParentStartedAtUtc
);

internal sealed record UpdateOutcome(
    int SchemaVersion,
    Guid OperationId,
    string Outcome,
    string Detail,
    string Version,
    int? InstallerExitCode,
    int? FallbackExitCode,
    DateTimeOffset CompletedAtUtc,
    string? BackupDirectory = null,
    string? FailedInstallationDirectory = null
);
