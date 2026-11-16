using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Bezoro.AhkManager.AgentCli;

internal sealed class AgentServer(Stream input, Stream output, AgentPeer parent)
{
    readonly ConcurrentDictionary<Guid, TaskCompletionSource<byte[]>> replies = new();
    readonly SemaphoreSlim writer = new(1);
    readonly CancellationTokenSource stopping = new();

    internal async Task Run(AgentLayout layout, AgentPeer self, CancellationToken ct)
    {
        using var host = parent.RetainObservableProcess();
        using var lifetime = ct.Register(stopping.Cancel);
        var pipes = new List<NamedPipeServerStream>(AgentPipe.Connections);
        try
        {
            string name = layout.PipeName(self);
            for (int index = 0; index < AgentPipe.Connections; index++)
                pipes.Add(AgentPipe.Listen(name, self.Stamp.Sid, first: index == 0));
            var loops = pipes.Select(pipe => Listen(pipe, layout, self)).ToList();
            loops.Add(ReadReplies());
            loops.Add(host.WaitForExitAsync(stopping.Token));
            await await Task.WhenAny(loops);
        }
        finally
        {
            stopping.Cancel();
            foreach (var pipe in pipes)
                pipe.Dispose();
            // The process exits here. A Windows stdio operation can block beyond
            // cancellation, so do not hold executable handles waiting on stdio.
        }
    }

    async Task ReadReplies()
    {
        while (!stopping.IsCancellationRequested)
        {
            var bytes = await AgentProtocol.Read(input, AgentProtocol.MaxResponse, stopping.Token);
            var response = AgentProtocol.ReadResponse(bytes);
            if (replies.TryGetValue(response.RequestId, out var reply))
                reply.TrySetResult(bytes);
        }
    }

    async Task Listen(NamedPipeServerStream pipe, AgentLayout layout, AgentPeer self)
    {
        while (!stopping.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(stopping.Token);
            bool verifiedPeer = false;
            try
            {
                using var peer = new AgentPeer(AgentPipe.ClientPid(pipe.SafePipeHandle));
                peer.RequirePeer(self, layout.Cli);
                verifiedPeer = true;
                await Exchange(pipe, peer);
            }
            catch (Exception ex) when (AgentFailure.Expected(ex))
            {
                // Unverified peers receive no data and produce no diagnostics.
                // A verified peer's relay failure may inform the trusted parent.
                if (verifiedPeer)
                    AgentDiagnostics.Write(
                        "relay",
                        ex is AgentFailure failure ? failure.Code : AgentDiagnostics.Classify(ex)
                    );
            }
            finally
            {
                pipe.Disconnect();
            }
        }
    }

    async Task Exchange(NamedPipeServerStream pipe, AgentPeer peer)
    {
        byte[] requestBytes;
        using (var admission = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            admission.CancelAfter(TimeSpan.FromSeconds(5));
            requestBytes = await AgentProtocol.Read(
                pipe,
                AgentProtocol.MaxRequest,
                admission.Token
            );
        }
        var request = AgentProtocol.ReadRequest(requestBytes);
        if (!peer.Live || !parent.Live)
            throw new AgentFailure("PeerUnverified");
        var pending = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (!replies.TryAdd(request.RequestId, pending))
            throw new InvalidDataException("DuplicateRequest");
        try
        {
            await AgentExchange.Relay(
                pipe,
                requestBytes,
                request.RequestId,
                pending.Task,
                Send,
                stopping.Token
            );
        }
        finally
        {
            _ = replies.TryRemove(request.RequestId, out _);
        }
    }

    async Task Send(byte[] payload, int limit)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await writer.WaitAsync(deadline.Token);
        try
        {
            if (!parent.Live)
                throw new AgentFailure("PeerUnverified");
            await AgentProtocol
                .Write(output, payload, limit, deadline.Token)
                .WaitAsync(deadline.Token);
        }
        catch
        {
            // A partial private frame may have been written. Breaking the
            // endpoint preserves unknown results without replaying any command.
            stopping.Cancel();
            throw;
        }
        finally
        {
            writer.Release();
        }
    }
}
