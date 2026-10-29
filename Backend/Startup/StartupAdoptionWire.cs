using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

internal static class StartupAdoptionWire
{
    internal static Task Send(Stream pipe, StartupAdoptionFrame frame, CancellationToken ct) =>
        LengthPrefixedJson.SendAsync(
            pipe,
            frame,
            Protocol.Json.StartupAdoptionFrame,
            StartupContinuationStore.MaximumBytes,
            ct
        );

    internal static Task<StartupAdoptionFrame> Receive(Stream pipe, CancellationToken ct) =>
        LengthPrefixedJson.ReceiveAsync(
            pipe,
            Protocol.Json.StartupAdoptionFrame,
            StartupContinuationStore.MaximumBytes,
            "NotAuthenticated",
            ct
        );

    internal static void Check(
        StartupAdoptionFrame frame,
        string kind,
        StartupAdoptionDescriptor descriptor
    )
    {
        if (
            frame.Kind != kind
            || frame.OperationId != descriptor.OperationId
            || frame.Nonce != descriptor.Nonce
            || frame.TicketHash != descriptor.TicketHash
        )
            throw new ProtocolException("NotAuthenticated");
    }

    internal static string PipeName(string nonce) => "Bezoro.AhkManager.StartupAdoption." + nonce;
}
