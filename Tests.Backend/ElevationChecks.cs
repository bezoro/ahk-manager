using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Tests.Backend;

internal static class ElevationChecks
{
    // Read-only Windows query deliberately rejects its oversized buffer. Its only purpose is
    // to exercise the out-struct marshaller without creating a process or changing priority.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetProcessInformation")]
    static extern bool ReadOnlyOutStructProbe(
        nint process,
        int kind,
        out LaunchNative.ProcessInfo info,
        uint size
    );

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static ParentProof Parent =>
        new(
            100,
            1,
            @"C:\fixture\Bezoro.AhkManager.exe",
            200,
            2,
            "S-1-5-21-fixture",
            1,
            AuthorityMode.Normal
        );

    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(
            ParentGuard.TokenMode(new(false, 0x2000)) == AuthorityMode.Normal
                && ParentGuard.TokenMode(new(true, 0x3000)) == AuthorityMode.Administrator,
            "actual medium/high token modes have explicit authority"
        );
        foreach (
            var security in new[]
            {
                new ProcessSecurity(true, 0x2000),
                new(false, 0x3000),
                new(true, 0x4000),
                new(false, 0),
                new(false, 0x1000),
            }
        )
            check(
                ParentGuard.TokenMode(security) is null,
                "mixed unknown low or SYSTEM integrity is never admitted"
            );
        bool missing = false;
        try
        {
            _ = JsonSerializer.Deserialize(
                "{\"parentPid\":100,\"parentCreated\":\"1\",\"parentExecutable\":\"C:\\\\fixture\\\\Manager.exe\",\"childPid\":200,\"childCreated\":\"2\",\"userSid\":\"S-1-fixture\",\"sessionId\":1}",
                Protocol.Json.ParentProof
            );
        }
        catch (JsonException)
        {
            missing = true;
        }
        check(
            missing,
            "parent authority field is required rather than guessed from launch arguments"
        );
        var parentStamp = new ProcessStamp(10, 100, @"C:\fixture\Manager.exe", "S-1-fixture", 1);
        var childStamp = parentStamp with { Pid = 20, Created = 200 };
        foreach (var mode in new[] { AuthorityMode.Normal, AuthorityMode.Administrator })
            check(
                ParentGuard.Compatible(
                    parentStamp,
                    childStamp,
                    mode,
                    mode,
                    parentStamp.Executable,
                    out var actual
                )
                    && actual == mode,
                "actual matched authority pair accepted " + mode
            );
        check(
            !ParentGuard.Compatible(
                parentStamp,
                childStamp,
                AuthorityMode.Normal,
                AuthorityMode.Administrator,
                parentStamp.Executable,
                out _
            )
                && !ParentGuard.Compatible(
                    parentStamp,
                    childStamp,
                    null,
                    null,
                    parentStamp.Executable,
                    out _
                ),
            "mixed or unreadable authority pairs rejected"
        );
        foreach (
            var invalid in new[]
            {
                childStamp with
                {
                    Sid = "S-1-other",
                },
                childStamp with
                {
                    Session = 2,
                },
                childStamp with
                {
                    Created = 99,
                },
                childStamp with
                {
                    Pid = 10,
                },
            }
        )
            check(
                !ParentGuard.Compatible(
                    parentStamp,
                    invalid,
                    AuthorityMode.Normal,
                    AuthorityMode.Normal,
                    parentStamp.Executable,
                    out _
                ),
                "other user/session/reused creation/parent self cannot authenticate"
            );
        check(
            !ParentGuard.Compatible(
                parentStamp with
                {
                    Sid = "S-1-5-18",
                },
                childStamp with
                {
                    Sid = "S-1-5-18",
                },
                AuthorityMode.Administrator,
                AuthorityMode.Administrator,
                parentStamp.Executable,
                out _
            ),
            "SYSTEM pair rejected even with matching fake modes"
        );
        var shell = new ProcessStamp(30, 100, @"C:\Windows\explorer.exe", parentStamp.Sid, 1);
        check(
            UnelevatedProcessLauncher.CompatibleShell(
                parentStamp,
                shell,
                new(false, 0x2000),
                shell.Executable
            ),
            "same user session Explorer medium token eligible"
        );
        foreach (
            var invalid in new[]
            {
                shell with
                {
                    Sid = "S-1-other",
                },
                shell with
                {
                    Session = 2,
                },
                shell with
                {
                    Executable = @"C:\malicious\explorer.exe",
                },
            }
        )
            check(
                !UnelevatedProcessLauncher.CompatibleShell(
                    parentStamp,
                    invalid,
                    new(false, 0x2000),
                    shell.Executable
                ),
                "normal launcher refuses replaced shell wrong user/session/image"
            );
        check(
            !UnelevatedProcessLauncher.CompatibleShell(
                parentStamp,
                shell,
                new(true, 0x3000),
                shell.Executable
            ),
            "normal launcher refuses high shell without elevated fallback"
        );
        string command = UnelevatedProcessLauncher.BuildCommand("a", new string('b', 1017));
        check(
            command.Length == 1023,
            "token launch accepts documented1023-character pre-NUL boundary"
        );
        bool tooLong = false;
        try
        {
            UnelevatedProcessLauncher.BuildCommand("a", new string('b', 1018));
        }
        catch (InvalidOperationException)
        {
            tooLong = true;
        }
        check(tooLong, "token launch rejects1024 characters before any process creation");
        int processInfoSize = 0;
        try
        {
            processInfoSize =
                System.Runtime.InteropServices.Marshal.SizeOf<LaunchNative.ProcessInfo>();
        }
        catch (ArgumentException) { }
        check(processInfoSize == 24, "PROCESS_INFORMATION has native x64 blittable24-byte layout");
        bool marshalable = true;
        try
        {
            _ = ReadOnlyOutStructProbe(-1, 0, out _, 24);
        }
        catch (System.Runtime.InteropServices.MarshalDirectiveException)
        {
            marshalable = false;
        }
        check(marshalable, "native out PROCESS_INFORMATION marshals before any launch or UAC");
        await TicketAsync(check);
        await ActivationAsync(check);
        await PipeAsync(check);
    }

    static async Task TicketAsync(Action<bool, string> check)
    {
        var uac = Signal();
        int starts = 0,
            commits = 0;
        bool quiescent = true;
        using var ticket = new ManagerElevation(
            Parent,
            () => quiescent,
            async owner =>
            {
                Interlocked.Increment(ref starts);
                await uac.Task;
                if (!owner.ObserveReady())
                    owner.ObserveCandidateExit();
            },
            (owner, ct) =>
            {
                commits++;
                check(
                    owner.ObserveAcknowledgement(owner.Snapshot.ReceiptId!.Value),
                    "authenticated matching receipt acknowledges commit"
                );
                return Task.CompletedTask;
            }
        );
        Guid id = Guid.NewGuid();
        var first = ticket.Begin(id);
        check(
            first.State == ElevationState.PendingApproval
                && !first.CanResumeNormal
                && ticket.Frozen
                && !ticket.ShutdownAllowed,
            "Begin responds pending before the human UAC wait and freezes effects/shutdown"
        );
        ticket.Begin(id);
        await Task.Delay(20);
        check(starts == 1, "same operation ID never dispatches a second UAC");
        bool conflict = false;
        try
        {
            ticket.Begin(Guid.NewGuid());
        }
        catch (ProtocolException)
        {
            conflict = true;
        }
        check(conflict, "different in-flight ID is rejected");
        var pendingCommit = await ticket.Commit(id, CancellationToken.None);
        check(
            pendingCommit.State == ElevationState.PendingApproval && commits == 0,
            "Accepted does not authorize commit or shutdown"
        );
        uac.TrySetResult();
        var ready = await ticket.Status(id, first.Revision, CancellationToken.None);
        if (ready.State != ElevationState.Ready)
            ready = await ticket.Status(id, ready.Revision, CancellationToken.None);
        check(
            ready.State == ElevationState.Ready && !ready.CanResumeNormal,
            "only native candidate readiness advances ticket"
        );
        var committed = await ticket.Commit(id, CancellationToken.None);
        await ticket.Commit(id, CancellationToken.None);
        check(
            committed.State == ElevationState.CommitAcked
                && committed.ReceiptId is not null
                && ticket.ShutdownAllowed
                && commits == 1,
            "commit deduplication retains authenticated receipt and permits quit only after Ack"
        );
        check(
            ticket.Cancel(id).State == ElevationState.CommitAcked,
            "cancel after commit cannot invalidate delivered authority"
        );
        check(
            !ticket.ObserveAcknowledgement(Guid.NewGuid()),
            "wrong receipt cannot acknowledge handoff"
        );

        uac = Signal();
        var captured = uac;
        using var late = new ManagerElevation(
            Parent,
            () => true,
            async owner =>
            {
                await captured.Task;
                if (!owner.ObserveReady())
                    owner.ObserveCandidateExit();
            },
            (_, _) => throw new InvalidOperationException()
        );
        id = Guid.NewGuid();
        first = late.Begin(id);
        var cancelled = late.Cancel(id);
        check(
            cancelled.State == ElevationState.Cancelled && !cancelled.CanResumeNormal,
            "cancel during UAC retains uncertainty until candidate absence is proved"
        );
        captured.TrySetResult();
        var ended = await late.Status(id, cancelled.Revision, CancellationToken.None);
        check(
            ended.State == ElevationState.Cancelled && ended.CanResumeNormal && !late.Frozen,
            "late cancelled approval cannot become Ready or activate"
        );

        using var denied = new ManagerElevation(
            Parent,
            () => true,
            owner =>
            {
                owner.ObserveDenied();
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask
        );
        id = Guid.NewGuid();
        first = denied.Begin(id);
        ended = await denied.Status(id, first.Revision, CancellationToken.None);
        check(
            ended.State == ElevationState.Denied && ended.CanResumeNormal,
            "UAC denial preserves the normal instance"
        );
        using var dirty = new ManagerElevation(
            Parent,
            () => false,
            _ => throw new InvalidOperationException("must not dispatch"),
            (_, _) => Task.CompletedTask
        );
        check(
            dirty.Begin(Guid.NewGuid()).State == ElevationState.Failed
                && dirty.Snapshot.CanResumeNormal,
            "backend quiescence failure prevents UAC dispatch"
        );
        using var high = new ManagerElevation(
            Parent with
            {
                AuthorityMode = AuthorityMode.Administrator,
            },
            () => true,
            _ => throw new InvalidOperationException("must not dispatch"),
            (_, _) => Task.CompletedTask
        );
        check(
            high.Begin(Guid.NewGuid()).State == ElevationState.Failed,
            "already administrator cannot request another runas"
        );

        using var lost = new ManagerElevation(
            Parent,
            () => true,
            owner =>
            {
                owner.ObserveReady();
                return Task.CompletedTask;
            },
            (_, _) => throw new IOException("Commit response unknown")
        );
        id = Guid.NewGuid();
        first = lost.Begin(id);
        ended = await lost.Status(id, first.Revision, CancellationToken.None);
        ended = await lost.Commit(id, CancellationToken.None);
        var receipt = ended.ReceiptId!.Value;
        check(
            ended.State == ElevationState.Unknown
                && !ended.CanResumeNormal
                && !lost.ShutdownAllowed,
            "lost Commit response preserves ticket frozen and never authorizes quit"
        );
        check(
            lost.ObserveAcknowledgement(receipt)
                && lost.Snapshot.State == ElevationState.CommitAcked,
            "readonly observation may resolve a lost response with the same receipt"
        );
        quiescent = false;
    }

    static async Task ActivationAsync(Action<bool, string> check)
    {
        foreach (
            string failure in new[]
            {
                "none",
                "original-alive",
                "lock-held",
                "expired",
                "replay",
                "launch-unknown",
            }
        )
        {
            var calls = new List<string>();
            int result = await ManagerElevation.ActivateCommitted(
                limit =>
                {
                    calls.Add("exit:" + limit.TotalSeconds);
                    return Task.FromResult(failure != "original-alive");
                },
                limit =>
                {
                    calls.Add("locks:" + limit.TotalSeconds);
                    return Task.FromResult(failure != "lock-held");
                },
                () => failure != "expired",
                () =>
                {
                    calls.Add("consume");
                    return failure != "replay";
                },
                () =>
                {
                    calls.Add("launch");
                    return failure != "launch-unknown";
                }
            );
            check(
                (result == 0) == (failure == "none"),
                "activation result is honest for " + failure
            );
            check(
                calls.Count(c => c == "launch") <= 1
                    && (
                        !calls.Contains("launch")
                        || calls.IndexOf("consume") < calls.IndexOf("launch")
                    ),
                "durable consumption precedes one host attempt for " + failure
            );
            if (failure is "original-alive" or "lock-held" or "expired" or "replay")
                check(
                    !calls.Contains("launch"),
                    "no profile host before old handles locks expiry and single-use gates for "
                        + failure
                );
        }
        // This same post-commit path is used even if CommitAck's write observes EOF.
        check(
            await ManagerElevation.ActivateCommitted(
                _ => Task.FromResult(true),
                _ => Task.FromResult(true),
                () => true,
                () => true,
                () => true
            ) == 0,
            "authenticated Commit survives expected Ack/EOF loss after original handles exit"
        );
    }

    static async Task PipeAsync(Action<bool, string> check)
    {
        using var user = WindowsIdentity.GetCurrent();
        string name = "Bezoro.AhkManager.FakeCheck." + Guid.NewGuid().ToString("N");
        using var server = ElevationNative.CreateServer(name, user.User!.Value);
        using var client = new NamedPipeClientStream(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await connection;
        check(
            ElevationNative.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint child)
                && child == Environment.ProcessId
                && ElevationNative.GetNamedPipeServerProcessId(
                    client.SafePipeHandle,
                    out uint parent
                )
                && parent == Environment.ProcessId,
            "explicit user DACL local pipe authenticates real native peers"
        );
        var frame = new ElevationFrame("Ready", Guid.NewGuid(), new string('a', 64));
        await ManagerElevation.Send(client, frame, deadline.Token);
        check(
            await ManagerElevation.Receive(server, deadline.Token) == frame,
            "bounded source-generated rendezvous roundtrip"
        );
        foreach (int size in new[] { 0, 4097 })
        {
            using var stream = new MemoryStream(BitConverter.GetBytes(size));
            bool refused = false;
            try
            {
                await ManagerElevation.Receive(stream, CancellationToken.None);
            }
            catch (ProtocolException)
            {
                refused = true;
            }
            check(refused, "zero or oversized rendezvous frames fail closed");
        }
        using var eof = new MemoryStream();
        bool precommit = false;
        try
        {
            await ManagerElevation.Receive(eof, CancellationToken.None);
        }
        catch (EndOfStreamException)
        {
            precommit = true;
        }
        check(precommit, "pre-commit EOF cannot produce an authenticated Commit");
    }
}
