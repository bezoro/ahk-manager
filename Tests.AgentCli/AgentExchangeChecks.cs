using System.Text.Json;
using System.Threading.Channels;
using Bezoro.AhkManager.AgentCli;

internal static class AgentExchangeChecks
{
    internal static async Task Run()
    {
        await CheckCallerLoss();
        await CheckReceipt();
    }

    static async Task CheckCallerLoss()
    {
        var id = Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var connection = new ClientStream();
        var privateWrites = Channel.CreateUnbounded<byte[]>();
        var reply = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        byte[] request = Request(id);
        var relay = AgentExchange.Relay(
            connection,
            request,
            id,
            reply.Task,
            (bytes, _) => privateWrites.Writer.WriteAsync(bytes).AsTask(),
            deadline.Token
        );

        var forwarded = AgentProtocol.ReadRequest(
            await privateWrites.Reader.ReadAsync(deadline.Token)
        );
        if (forwarded.RequestId != id)
            throw new InvalidOperationException("Request identity changed.");
        connection.Incoming.Writer.Complete();
        var canceled = JsonSerializer.Deserialize(
            await privateWrites.Reader.ReadAsync(deadline.Token),
            AgentJson.Default.AgentCancellation
        );
        if (canceled != new AgentCancellation(1, id, true) || reply.Task.IsCompleted)
            throw new InvalidOperationException(
                "Caller loss must be relayed before the pending host reply."
            );
        reply.SetResult("late response"u8.ToArray());
        var receipt = AgentProtocol.ReadReceipt(
            await privateWrites.Reader.ReadAsync(deadline.Token)
        );
        if (receipt != new AgentReceipt(1, id, false) || connection.Outgoing.Reader.TryRead(out _))
            throw new InvalidOperationException(
                "An exited client cannot acknowledge a late host effect."
            );
        await relay.WaitAsync(deadline.Token);
    }

    static async Task CheckReceipt()
    {
        var id = Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var connection = new ClientStream();
        var privateWrites = Channel.CreateUnbounded<byte[]>();
        var relay = AgentExchange.Relay(
            connection,
            Request(id),
            id,
            Task.FromResult("response"u8.ToArray()),
            (bytes, _) => privateWrites.Writer.WriteAsync(bytes).AsTask(),
            deadline.Token
        );
        _ = await privateWrites.Reader.ReadAsync(deadline.Token);
        byte[] prefix = await connection.Outgoing.Reader.ReadAsync(deadline.Token);
        byte[] response = await connection.Outgoing.Reader.ReadAsync(deadline.Token);
        if (
            prefix[0] != response.Length
            || !response.AsSpan().SequenceEqual("response"u8)
            || privateWrites.Reader.TryRead(out _)
        )
            throw new InvalidOperationException("Sending bytes alone cannot acknowledge delivery.");
        using var outgoing = new MemoryStream();
        await AgentProtocol.Write(
            outgoing,
            JsonSerializer.SerializeToUtf8Bytes(
                new AgentReceipt(1, id, true),
                AgentJson.Default.AgentReceipt
            ),
            AgentProtocol.MaxReceipt,
            deadline.Token
        );
        await connection.Incoming.Writer.WriteAsync(outgoing.ToArray(), deadline.Token);
        if (
            AgentProtocol.ReadReceipt(await privateWrites.Reader.ReadAsync(deadline.Token))
            != new AgentReceipt(1, id, true)
        )
            throw new InvalidOperationException(
                "Acknowledgment must be correlated to the confirmed response."
            );
        await relay.WaitAsync(deadline.Token);
    }

    static byte[] Request(Guid id) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new AgentRequest(1, id, "app.quit", AgentProtocol.Empty),
            AgentJson.Default.AgentRequest
        );

    sealed class ClientStream : Stream
    {
        byte[]? buffer;
        int offset;
        internal Channel<byte[]> Incoming { get; } = Channel.CreateUnbounded<byte[]>();
        internal Channel<byte[]> Outgoing { get; } = Channel.CreateUnbounded<byte[]>();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken ct = default
        ) => Outgoing.Writer.WriteAsync(source.ToArray(), ct);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken ct = default
        )
        {
            if (buffer is null || offset == buffer.Length)
            {
                if (
                    !await Incoming.Reader.WaitToReadAsync(ct)
                    || !Incoming.Reader.TryRead(out buffer)
                )
                    return 0;
                offset = 0;
            }
            int copied = Math.Min(destination.Length, buffer.Length - offset);
            buffer.AsMemory(offset, copied).CopyTo(destination);
            offset += copied;
            return copied;
        }
    }
}
