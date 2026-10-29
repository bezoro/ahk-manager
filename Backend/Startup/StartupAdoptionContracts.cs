using System.Globalization;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

public sealed record StartupAdoptionFrame(
    string Kind,
    Guid OperationId,
    string Nonce,
    StartupContinuationTicket? Ticket = null,
    string? TicketHash = null,
    OperationResult? Result = null
);

internal sealed record StartupAdoptionDescriptor(
    Guid OperationId,
    string Nonce,
    int BootstrapPid,
    long BootstrapCreated,
    string TicketHash
)
{
    internal string Encode() =>
        string.Join(
            '|',
            OperationId.ToString("D"),
            Nonce,
            BootstrapPid.ToString(CultureInfo.InvariantCulture),
            BootstrapCreated.ToString(CultureInfo.InvariantCulture),
            TicketHash
        );

    internal static StartupAdoptionDescriptor Parse(string value)
    {
        var parts = value.Split('|');
        if (
            parts.Length != 5
            || !Guid.TryParseExact(parts[0], "D", out var id)
            || id == Guid.Empty
            || !Hex(parts[1])
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || pid <= 0
            || !long.TryParse(
                parts[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long created
            )
            || created <= 0
            || !Hex(parts[4])
        )
            throw new ProtocolException("NotAuthenticated");
        return new(id, parts[1], pid, created, parts[4]);
    }

    static bool Hex(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
