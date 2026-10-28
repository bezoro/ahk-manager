using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.Backend;

public sealed record StartupAdoptionFrame(string Kind, Guid OperationId, string Nonce, StartupContinuationTicket? Ticket = null, string? TicketHash = null, OperationResult? Result = null);
internal sealed record StartupAdoptionDescriptor(Guid OperationId, string Nonce, int BootstrapPid, long BootstrapCreated, string TicketHash)
{
    internal string Encode() => string.Join('|', OperationId.ToString("D"), Nonce, BootstrapPid.ToString(CultureInfo.InvariantCulture), BootstrapCreated.ToString(CultureInfo.InvariantCulture), TicketHash);
    internal static StartupAdoptionDescriptor Parse(string value)
    {
        var parts = value.Split('|');
        if (parts.Length != 5 || !Guid.TryParseExact(parts[0], "D", out var id) || id == Guid.Empty || !Hex(parts[1]) || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0 || !Hex(parts[4])) throw new ProtocolException("NotAuthenticated");
        return new(id, parts[1], pid, created, parts[4]);
    }
    static bool Hex(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class StartupAdoptionWire
{
    internal static async Task Send(Stream pipe, StartupAdoptionFrame frame, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Protocol.Json.StartupAdoptionFrame);
        if (bytes.Length > StartupContinuationStore.MaximumBytes) throw new ProtocolException("FrameTooLarge");
        await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), ct).ConfigureAwait(false); await pipe.WriteAsync(bytes, ct).ConfigureAwait(false); await pipe.FlushAsync(ct).ConfigureAwait(false);
    }
    internal static async Task<StartupAdoptionFrame> Receive(Stream pipe, CancellationToken ct)
    {
        byte[] prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix, ct).ConfigureAwait(false); int length = BitConverter.ToInt32(prefix);
        if (length is < 2 or > StartupContinuationStore.MaximumBytes) throw new ProtocolException("FrameTooLarge");
        byte[] bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(bytes); Protocol.RejectDuplicates(parsed.RootElement);
        return JsonSerializer.Deserialize(bytes, Protocol.Json.StartupAdoptionFrame) ?? throw new ProtocolException("NotAuthenticated");
    }
    internal static void Check(StartupAdoptionFrame frame, string kind, StartupAdoptionDescriptor descriptor)
    {
        if (frame.Kind != kind || frame.OperationId != descriptor.OperationId || frame.Nonce != descriptor.Nonce || frame.TicketHash != descriptor.TicketHash) throw new ProtocolException("NotAuthenticated");
    }
    internal static string PipeName(string nonce) => "Bezoro.AhkManager.StartupAdoption." + nonce;
}

internal static class StartupAdoptionPeer
{
    internal static bool Bootstrap(StartupAdoptionDescriptor descriptor, ParentProof parent, ProcessStamp own, ProcessStamp peer, ProcessSecurity security, string ownHash, string peerHash) => parent.AuthorityMode == AuthorityMode.Administrator && own.Pid == parent.ChildPid && own.Created == parent.ChildCreated && own.Sid == parent.UserSid && own.Session == parent.SessionId && peer.Pid == descriptor.BootstrapPid && peer.Created == descriptor.BootstrapCreated && peer.Sid == own.Sid && peer.Session == own.Session && ScriptPaths.Equal(peer.Executable, own.Executable) && ParentGuard.TokenMode(security) == AuthorityMode.Administrator && ownHash == peerHash;
    internal static bool Backend(ProcessStamp bootstrap, ProcessStamp expectedHost, ProcessStamp actualHost, ProcessStamp backend, ProcessSecurity hostSecurity, ProcessSecurity backendSecurity, int actualParent, string hostPath, string backendPath, string expectedHash, string actualHash) => actualHost == expectedHost && expectedHost.Sid == bootstrap.Sid && expectedHost.Session == bootstrap.Session && ScriptPaths.Equal(expectedHost.Executable, hostPath) && backend.Sid == bootstrap.Sid && backend.Session == bootstrap.Session && ScriptPaths.Equal(backend.Executable, backendPath) && actualParent == expectedHost.Pid && ParentGuard.TokenMode(hostSecurity) == AuthorityMode.Administrator && ParentGuard.TokenMode(backendSecurity) == AuthorityMode.Administrator && expectedHash == actualHash;
}

// Descriptor data selects a pending mode, but provides no authority. The live
// kernel-authenticated bootstrap must supply the immutable ticket on its pipe.
internal sealed class StartupAdoption(StartupAdoptionDescriptor descriptor, ParentProof parent, Func<Task<OperationResult>>? testBoundary = null)
{
    Task<OperationResult>? attempt;
    volatile bool finished;
    internal bool Pending => !finished;
    readonly object lane = new();
    internal Task<OperationResult> RunAsync(ManagerService manager) { lock (lane) return attempt ??= RunOnce(manager); }
    async Task<OperationResult> RunOnce(ManagerService manager)
    {
        var result = await (testBoundary is null ? RunCore(manager) : testBoundary()).ConfigureAwait(false);
        // The task has no more effects to dispatch. Unknown remains in the domain's
        // durable recovery barrier, while the UI can now request explicit recovery.
        finished = true;
        return result;
    }
    internal Task<OperationResult> StatusAsync() { lock (lane) return attempt ?? Task.FromResult(new OperationResult(Outcome.Pending, ReasonCode.PendingOperation)); }
    async Task<OperationResult> RunCore(ManagerService manager)
    {
        bool accepted = false;
        try
        {
            if (parent.AuthorityMode != AuthorityMode.Administrator) throw new ProtocolException("ParentMismatch");
            using var bootstrap = new NativeProcessIdentity(descriptor.BootstrapPid);
            using var self = new NativeProcessIdentity(Environment.ProcessId);
            if (!self.TryRead(out var own) || !bootstrap.TryRead(out var peer) || !bootstrap.TryReadSecurity(out var security) || !StartupAdoptionPeer.Bootstrap(descriptor, parent, own, peer, security, ScriptPaths.Fingerprint(own.Executable), ScriptPaths.Fingerprint(peer.Executable))) throw new ProtocolException("ParentMismatch");
            using var pipe = new NamedPipeClientStream(".", StartupAdoptionWire.PipeName(descriptor.Nonce), PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (!ElevationNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != peer.Pid || !bootstrap.Matches(ManagerElevation.ToIdentity(peer))) throw new ProtocolException("ParentMismatch");
            await StartupAdoptionWire.Send(pipe, new("AdoptionReady", descriptor.OperationId, descriptor.Nonce, TicketHash: descriptor.TicketHash), deadline.Token).ConfigureAwait(false);
            var delivery = await StartupAdoptionWire.Receive(pipe, deadline.Token).ConfigureAwait(false); StartupAdoptionWire.Check(delivery, "Adopt", descriptor);
            if (delivery.Ticket is not { } ticket || StartupContinuationStore.Hash(ticket) != descriptor.TicketHash || ticket.OperationId != descriptor.OperationId || ticket.UserSid != own.Sid || ticket.SessionId != own.Session || !bootstrap.Matches(ManagerElevation.ToIdentity(peer))) throw new ProtocolException("NotAuthenticated");
            var authority = await manager.AcceptStartupContinuationAsync(ticket).ConfigureAwait(false);
            if (authority is null)
            {
                var refused = new OperationResult(Outcome.Failed, ReasonCode.IdentityChanged, "A continuação não foi aceita: o estado mudou ou essa solicitação já foi recebida. Nenhum pedido foi repetido.");
                await StartupAdoptionWire.Send(pipe, new("AdoptionRefused", descriptor.OperationId, descriptor.Nonce, TicketHash: descriptor.TicketHash, Result: refused), deadline.Token).ConfigureAwait(false);
                finished = true; return refused;
            }
            accepted = true;
            // Accepted is durable before this acknowledgement and before effects.
            // Losing this acknowledgement never makes RunAsync dispatch again.
            try { await StartupAdoptionWire.Send(pipe, new("AdoptionAccepted", descriptor.OperationId, descriptor.Nonce, TicketHash: descriptor.TicketHash), deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ManagerElevation.Known(ex)) { }
            using var executionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await manager.ResumeNormalLoginAsync(authority, executionDeadline.Token).ConfigureAwait(false);
            var state = await manager.StartupContinuationStatusAsync(ticket.OperationId).ConfigureAwait(false);
            if (state?.Phase is not (StartupContinuationPhase.Completed or StartupContinuationPhase.Partial)) result = new(Outcome.Unknown, ReasonCode.PendingOperation, "A continuação pode ter iniciado scripts. Consulte as pendências; o pedido não será repetido.");
            finished = result.Outcome != Outcome.Unknown;
            try { using var resultDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await StartupAdoptionWire.Send(pipe, new("AdoptionResult", descriptor.OperationId, descriptor.Nonce, TicketHash: descriptor.TicketHash, Result: result), resultDeadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ManagerElevation.Known(ex)) { }
            return result;
        }
        catch (Exception ex) when (ManagerElevation.Known(ex))
        {
            // Without a proved acceptance, this peer may not infer or recreate one
            // from the recovery file. After acceptance, any interruption is unknown.
            finished = !accepted;
            return new(accepted ? Outcome.Unknown : Outcome.Failed, accepted ? ReasonCode.PendingOperation : ReasonCode.IdentityChanged, accepted ? "A continuação não pôde ser confirmada. Não repita o pedido; verifique as pendências." : "Não foi possível autenticar a continuação. Nenhum script foi iniciado por este pedido.");
        }
    }
}

internal sealed class StartupAdoptionServer : IDisposable
{
    readonly NamedPipeServerStream pipe;
    readonly StartupContinuationTicket ticket;
    readonly string backendPath, hostPath, backendHash;
    readonly ProcessStamp bootstrap;
    ProcessStamp? launchedHost;
    internal readonly StartupAdoptionDescriptor Descriptor;
    internal StartupAdoptionServer(StartupContinuationTicket ticket, string nonce, ProcessStamp bootstrap, string backend, string host, string hash)
    {
        this.ticket = ticket; this.bootstrap = bootstrap; backendPath = backend; hostPath = host; backendHash = hash;
        Descriptor = new(ticket.OperationId, nonce, bootstrap.Pid, bootstrap.Created, StartupContinuationStore.Hash(ticket));
        pipe = ElevationNative.CreateServer(StartupAdoptionWire.PipeName(nonce), bootstrap.Sid);
    }
    internal void HostLaunched(ProcessStamp host) => launchedHost = host;
    internal async Task<OperationResult> DeliverAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        if (launchedHost is not { } expected || !ElevationNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peerPid)) throw new ProtocolException("ParentMismatch");
        using var peer = new NativeProcessIdentity(checked((int)peerPid)); using var host = new NativeProcessIdentity(expected.Pid);
        if (!peer.TryRead(out var backend) || !host.TryRead(out var actualHost) || !host.TryReadSecurity(out var hostSecurity) || !peer.TryReadSecurity(out var security) || !StartupAdoptionPeer.Backend(bootstrap, expected, actualHost, backend, hostSecurity, security, ManagerElevation.ActualParent(backend.Pid), hostPath, backendPath, backendHash, ScriptPaths.Fingerprint(backendPath))) throw new ProtocolException("ParentMismatch");
        var ready = await StartupAdoptionWire.Receive(pipe, deadline.Token).ConfigureAwait(false); StartupAdoptionWire.Check(ready, "AdoptionReady", Descriptor);
        if (!peer.Matches(ManagerElevation.ToIdentity(backend)) || !host.Matches(ManagerElevation.ToIdentity(expected))) throw new ProtocolException("IdentityChanged");
        await StartupAdoptionWire.Send(pipe, new("Adopt", ticket.OperationId, Descriptor.Nonce, ticket, Descriptor.TicketHash), deadline.Token).ConfigureAwait(false);
        var accepted = await StartupAdoptionWire.Receive(pipe, deadline.Token).ConfigureAwait(false);
        if (accepted.Kind == "AdoptionRefused") { StartupAdoptionWire.Check(accepted, "AdoptionRefused", Descriptor); return accepted.Result ?? new(Outcome.Failed, ReasonCode.IdentityChanged); }
        StartupAdoptionWire.Check(accepted, "AdoptionAccepted", Descriptor);
        var result = await StartupAdoptionWire.Receive(pipe, deadline.Token).ConfigureAwait(false); StartupAdoptionWire.Check(result, "AdoptionResult", Descriptor);
        return result.Result ?? new(Outcome.Unknown, ReasonCode.PendingOperation);
    }
    public void Dispose() => pipe.Dispose();
}
