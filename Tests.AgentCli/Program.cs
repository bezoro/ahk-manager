using System.Buffers.Binary;
using System.Text.Json;
using Bezoro.AhkManager.AgentCli;

var id = Guid.NewGuid();
var request = new AgentRequest(1, id, "app.status", AgentProtocol.Empty);
byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, AgentJson.Default.AgentRequest);
var receipt = new AgentReceipt(1, id, false);
byte[] acknowledgement = JsonSerializer.SerializeToUtf8Bytes(
    receipt,
    AgentJson.Default.AgentReceipt
);
using var wire = new MemoryStream();
await AgentProtocol.Write(wire, payload, AgentProtocol.MaxRequest, default);
await AgentProtocol.Write(wire, acknowledgement, AgentProtocol.MaxReceipt, default);
byte[] framed = wire.ToArray();
Check(framed.AsSpan(0, 4).SequenceEqual(Prefix(payload.Length)), "first frame is little-endian");
using var fragmented = new FragmentedStream(framed);
var received = AgentProtocol.ReadRequest(
    await AgentProtocol.Read(fragmented, AgentProtocol.MaxRequest, default)
);
Check(
    received.RequestId == id
        && received.Command == "app.status"
        && !received.Arguments.EnumerateObject().Any(),
    "fragmented request preserves identity and arguments"
);
Check(
    AgentProtocol.ReadReceipt(
        await AgentProtocol.Read(fragmented, AgentProtocol.MaxReceipt, default)
    ) == receipt,
    "a negative receipt remains negative and correlates to its request"
);
await Refuses<EndOfStreamException>(() =>
    AgentProtocol.Read(fragmented, AgentProtocol.MaxReceipt, default)
);

foreach (int length in new[] { 0, AgentProtocol.MaxRequest + 1, -1 })
    await Refuses<InvalidDataException>(() =>
        AgentProtocol.Read(new MemoryStream(Prefix(length)), AgentProtocol.MaxRequest, default)
    );
await Refuses<EndOfStreamException>(() =>
    AgentProtocol.Read(new MemoryStream([3, 0, 0, 0, 1, 2]), AgentProtocol.MaxRequest, default)
);
await Refuses<OperationCanceledException>(() =>
    AgentProtocol.Read(
        new FragmentedStream(framed),
        AgentProtocol.MaxRequest,
        new CancellationToken(true)
    )
);

Rejects<InvalidDataException>(() =>
    AgentProtocol.ReadRequest(
        AgentProtocol.Utf8.GetBytes(
            $$$$"""{"protocol":1,"requestId":"{{{{id:D}}}}","command":"app.status","arguments":{"sort":{"order":1,"order":2}}}"""
        )
    )
);
Rejects<InvalidDataException>(() =>
    AgentProtocol.ReadRequest(
        AgentProtocol.Utf8.GetBytes(
            $$$"""{"protocol":2,"requestId":"{{{id:D}}}","command":"app.status","arguments":{}}"""
        )
    )
);
Rejects<InvalidDataException>(() =>
    AgentProtocol.ReadRequest(
        AgentProtocol.Utf8.GetBytes(
            $$"""{"protocol":1,"requestId":"{{id:D}}","command":"app.status","arguments":[],"extra":true}"""
        )
    )
);
Rejects<System.Text.DecoderFallbackException>(() => AgentProtocol.ReadRequest([0xc3, 0x28]));
Rejects<InvalidDataException>(() =>
    AgentProtocol.ReadReceipt(
        AgentProtocol.Utf8.GetBytes(
            $$"""{"protocol":1,"requestId":"{{id:D}}","delivered":"true"}"""
        )
    )
);
var response = AgentProtocol.ReadResponse(
    AgentProtocol.Utf8.GetBytes(
        $$"""{"protocol":1,"requestId":"{{id:D}}","version":"2.0.2","generation":"session","outcome":"Unknown","code":"ReplyUnconfirmed","result":null}"""
    )
);
Check(
    response.RequestId == id
        && response.Outcome == "Unknown"
        && response.Code == "ReplyUnconfirmed",
    "an unknown outcome survives transport"
);
await AgentExchangeChecks.Run();
AgentDiagnosticsChecks.Run();
Console.WriteLine("Agent CLI frames, limits, cancellation, typed receipts and strict JSON passed.");

static byte[] Prefix(int length)
{
    var bytes = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)length));
    return bytes;
}

static void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

static void Rejects<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

static async Task Refuses<T>(Func<Task> action)
    where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], ct);
}
