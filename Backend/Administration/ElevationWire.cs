namespace Bezoro.AhkManager.Backend;

internal static class ElevationWire
{
    internal static Task Send(Stream pipe, ElevationFrame frame, CancellationToken ct) =>
        LengthPrefixedJson.SendAsync(pipe, frame, Protocol.Json.ElevationFrame, 4096, ct);

    internal static Task<ElevationFrame> Receive(Stream pipe, CancellationToken ct) =>
        LengthPrefixedJson.ReceiveAsync(
            pipe,
            Protocol.Json.ElevationFrame,
            4096,
            "InvalidRequest",
            ct
        );
}
