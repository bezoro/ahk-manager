using System.IO.Pipes;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

internal sealed class StartupAdoptionServer : IDisposable
{
    readonly NamedPipeServerStream pipe;
    readonly StartupContinuationTicket ticket;
    readonly string backendPath,
        hostPath,
        backendHash;
    readonly ProcessStamp bootstrap;
    ProcessStamp? launchedHost;
    internal readonly StartupAdoptionDescriptor Descriptor;

    internal StartupAdoptionServer(
        StartupContinuationTicket ticket,
        string nonce,
        ProcessStamp bootstrap,
        string backend,
        string host,
        string hash
    )
    {
        this.ticket = ticket;
        this.bootstrap = bootstrap;
        backendPath = backend;
        hostPath = host;
        backendHash = hash;
        Descriptor = new(
            ticket.OperationId,
            nonce,
            bootstrap.Pid,
            bootstrap.Created,
            StartupContinuationStore.Hash(ticket)
        );
        pipe = ElevationNative.CreateServer(StartupAdoptionWire.PipeName(nonce), bootstrap.Sid);
    }

    internal void HostLaunched(ProcessStamp host) => launchedHost = host;

    internal async Task<OperationResult> DeliverAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        if (
            launchedHost is not { } expected
            || !ElevationNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peerPid)
        )
            throw new ProtocolException("ParentMismatch");
        using var peer = new NativeProcessIdentity(checked((int)peerPid));
        using var host = new NativeProcessIdentity(expected.Pid);
        if (
            !peer.TryRead(out var backend)
            || !host.TryRead(out var actualHost)
            || !host.TryReadSecurity(out var hostSecurity)
            || !peer.TryReadSecurity(out var security)
            || !StartupAdoptionPeer.Backend(
                bootstrap,
                expected,
                actualHost,
                backend,
                hostSecurity,
                security,
                ElevationNative.ActualParent(backend.Pid),
                hostPath,
                backendPath,
                backendHash,
                ScriptPaths.Fingerprint(backendPath)
            )
        )
            throw new ProtocolException("ParentMismatch");
        var ready = await StartupAdoptionWire.Receive(pipe, deadline.Token).ConfigureAwait(false);
        StartupAdoptionWire.Check(ready, "AdoptionReady", Descriptor);
        if (
            !peer.Matches(ElevationNative.ToIdentity(backend))
            || !host.Matches(ElevationNative.ToIdentity(expected))
        )
            throw new ProtocolException("IdentityChanged");
        await StartupAdoptionWire
            .Send(
                pipe,
                new("Adopt", ticket.OperationId, Descriptor.Nonce, ticket, Descriptor.TicketHash),
                deadline.Token
            )
            .ConfigureAwait(false);
        var accepted = await StartupAdoptionWire
            .Receive(pipe, deadline.Token)
            .ConfigureAwait(false);
        if (accepted.Kind == "AdoptionRefused")
        {
            StartupAdoptionWire.Check(accepted, "AdoptionRefused", Descriptor);
            return accepted.Result ?? new(Outcome.Failed, ReasonCode.IdentityChanged);
        }

        StartupAdoptionWire.Check(accepted, "AdoptionAccepted", Descriptor);
        var result = await StartupAdoptionWire.Receive(pipe, deadline.Token).ConfigureAwait(false);
        StartupAdoptionWire.Check(result, "AdoptionResult", Descriptor);
        return result.Result ?? new(Outcome.Unknown, ReasonCode.PendingOperation);
    }

    public void Dispose() => pipe.Dispose();
}
