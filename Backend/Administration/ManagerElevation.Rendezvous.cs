using System.ComponentModel;
using System.Diagnostics;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Backend;

// This worker uses the coordinator's existing lock and retained native handles.
// It never becomes a second ticket owner or retries a native launch.
internal sealed partial class ManagerElevation
{
    async Task RunNormalAsync()
    {
        try
        {
            lock (sync)
                if (cancelled || disposed)
                    throw new OperationCanceledException();
            if (prepareBoundary is not null)
                prepareBoundary();
            else
            {
                files = new(backend, host, archive);
                pipe = ElevationNative.CreateServer(
                    "Bezoro.AhkManager.Administration." + nonce,
                    parent.UserSid
                );
            }

            var connect = pipe?.WaitForConnectionAsync();
            var info = new ProcessStartInfo(backend)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(backend)!,
            };
            info.Arguments =
                "--administrator-bootstrap "
                + operationId.ToString("D")
                + " "
                + nonce
                + " "
                + Environment.ProcessId
                + " "
                + parent.ChildCreated
                + " "
                + parent.ParentPid
                + " "
                + parent.ParentCreated;
            lock (sync)
            {
                if (cancelled || disposed)
                    throw new OperationCanceledException();
                // Cancellation before this point proves no dispatch. Afterwards, a caller
                // must retain the ticket until refusal or candidate exit is established.
                dispatchStarted = true;
            }

            candidateProcess = dispatchBoundary is null ? Process.Start(info) : dispatchBoundary();
            if (candidateProcess is null)
                throw new ProtocolException("LaunchUncertain");
            var sessionFiles = files ?? throw new ProtocolException("LaunchUncertain");
            var sessionPipe = pipe ?? throw new ProtocolException("LaunchUncertain");
            var connection = connect ?? throw new ProtocolException("LaunchUncertain");
            // Access Process.Handle now: it is the ShellExecute creation receipt, not a JSON PID.
            if (
                !ProcessNative.GetProcessTimes(
                    candidateProcess.SafeHandle,
                    out long createdAt,
                    out _,
                    out _,
                    out _
                )
            )
                throw new ProtocolException("IdentityChanged");
            candidate = new(candidateProcess.Id);
            if (
                !candidate.TryRead(out candidateStamp)
                || candidateStamp.Created != createdAt
                || candidateStamp.Sid != parent.UserSid
                || candidateStamp.Session != parent.SessionId
                || !ScriptPaths.Equal(candidateStamp.Executable, backend)
                || !candidate.TryReadSecurity(out var security)
                || ParentGuard.TokenMode(security) != AuthorityMode.Administrator
                || !sessionFiles.Valid()
            )
                throw new ProtocolException("ParentMismatch");
            lock (sync)
                if (cancelled || disposed)
                {
                    sessionPipe.Dispose();
                    throw new OperationCanceledException();
                }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await connection.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (
                !ElevationNative.GetNamedPipeClientProcessId(
                    sessionPipe.SafePipeHandle,
                    out uint peer
                )
                || peer != candidateStamp.Pid
                || !candidate.Matches(ToIdentity(candidateStamp))
            )
                throw new ProtocolException("ParentMismatch");
            var message = await Receive(sessionPipe, deadline.Token).ConfigureAwait(false);
            Check(message, "Ready");
            if (!ObserveReady())
                throw new OperationCanceledException();
            // One reader owns this pipe. A commit frame is written only by Commit, never by status.
            message = await Receive(sessionPipe, deadline.Token).ConfigureAwait(false);
            Check(message, "CommitAck");
            if (
                !committed
                || message.ReceiptHash != receiptHash
                || message.FileIdentity != receiptIdentity
            )
                throw new ProtocolException("InvalidRequest");
            if (!ObserveAcknowledgement(receiptId))
                throw new ProtocolException("InvalidRequest");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ObserveDenied();
        }
        catch (Exception ex) when (Known(ex))
        {
            lock (sync)
            {
                if (!dispatchStarted)
                {
                    Set(
                        cancelled || ex is OperationCanceledException
                            ? ElevationState.Cancelled
                            : ElevationState.Failed,
                        cancelled || ex is OperationCanceledException
                            ? ReasonCode.Cancelled
                            : ReasonCode.DispatchFailed,
                        true
                    );
                    pipe?.Dispose();
                    files?.Dispose();
                    files = null;
                    return;
                }
            }

            bool exited = candidate is not null && candidate.Exited;
            Set(
                cancelled ? ElevationState.Cancelled
                    : ex is OperationCanceledException ? ElevationState.Expired
                    : ElevationState.Unknown,
                cancelled ? ReasonCode.Cancelled : ReasonCode.LaunchUncertain,
                exited
            );
            pipe?.Dispose();
            if (candidate is not null && !exited)
            {
                bool gone = await candidate
                    .WaitForExitAsync(TimeSpan.FromSeconds(65))
                    .ConfigureAwait(false);
                if (gone)
                    Set(
                        cancelled ? ElevationState.Cancelled : ElevationState.Failed,
                        cancelled ? ReasonCode.Cancelled : ReasonCode.DispatchFailed,
                        true
                    );
            }
        }
    }

    void Check(ElevationFrame frame, string kind)
    {
        if (frame.Kind != kind || frame.OperationId != operationId || frame.Nonce != nonce)
            throw new ProtocolException("NotAuthenticated");
    }
}
