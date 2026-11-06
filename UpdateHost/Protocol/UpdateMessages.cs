using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.UpdateHost;

// Portable protocol 2 retains its deployed JSON keys and cache layout.
// These historical names do not permit executable installers or protocol 1.
internal sealed record CachedPackage(
    [property: JsonPropertyName("installerPath")] string ArchivePath,
    [property: JsonPropertyName("installerSha256")] string ArchiveSha256,
    string Version
);

internal sealed record UpdatePlan(
    int SchemaVersion,
    Guid OperationId,
    int ParentPid,
    DateTimeOffset ParentStartedAtUtc,
    [property: JsonPropertyName("installDirectory")] string ApplicationDirectory,
    [property: JsonPropertyName("installerPath")] string ArchivePath,
    [property: JsonPropertyName("installerSha256")] string ArchiveSha256,
    string Version,
    CachedPackage? Fallback = null,
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
    [property: JsonPropertyName("installerExitCode")] int? ApplyExitCode,
    int? FallbackExitCode,
    DateTimeOffset CompletedAtUtc,
    string? BackupDirectory = null,
    string? FailedInstallationDirectory = null
);
