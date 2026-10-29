using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.Core.Models;

public sealed record ScriptRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Path { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string Description { get; init; } = "";
    public Guid? CollectionId { get; init; }
    public bool? RunAtLogin { get; init; } = false;

    [JsonRequired]
    public bool RunAsAdministrator { get; init; }

    [JsonRequired]
    public bool LaunchBlocked { get; init; }

    [JsonRequired]
    public bool LegacyGuard { get; init; }
    public string? LegacySourcePath { get; init; }
    public string? LegacySourceHash { get; init; }
    public string? InterpreterPath { get; init; }
    public ImmutableArray<string> ManualHotkeys { get; init; } = [];
}

public sealed record SettingsDocument
{
    public int Schema { get; init; } = 1;
    public List<ScriptRecord> Scripts { get; init; } = [];
    public List<CollectionSnapshot> Collections { get; init; } = [];
    public ManagerSettings Settings { get; init; } = new();
    public string LastLoginKey { get; init; } = "";
}

public sealed record OperationRecord(
    Guid Id,
    Guid ScriptId,
    ScriptOperation Operation,
    string ScriptPath,
    string InterpreterPath,
    string ScriptHash,
    string InterpreterHash,
    string UserSid,
    string LoginKey,
    DateTimeOffset RequestedAt,
    Outcome Outcome,
    ProcessIdentity? Identity = null,
    ReasonCode Code = ReasonCode.None,
    bool RestartOldExited = false,
    bool RecycleReceipt = false,
    bool RecycleStopped = false
);

public sealed record JournalDocument
{
    public int Schema { get; init; } = 1;
    public List<OperationRecord> Operations { get; init; } = [];
}
