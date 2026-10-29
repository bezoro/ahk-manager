using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

public sealed record ElevationFrame(
    string Kind,
    Guid OperationId,
    string Nonce,
    string? ReceiptHash = null,
    string? FileIdentity = null
);

public sealed record ElevationReceipt(
    int Version,
    Guid OperationId,
    Guid ReceiptId,
    string Nonce,
    ProcessStamp NormalBackend,
    ProcessStamp NormalHost,
    ProcessStamp Bootstrap,
    string BackendHash,
    string HostHash,
    string ArchiveHash,
    string ProfileRoot,
    string ChromiumRoot,
    DateTimeOffset ExpiresAt,
    StartupContinuationTicket? StartupContinuation = null,
    bool ResumeLogin = false
);

internal sealed record ElevationBootstrapRequest(
    Guid OperationId,
    string Nonce,
    int NormalBackendPid,
    long NormalBackendCreated,
    int NormalHostPid,
    long NormalHostCreated
)
{
    internal static ElevationBootstrapRequest Parse(string[] args)
    {
        if (
            args.Length != 7
            || args[0] != "--administrator-bootstrap"
            || !Guid.TryParseExact(args[1], "D", out var operation)
            || operation == Guid.Empty
            || args[2].Length != 64
            || args[2].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !int.TryParse(args[3], out int backendPid)
            || !long.TryParse(args[4], out long backendCreated)
            || !int.TryParse(args[5], out int hostPid)
            || !long.TryParse(args[6], out long hostCreated)
        )
            throw new ProtocolException("NotAuthenticated");
        return new(operation, args[2], backendPid, backendCreated, hostPid, hostCreated);
    }
}
