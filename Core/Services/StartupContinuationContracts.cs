using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public enum StartupContinuationPhase
{
    Prepared,
    Committed,
    Accepted,
    Completed,
    Partial,
    Recovered,
}

public enum StartupContinuationItemState
{
    NotDispatched,
    EnteringExecution,
    KnownRunning,
    KnownStarted,
    KnownFailed,
    RefusedBeforeExecution,
    Uncertain,
}

public sealed record StartupContinuationItem(
    Guid ScriptId,
    string Name,
    string ScriptPath,
    string InterpreterPath,
    string ScriptHash,
    string InterpreterHash
);

public sealed record StartupContinuationTicket(
    int Version,
    Guid OperationId,
    string ProfileRoot,
    string ProfileHash,
    string UserSid,
    int SessionId,
    string LoginKey,
    DateTimeOffset ExpiresAt,
    ImmutableArray<StartupContinuationItem> Items
);

public sealed record StartupContinuationRecord(
    StartupContinuationTicket Ticket,
    string TicketHash,
    StartupContinuationPhase Phase,
    ImmutableArray<StartupContinuationItemState> Items,
    OperationResult? Result = null
);

public sealed record StartupContinuationOffer(
    Guid OperationId,
    ImmutableArray<string> ScriptNames,
    bool CanResume,
    ReasonCode Reason
);

internal sealed record StartupRecoveryFile(
    string OriginalName,
    string PreservedName,
    string Hash,
    string FileIdentity
);

internal sealed record StartupRecoveryReceipt(
    Guid OperationId,
    DateTimeOffset RequestedAt,
    ImmutableArray<StartupRecoveryFile> Files
);
