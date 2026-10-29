using System.Diagnostics;
using System.IO.Pipes;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using static Bezoro.AhkManager.Backend.ElevationActivation;
using static Bezoro.AhkManager.Backend.ElevationNative;
using static Bezoro.AhkManager.Backend.ElevationReceiptStore;
using static Bezoro.AhkManager.Backend.ElevationWire;

namespace Bezoro.AhkManager.Backend;

internal static class ElevationBootstrap
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var request = ElevationBootstrapRequest.Parse(args);
        string nonce = request.Nonce,
            executable = ScriptPaths.Canonical(Environment.ProcessPath!, false);
        string hostPath = ScriptPaths.Canonical(
            Path.Combine(Path.GetDirectoryName(executable)!, "..", "Bezoro.AhkManager.exe"),
            false
        );
        using var self = new NativeProcessIdentity(Environment.ProcessId);
        using var normal = new NativeProcessIdentity(request.NormalBackendPid);
        using var host = new NativeProcessIdentity(request.NormalHostPid);
        if (
            !self.TryRead(out var own)
            || !normal.TryRead(out var normalStamp)
            || !host.TryRead(out var hostStamp)
            || own.Sid == "S-1-5-18"
            || own.Sid != normalStamp.Sid
            || own.Sid != hostStamp.Sid
            || own.Session != normalStamp.Session
            || own.Session != hostStamp.Session
            || normalStamp.Created != request.NormalBackendCreated
            || hostStamp.Created != request.NormalHostCreated
            || !ScriptPaths.Equal(normalStamp.Executable, executable)
            || !ScriptPaths.Equal(hostStamp.Executable, hostPath)
            || !self.TryReadSecurity(out var security)
            || ParentGuard.TokenMode(security) != AuthorityMode.Administrator
            || !normal.TryReadSecurity(out var lower)
            || ParentGuard.TokenMode(lower) != AuthorityMode.Normal
            || !host.TryReadSecurity(out lower)
            || ParentGuard.TokenMode(lower) != AuthorityMode.Normal
            || ActualParent(request.NormalBackendPid) != request.NormalHostPid
        )
            throw new ProtocolException("ParentMismatch");
        using var files = new ElevationFiles(
            executable,
            hostPath,
            Path.Combine(Path.GetDirectoryName(hostPath)!, "resources", "app.asar")
        );
        using var pipe = new NamedPipeClientStream(
            ".",
            "Bezoro.AhkManager.Administration." + nonce,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        using var beforeCommit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(beforeCommit.Token).ConfigureAwait(false);
        if (
            !ElevationNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer)
            || peer != request.NormalBackendPid
            || !normal.Matches(ToIdentity(normalStamp))
            || !host.Matches(ToIdentity(hostStamp))
            || !files.Valid()
        )
            throw new ProtocolException("ParentMismatch");
        await Send(pipe, new("Ready", request.OperationId, nonce), beforeCommit.Token)
            .ConfigureAwait(false);
        // Any EOF before this authenticated Commit aborts. A receipt file alone has no authority.
        ElevationFrame commit = await Receive(pipe, beforeCommit.Token).ConfigureAwait(false);
        if (
            commit.Kind != "Commit"
            || commit.OperationId != request.OperationId
            || commit.Nonce != nonce
            || commit.ReceiptHash is not { Length: 64 }
            || commit.FileIdentity is null
            || !normal.Matches(ToIdentity(normalStamp))
            || !host.Matches(ToIdentity(hostStamp))
        )
            throw new ProtocolException("NotAuthenticated");
        string directory = ScriptPaths.LocalPath(CoordinationPath(nonce)),
            path = ScriptPaths.Canonical(Path.Combine(directory, "commit.json"), false);
        using var receiptLease = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        var receipt = ElevationReceiptStore.ReadCommitted(
            receiptLease,
            commit,
            request,
            nonce,
            normalStamp,
            hostStamp,
            own,
            files
        );
        if (commit.BackgroundStartup && receipt.StartupContinuation is null)
            throw new ProtocolException("InvalidRequest");
        using var adoption = receipt.StartupContinuation is { } continuationTicket
            ? new StartupAdoptionServer(
                continuationTicket,
                nonce,
                own,
                executable,
                hostPath,
                files.BackendHash
            )
            : null;
        // From here EOF/normal-parent exit is expected, including a lost Ack response.
        // Authenticated commit is pinned. Never revert to the pre-commit EOF rule.
        try
        {
            using var ackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Send(
                    pipe,
                    new(
                        "CommitAck",
                        request.OperationId,
                        nonce,
                        commit.ReceiptHash,
                        commit.FileIdentity
                    ),
                    ackDeadline.Token
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ElevationNative.IsExpectedFailure(ex)) { }

        async Task<bool> WaitExit(TimeSpan limit)
        {
            var elapsed = Stopwatch.StartNew();
            while (!normal.Exited || !host.Exited)
            {
                if (elapsed.Elapsed >= limit || receipt.ExpiresAt <= DateTimeOffset.UtcNow)
                    return false;
                await Task.Delay(100).ConfigureAwait(false);
            }

            return true;
        }

        async Task<bool> WaitLocks(TimeSpan limit)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ProfileLocksAvailable(receipt.ProfileRoot, own.Sid, receipt.ChromiumRoot))
            {
                if (elapsed.Elapsed >= limit || receipt.ExpiresAt <= DateTimeOffset.UtcNow)
                    return false;
                await Task.Delay(100).ConfigureAwait(false);
            }

            return true;
        }

        bool Consume()
        {
            using var consume = new FileStream(
                Path.Combine(directory, "consumed"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            );
            consume.Write(receipt.ReceiptId.ToByteArray());
            consume.Flush(true);
            return true;
        }

        bool Launch()
        {
            using var launched = Process.Start(
                CommittedHostStartInfo(
                    hostPath,
                    adoption?.Descriptor.Encode(),
                    receipt.ResumeLogin,
                    commit.BackgroundStartup
                )
            );
            if (
                launched is null
                || !ProcessNative.GetProcessTimes(
                    launched.SafeHandle,
                    out long launchedAt,
                    out _,
                    out _,
                    out _
                )
            )
                return false;
            using var delivered = new NativeProcessIdentity(launched.Id);
            bool valid =
                delivered.TryRead(out var stamp)
                && stamp.Created == launchedAt
                && ScriptPaths.Equal(stamp.Executable, hostPath)
                && stamp.Sid == own.Sid
                && stamp.Session == own.Session
                && delivered.TryReadSecurity(out var actual)
                && ParentGuard.TokenMode(actual) == AuthorityMode.Administrator;
            if (valid)
                adoption?.HostLaunched(stamp);
            return valid;
        }

        int result = await ActivateCommitted(
                WaitExit,
                WaitLocks,
                () => files.Valid() && receipt.ExpiresAt > DateTimeOffset.UtcNow,
                Consume,
                Launch
            )
            .ConfigureAwait(false);
        if (result == 0 && adoption is not null)
        {
            try
            {
                var continuationResult = await adoption.DeliverAsync().ConfigureAwait(false);
                result = continuationResult.Outcome == Outcome.Unknown ? 75 : 0;
            }
            catch (Exception ex) when (ElevationNative.IsExpectedFailure(ex))
            {
                result = 75;
            }
        }

        ReportActivationFailure(
            result,
            normal.Exited,
            host.Exited,
            receipt.ResumeLogin || commit.BackgroundStartup,
            ElevationNative.RecoveryError
        );
        return result;
    }
}
