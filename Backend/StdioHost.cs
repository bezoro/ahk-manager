using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.Backend;

internal sealed class StdioHost(Stream input, Stream output, TextWriter diagnostics, IParentGuard parent,
    string session, string profileRoot, Func<IBackendSession> compose, TimeSpan? shutdownDeadline = null)
{
    sealed record Work(RequestFrame Request, string Hash, int Bytes);
    sealed record Completed(string Hash, byte[] Response);
    readonly Channel<Work> requests = Channel.CreateBounded<Work>(new BoundedChannelOptions(Protocol.MaximumPending) { SingleReader = true, SingleWriter = true });
    readonly Channel<byte[]> responses = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true });
    readonly Dictionary<ulong, string> active = [];
    readonly Dictionary<ulong, Completed> completed = [];
    readonly Queue<ulong> cacheOrder = [];
    readonly object sync = new();
    readonly CancellationTokenSource intake = new(), operation = new();
    ulong highest;
    int queuedBytes, cacheBytes;
    volatile bool stopping;
    long shutdownStarted;
    readonly TaskCompletionSource shutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TimeSpan deadline = shutdownDeadline ?? Protocol.ShutdownDeadline;
    IBackendSession? service;
    int diagnosticLines;
    int exitCode;
    ResponseFrame Frame(RequestFrame request, string code, JsonElement? payload = null, string? error = null) => new(1, "response", request.Id, session, request.Action, code, payload, error);
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        using var caller = ct.Register(Stop);
        var reader = new FrameReader(input);
        try
        {
            using var handshake = new CancellationTokenSource(Protocol.HandshakeDeadline);
            if (!parent.Validate()) throw new ProtocolException("ParentMismatch");
            await FrameWriter.WriteAsync(output, FrameWriter.Encode(new(1, "parentProof", null, session, "hello", "Ok", JsonSerializer.SerializeToElement(parent.Proof, Protocol.Json.ParentProof))), handshake.Token).ConfigureAwait(false);
            byte[] first = await reader.ReadAsync(handshake.Token).ConfigureAwait(false) ?? throw new ProtocolException("NotAuthenticated");
            RequestFrame hello = Protocol.ReadRequest(first);
            var payload = hello.Payload.Deserialize(Protocol.Json.HelloPayload) ?? throw new ProtocolException("NotAuthenticated");
            if (hello.Session != session || hello.Action != "hello" || payload.ParentPid != parent.Proof.ParentPid || payload.ParentCreated != parent.Proof.ParentCreated || !parent.Validate()) throw new ProtocolException("ParentMismatch");
            highest = ulong.Parse(hello.Id, CultureInfo.InvariantCulture);
            service = compose();
            service.Changed += Changed;
            var helloResult = new HelloResult(1, profileRoot, parent.Proof.ParentExecutable);
            await FrameWriter.WriteAsync(output, FrameWriter.Encode(Frame(hello, "Ok", JsonSerializer.SerializeToElement(helloResult, Protocol.Json.HelloResult))), handshake.Token).ConfigureAwait(false);
            Task write = WriteLoop(); Task read = ReadLoop(reader); Task watch = WatchParent();
            await Consume().ConfigureAwait(false);
            Stop();
            try { await Task.WhenAll(read, watch).WaitAsync(Remaining()).ConfigureAwait(false); }
            catch (TimeoutException) { Log("InputShutdownUnknown"); exitCode = 75; }
            await DisposeBounded().ConfigureAwait(false);
            responses.Writer.TryComplete();
            try { await write.WaitAsync(Remaining()).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { Log("OutputShutdownUnknown"); exitCode = 75; }
            return exitCode;
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            Log(ex is ProtocolException protocol ? protocol.Code : ex.GetType().Name); exitCode = 65;
            Stop(); await DisposeBounded().ConfigureAwait(false); return exitCode;
        }
        finally { if (service is not null) service.Changed -= Changed; }
    }
    async Task ReadLoop(FrameReader reader)
    {
        try
        {
            while (!intake.IsCancellationRequested)
            {
                byte[]? bytes = await reader.ReadAsync(intake.Token).ConfigureAwait(false);
                if (bytes is null) { Stop(); break; }
                RequestFrame request = Protocol.ReadRequest(bytes);
                if (request.Session != session || request.Action == "hello") throw new ProtocolException("NotAuthenticated");
                ulong id = ulong.Parse(request.Id, CultureInfo.InvariantCulture);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                byte[]? cached = null; string? reject = null; bool duplicate = false;
                lock (sync)
                {
                    if (active.TryGetValue(id, out var pendingHash)) { duplicate = pendingHash == hash; if (!duplicate) reject = "IdConflict"; }
                    else if (completed.TryGetValue(id, out var previous)) { if (previous.Hash == hash) cached = previous.Response; else reject = "IdConflict"; }
                    else if (id <= highest) reject = "RequestExpired";
                    else
                    {
                        highest = id;
                        if (active.Count >= Protocol.MaximumPending || queuedBytes + bytes.Length > 32 * 1024 * 1024) reject = "QueueFull";
                        else { active.Add(id, hash); queuedBytes += bytes.Length; }
                    }
                }
                if (duplicate) continue;
                if (cached is not null) { await responses.Writer.WriteAsync(cached, intake.Token).ConfigureAwait(false); continue; }
                if (reject is not null) { await responses.Writer.WriteAsync(FrameWriter.Encode(Frame(request, reject)), intake.Token).ConfigureAwait(false); continue; }
                if (!requests.Writer.TryWrite(new(request, hash, bytes.Length)))
                {
                    lock (sync) { active.Remove(id); queuedBytes -= bytes.Length; }
                    await responses.Writer.WriteAsync(FrameWriter.Encode(Frame(request, "QueueFull")), intake.Token).ConfigureAwait(false);
                }
                if (request.Action == "shutdown") { BeginShutdown(); break; }
            }
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException or IOException or OperationCanceledException or ChannelClosedException)
        { if (!intake.IsCancellationRequested) { Log(ex is ProtocolException p ? p.Code : ex.GetType().Name); exitCode = 65; Stop(); } }
        finally { requests.Writer.TryComplete(); }
    }
    async Task Consume()
    {
        await foreach (var work in requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            ResponseFrame reply;
            try
            {
                if ((stopping && work.Request.Action != "shutdown") || operation.IsCancellationRequested) reply = Frame(work.Request, "ShuttingDown");
                else if (!parent.Validate()) { Stop(); reply = Frame(work.Request, "ParentMismatch"); }
                else
                {
                    Task<JsonElement> task = service!.ExecuteAsync(work.Request.Action, work.Request.Payload, operation.Token);
                    if (!task.IsCompleted) await Task.WhenAny(task, shutdownSignal.Task).ConfigureAwait(false);
                    JsonElement result = stopping ? await task.WaitAsync(Remaining()).ConfigureAwait(false) : await task.ConfigureAwait(false);
                    reply = Frame(work.Request, "Ok", result);
                }
            }
            catch (TimeoutException)
            {
                exitCode = 75; Log("ShutdownDeadlineExceeded");
                reply = Frame(work.Request, "Ok", JsonSerializer.SerializeToElement(new OperationResult(Outcome.Unknown, ReasonCode.PendingOperation, "Backend shutdown deadline exceeded."), Protocol.Json.OperationResult));
            }
            catch (Exception ex) when (ex is ProtocolException or JsonException or ArgumentException or InvalidOperationException or IOException or OperationCanceledException)
            { reply = Frame(work.Request, ex is ProtocolException p ? p.Code : ex is JsonException or ArgumentException ? "InvalidRequest" : "InternalFailure"); Log(reply.Code); }
            byte[] response;
            try { response = FrameWriter.Encode(reply); }
            catch (ProtocolException) { response = FrameWriter.Encode(Frame(work.Request, "FrameTooLarge")); }
            ulong id = ulong.Parse(work.Request.Id, CultureInfo.InvariantCulture);
            lock (sync)
            {
                active.Remove(id); queuedBytes -= work.Bytes;
                completed.Add(id, new(work.Hash, response)); cacheOrder.Enqueue(id); cacheBytes += response.Length;
                while (cacheOrder.Count > 32 || cacheBytes > 32 * 1024 * 1024)
                { ulong expired = cacheOrder.Dequeue(); cacheBytes -= completed[expired].Response.Length; completed.Remove(expired); }
            }
            try { await responses.Writer.WriteAsync(response).AsTask().WaitAsync(Remaining()).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or ChannelClosedException) { Log("OutputBackpressure"); exitCode = 75; Stop(); break; }
        }
    }
    async Task WriteLoop()
    {
        try { await foreach (byte[] frame in responses.Reader.ReadAllAsync().ConfigureAwait(false)) await FrameWriter.WriteAsync(output, frame, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Log("OutputDisconnected"); exitCode = 65; Stop(); responses.Writer.TryComplete(); }
    }
    async Task WatchParent()
    {
        try
        {
            while (!intake.IsCancellationRequested)
            {
                await Task.Delay(250, intake.Token).ConfigureAwait(false);
                if (!parent.Validate()) { Log("ParentLost"); Stop(); break; }
            }
        }
        catch (OperationCanceledException) { }
    }
    void Changed(object? sender, ManagerSnapshot snapshot)
    {
        try
        {
            if (!responses.Writer.TryWrite(FrameWriter.Encode(new(1, "snapshot", null, session, "snapshot", "Ok", JsonSerializer.SerializeToElement(snapshot, Protocol.Json.ManagerSnapshot)))))
            { Log("OutputBackpressure"); exitCode = 75; Stop(); }
        }
        catch (ProtocolException) { Log("FrameTooLarge"); exitCode = 65; Stop(); }
    }
    void BeginShutdown() { stopping = true; Interlocked.CompareExchange(ref shutdownStarted, Stopwatch.GetTimestamp(), 0); shutdownSignal.TrySetResult(); }
    void Stop() { BeginShutdown(); intake.Cancel(); operation.Cancel(); requests.Writer.TryComplete(); }
    TimeSpan Remaining()
    {
        long started = Interlocked.Read(ref shutdownStarted);
        return started != 0 ? TimeSpan.FromMilliseconds(Math.Max(1, (deadline - Stopwatch.GetElapsedTime(started)).TotalMilliseconds)) : deadline;
    }
    async Task DisposeBounded()
    {
        if (service is null) return;
        try { await service.DisposeAsync().AsTask().WaitAsync(Remaining()).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException) { Log("ShutdownUnknown"); exitCode = 75; }
    }
    void Log(string code) { if (Interlocked.Increment(ref diagnosticLines) <= 32) lock (diagnostics) diagnostics.WriteLine("backend:" + code[..Math.Min(code.Length, 80)]); }
}
