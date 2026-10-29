using System.IO.Pipes;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

// Descriptor data selects a pending mode, but provides no authority. The live
// kernel-authenticated bootstrap must supply the immutable ticket on its pipe.
internal sealed class StartupAdoption(
    StartupAdoptionDescriptor descriptor,
    ParentProof parent,
    Func<Task<OperationResult>>? testBoundary = null
)
{
    Task<OperationResult>? attempt;
    volatile bool finished;
    internal bool Pending => !finished;

    readonly object lane = new();

    internal Task<OperationResult> RunAsync(ManagerService manager)
    {
        lock (lane)
            return attempt ??= RunOnce(manager);
    }

    async Task<OperationResult> RunOnce(ManagerService manager)
    {
        var result = await (
            testBoundary is null ? RunCore(manager) : testBoundary()
        ).ConfigureAwait(false);
        // The task has no more effects to dispatch. Unknown remains in the domain's
        // durable recovery barrier, while the UI can now request explicit recovery.
        finished = true;
        return result;
    }

    internal Task<OperationResult> StatusAsync()
    {
        lock (lane)
            return attempt
                ?? Task.FromResult(
                    new OperationResult(Outcome.Pending, ReasonCode.PendingOperation)
                );
    }

    async Task<OperationResult> RunCore(ManagerService manager)
    {
        bool accepted = false;
        try
        {
            if (parent.AuthorityMode != AuthorityMode.Administrator)
                throw new ProtocolException("ParentMismatch");
            using var bootstrap = new NativeProcessIdentity(descriptor.BootstrapPid);
            using var self = new NativeProcessIdentity(Environment.ProcessId);
            if (
                !self.TryRead(out var own)
                || !bootstrap.TryRead(out var peer)
                || !bootstrap.TryReadSecurity(out var security)
                || !StartupAdoptionPeer.Bootstrap(
                    descriptor,
                    parent,
                    own,
                    peer,
                    security,
                    ScriptPaths.Fingerprint(own.Executable),
                    ScriptPaths.Fingerprint(peer.Executable)
                )
            )
                throw new ProtocolException("ParentMismatch");
            using var pipe = new NamedPipeClientStream(
                ".",
                StartupAdoptionWire.PipeName(descriptor.Nonce),
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (
                !ElevationNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)
                || pid != peer.Pid
                || !bootstrap.Matches(ElevationNative.ToIdentity(peer))
            )
                throw new ProtocolException("ParentMismatch");
            await StartupAdoptionWire
                .Send(
                    pipe,
                    new(
                        "AdoptionReady",
                        descriptor.OperationId,
                        descriptor.Nonce,
                        TicketHash: descriptor.TicketHash
                    ),
                    deadline.Token
                )
                .ConfigureAwait(false);
            var delivery = await StartupAdoptionWire
                .Receive(pipe, deadline.Token)
                .ConfigureAwait(false);
            StartupAdoptionWire.Check(delivery, "Adopt", descriptor);
            if (
                delivery.Ticket is not { } ticket
                || StartupContinuationStore.Hash(ticket) != descriptor.TicketHash
                || ticket.OperationId != descriptor.OperationId
                || ticket.UserSid != own.Sid
                || ticket.SessionId != own.Session
                || !bootstrap.Matches(ElevationNative.ToIdentity(peer))
            )
                throw new ProtocolException("NotAuthenticated");
            var authority = await manager
                .AcceptStartupContinuationAsync(ticket)
                .ConfigureAwait(false);
            if (authority is null)
            {
                var refused = new OperationResult(
                    Outcome.Failed,
                    ReasonCode.IdentityChanged,
                    "A continuação não foi aceita: o estado mudou ou essa solicitação já foi recebida. Nenhum pedido foi repetido."
                );
                await StartupAdoptionWire
                    .Send(
                        pipe,
                        new(
                            "AdoptionRefused",
                            descriptor.OperationId,
                            descriptor.Nonce,
                            TicketHash: descriptor.TicketHash,
                            Result: refused
                        ),
                        deadline.Token
                    )
                    .ConfigureAwait(false);
                finished = true;
                return refused;
            }

            accepted = true;
            // Accepted is durable before this acknowledgement and before effects.
            // Losing this acknowledgement never makes RunAsync dispatch again.
            try
            {
                await StartupAdoptionWire
                    .Send(
                        pipe,
                        new(
                            "AdoptionAccepted",
                            descriptor.OperationId,
                            descriptor.Nonce,
                            TicketHash: descriptor.TicketHash
                        ),
                        deadline.Token
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ElevationNative.IsExpectedFailure(ex)) { }

            using var executionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await manager
                .ResumeNormalLoginAsync(authority, executionDeadline.Token)
                .ConfigureAwait(false);
            var state = await manager
                .StartupContinuationStatusAsync(ticket.OperationId)
                .ConfigureAwait(false);
            if (
                state?.Phase
                is not (StartupContinuationPhase.Completed or StartupContinuationPhase.Partial)
            )
                result = new(
                    Outcome.Unknown,
                    ReasonCode.PendingOperation,
                    "A continuação pode ter iniciado scripts. Consulte as pendências; o pedido não será repetido."
                );
            finished = result.Outcome != Outcome.Unknown;
            try
            {
                using var resultDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await StartupAdoptionWire
                    .Send(
                        pipe,
                        new(
                            "AdoptionResult",
                            descriptor.OperationId,
                            descriptor.Nonce,
                            TicketHash: descriptor.TicketHash,
                            Result: result
                        ),
                        resultDeadline.Token
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ElevationNative.IsExpectedFailure(ex)) { }

            return result;
        }
        catch (Exception ex) when (ElevationNative.IsExpectedFailure(ex))
        {
            // Without a proved acceptance, this peer may not infer or recreate one
            // from the recovery file. After acceptance, any interruption is unknown.
            finished = !accepted;
            return new(
                accepted ? Outcome.Unknown : Outcome.Failed,
                accepted ? ReasonCode.PendingOperation : ReasonCode.IdentityChanged,
                accepted
                    ? "A continuação não pôde ser confirmada. Não repita o pedido; verifique as pendências."
                    : "Não foi possível autenticar a continuação. Nenhum script foi iniciado por este pedido."
            );
        }
    }
}
