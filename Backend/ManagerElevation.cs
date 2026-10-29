using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

// State is retained for the lifetime of the normal backend. Mutating ticket calls never replay
// a launch, even after a transport timeout. No profile or domain owner is opened by bootstrap.
internal sealed partial class ManagerElevation : IDisposable
{
    readonly object sync = new();
    readonly ParentProof parent;
    readonly Func<bool> quiescent;
    readonly string backend,
        host,
        archive,
        profile;
    readonly Func<ManagerElevation, Task>? beginBoundary;
    readonly Func<ManagerElevation, CancellationToken, Task>? commitBoundary;
    readonly Action? prepareBoundary;
    readonly Func<Process?>? dispatchBoundary;
    readonly Func<StartupContinuationTicket, Task<bool>>? commitStartup;
    StartupContinuationTicket? startupContinuation;
    bool resumeLogin,
        backgroundStartup;
    Guid operationId,
        receiptId;
    long revision;
    ElevationState state;
    ReasonCode? reason;
    bool canResume = true,
        cancelled,
        committed,
        disposed,
        dispatchStarted;
    readonly Dictionary<Guid, ElevationStatus> history = [];
    string nonce = "",
        receiptHash = "",
        receiptIdentity = "";
    NamedPipeServerStream? pipe;
    NativeProcessIdentity? candidate;
    ProcessStamp candidateStamp;
    Process? candidateProcess;
    ElevationFiles? files;
    TaskCompletionSource changed = NewSignal();
    TaskCompletionSource ready = NewSignal();
    TaskCompletionSource ack = NewSignal();
    Task? launchTask;

    static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ManagerElevation(
        ParentProof parent,
        Func<bool> quiescent,
        Func<StartupContinuationTicket, Task<bool>>? commitStartup = null
    )
    {
        this.parent = parent;
        this.quiescent = quiescent;
        this.commitStartup = commitStartup;
        backend = ScriptPaths.Canonical(Environment.ProcessPath!, false);
        host = ScriptPaths.Canonical(parent.ParentExecutable, false);
        archive = Path.Combine(Path.GetDirectoryName(host)!, "resources", "app.asar");
        profile = ProfileRoot;
    }

    // The injected boundaries exercise the real ticket lifecycle without dispatching UAC.
    internal ManagerElevation(
        ParentProof parent,
        Func<bool> quiescent,
        Func<ManagerElevation, Task> beginBoundary,
        Func<ManagerElevation, CancellationToken, Task> commitBoundary
    )
    {
        this.parent = parent;
        this.quiescent = quiescent;
        this.beginBoundary = beginBoundary;
        this.commitBoundary = commitBoundary;
        backend = host = archive = profile = "";
    }

    internal ManagerElevation(
        ParentProof parent,
        Func<bool> quiescent,
        Action prepareBoundary,
        Func<Process?> dispatchBoundary
    )
    {
        this.parent = parent;
        this.quiescent = quiescent;
        this.prepareBoundary = prepareBoundary;
        this.dispatchBoundary = dispatchBoundary;
        backend = host = archive = profile = "";
    }

    internal Task Completion => launchTask ?? Task.CompletedTask;

    internal ElevationStatus Snapshot
    {
        get
        {
            lock (sync)
                return Current();
        }
    }

    internal bool ObserveReady()
    {
        lock (sync)
        {
            if (cancelled || disposed || committed || state != ElevationState.PendingApproval)
                return false;
            Set(ElevationState.Ready);
            ready.TrySetResult();
            return true;
        }
    }

    internal bool ObserveAcknowledgement(Guid receipt)
    {
        lock (sync)
        {
            if (!committed || receipt == Guid.Empty || receipt != receiptId || cancelled)
                return false;
            Set(ElevationState.CommitAcked);
            ack.TrySetResult();
            return true;
        }
    }

    internal void ObserveCandidateExit()
    {
        lock (sync)
        {
            if (state != ElevationState.CommitAcked)
                Set(
                    cancelled ? ElevationState.Cancelled : ElevationState.Failed,
                    cancelled ? ReasonCode.Cancelled : ReasonCode.DispatchFailed,
                    true
                );
        }
    }

    internal void ObserveDenied()
    {
        lock (sync)
            Set(
                cancelled ? ElevationState.Cancelled : ElevationState.Denied,
                ReasonCode.Cancelled,
                true
            );
    }

    internal static string ProfileRoot => ElevationReceiptStore.ProfileRoot;
    internal static string ChromiumRoot => ElevationReceiptStore.ChromiumRoot;

    internal static bool ProfileRootsMatch(string operational, string chromium) =>
        ElevationReceiptStore.ProfileRootsMatch(operational, chromium);

    public bool Frozen
    {
        get
        {
            lock (sync)
                return operationId != Guid.Empty && !canResume;
        }
    }

    public bool ShutdownAllowed
    {
        get
        {
            lock (sync)
                return !Frozen || state == ElevationState.CommitAcked;
        }
    }

    ElevationStatus Current() =>
        new(
            operationId,
            revision,
            state,
            reason,
            receiptId == Guid.Empty ? null : receiptId,
            canResume
        );

    void Set(ElevationState value, ReasonCode? code = null, bool resume = false)
    {
        lock (sync)
        {
            state = value;
            reason = code;
            canResume = resume;
            revision++;
            var previous = changed;
            changed = NewSignal();
            previous.TrySetResult();
        }
    }

    public ElevationStatus Begin(Guid id, bool resumeLogin = false)
    {
        if (id == Guid.Empty)
            throw new ProtocolException("InvalidRequest");
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous))
                return previous;
            if (operationId != Guid.Empty)
            {
                if (id == operationId)
                    return Current();
                if (
                    !canResume
                    || committed
                    || state
                        is not (
                            ElevationState.Denied
                            or ElevationState.Cancelled
                            or ElevationState.Failed
                            or ElevationState.Expired
                        )
                    || candidate is not null && !candidate.Exited
                )
                    throw new ProtocolException("PendingOperation");
                // A terminal worker can still be unwinding. Report a definite, effect-free
                // refusal for this new ID instead of creating an ambiguous transport failure.
                if (launchTask?.IsCompleted == false)
                {
                    var busy = new ElevationStatus(
                        id,
                        revision,
                        ElevationState.Failed,
                        ReasonCode.PendingOperation,
                        null,
                        true
                    );
                    history.Add(id, busy);
                    return busy;
                }

                history.Add(operationId, Current());
                pipe?.Dispose();
                candidate?.Dispose();
                candidateProcess?.Dispose();
                files?.Dispose();
                pipe = null;
                candidate = null;
                candidateProcess = null;
                files = null;
                cancelled = committed = dispatchStarted = false;
                receiptId = Guid.Empty;
                receiptHash = receiptIdentity = "";
                ready = NewSignal();
                ack = NewSignal();
            }

            if (startingStartupId != id)
            {
                startupContinuation = null;
                backgroundStartup = false;
            }
            this.resumeLogin = startupContinuation is null && resumeLogin;
            operationId = id;
            if (parent.AuthorityMode != AuthorityMode.Normal || !quiescent())
            {
                Set(ElevationState.Failed, ReasonCode.PendingOperation, true);
                return Current();
            }

            nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Set(ElevationState.PendingApproval);
            // Task.Run is essential: ShellExecute's UAC wait must not block Begin's response.
            launchTask = Task.Run(
                beginBoundary is null ? RunNormalAsync : () => beginBoundary(this)
            );
            return Current();
        }
    }

    Guid startingStartupId;

    internal ElevationStatus BeginStartup(
        Guid id,
        StartupContinuationTicket ticket,
        bool backgroundStartup = false
    )
    {
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous))
                return previous;
            if (operationId == id)
                return Current();
            if (operationId != Guid.Empty && !canResume)
                throw new ProtocolException("PendingOperation");
            startupContinuation = ticket;
            this.backgroundStartup = backgroundStartup;
            startingStartupId = id;
            try
            {
                return Begin(id);
            }
            finally
            {
                startingStartupId = Guid.Empty;
            }
        }
    }

    public async Task<ElevationStatus> Status(Guid id, long afterRevision, CancellationToken ct)
    {
        Task wait;
        lock (sync)
        {
            if (afterRevision < 0)
                throw new ProtocolException("InvalidRequest");
            if (history.TryGetValue(id, out var previous))
                return previous;
            Require(id);
            if (revision != afterRevision)
                return Current();
            wait = changed.Task;
        }

        try
        {
            await wait.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { }

        lock (sync)
            return history.TryGetValue(id, out var previous) ? previous : Current();
    }

    public async Task<ElevationStatus> Commit(Guid id, CancellationToken ct)
    {
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous))
                return previous;
            Require(id);
            if (committed || state != ElevationState.Ready)
                return Current();
            if (
                cancelled
                || !quiescent()
                || beginBoundary is null
                    && (
                        candidate is null
                        || !candidate.TryRead(out var stamp)
                        || stamp != candidateStamp
                    )
            )
            {
                Set(ElevationState.Unknown, ReasonCode.IdentityChanged);
                return Current();
            }

            committed = true;
            receiptId = Guid.NewGuid();
            Set(ElevationState.CommitPending);
        }

        try
        {
            if (commitBoundary is not null)
            {
                await commitBoundary(this, ct).ConfigureAwait(false);
                lock (sync)
                    return Current();
            }

            using var self = new NativeProcessIdentity(Environment.ProcessId);
            using var normalHost = new NativeProcessIdentity(parent.ParentPid);
            if (
                !self.TryRead(out var own)
                || !normalHost.TryRead(out var hostStamp)
                || own.Created != parent.ChildCreated
                || hostStamp.Created != parent.ParentCreated
                || files is null
                || !files.Valid()
            )
                throw new ProtocolException("IdentityChanged");
            if (
                startupContinuation is { } startup
                && (commitStartup is null || !await commitStartup(startup).ConfigureAwait(false))
            )
                throw new ProtocolException("IdentityChanged");
            var receipt = new ElevationReceipt(
                1,
                operationId,
                receiptId,
                nonce,
                own,
                hostStamp,
                candidateStamp,
                files.BackendHash,
                files.HostHash,
                files.ArchiveHash,
                profile,
                ChromiumRoot,
                DateTimeOffset.UtcNow.AddSeconds(120),
                startupContinuation,
                resumeLogin
            );
            string directory = CoordinationPath(nonce);
            ElevationNative.CreatePrivateDirectory(directory, own.Sid);
            string path = Path.Combine(directory, "commit.json");
            byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(
                receipt,
                Protocol.Json.ElevationReceipt
            );
            using (
                var output = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read
                )
            )
            {
                output.Write(receiptBytes);
                output.Flush(true);
            }

            using var input = PinReceipt(path, receiptBytes, out receiptHash, out receiptIdentity);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await Send(
                    pipe!,
                    new("Commit", id, nonce, receiptHash, receiptIdentity, backgroundStartup),
                    deadline.Token
                )
                .ConfigureAwait(false);
            await ack.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Known(ex))
        {
            lock (sync)
                if (state != ElevationState.CommitAcked)
                    Set(ElevationState.Unknown, ReasonCode.LaunchUncertain);
        }

        lock (sync)
            return Current();
    }

    public ElevationStatus Cancel(Guid id)
    {
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous))
                return previous;
            Require(id);
            if (committed)
                return Current();
            cancelled = true;
            pipe?.Dispose();
            Set(
                ElevationState.Cancelled,
                ReasonCode.Cancelled,
                canResume || !dispatchStarted && beginBoundary is null
            );
            return Current();
        }
    }

    void Require(Guid id)
    {
        if (id == Guid.Empty || id != operationId)
            throw new ProtocolException("InvalidRequest");
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            if (!committed)
                cancelled = true;
            pipe?.Dispose();
        }

        // After CommitAck EOF is expected. It must never kill or cancel the bootstrap.
        if (launchTask?.IsCompleted == true)
        {
            candidate?.Dispose();
            candidateProcess?.Dispose();
            files?.Dispose();
        }
    }

    internal static bool Known(Exception ex) => ElevationNative.IsExpectedFailure(ex);

    internal static ProcessIdentity ToIdentity(ProcessStamp stamp) =>
        ElevationNative.ToIdentity(stamp);

    internal static string CoordinationPath(string nonce) =>
        ElevationReceiptStore.CoordinationPath(nonce);

    internal static FileStream PinReceipt(
        string path,
        byte[] expected,
        out string hash,
        out string identity
    ) => ElevationReceiptStore.PinReceipt(path, expected, out hash, out identity);

    internal static string FileIdentity(FileStream file) =>
        ElevationReceiptStore.FileIdentity(file);

    internal static Task Send(Stream pipe, ElevationFrame frame, CancellationToken ct) =>
        ElevationWire.Send(pipe, frame, ct);

    internal static Task<ElevationFrame> Receive(Stream pipe, CancellationToken ct) =>
        ElevationWire.Receive(pipe, ct);

    internal static Task<int> Bootstrap(string[] args) => ElevationBootstrap.RunAsync(args);

    internal static ProcessStartInfo CommittedHostStartInfo(
        string hostPath,
        string? startupAdoption = null,
        bool resumeLogin = false,
        bool backgroundStartup = false
    ) =>
        ElevationActivation.CommittedHostStartInfo(
            hostPath,
            startupAdoption,
            resumeLogin,
            backgroundStartup
        );

    internal static Task<int> ActivateCommitted(
        Func<TimeSpan, Task<bool>> waitExit,
        Func<TimeSpan, Task<bool>> waitLocks,
        Func<bool> valid,
        Func<bool> consume,
        Func<bool> launch
    ) => ElevationActivation.ActivateCommitted(waitExit, waitLocks, valid, consume, launch);

    internal static int ActualParent(int pid) => ElevationNative.ActualParent(pid);

    internal static bool ProfileLocksAvailable(
        string profile,
        string sid,
        string? chromium = null
    ) => ElevationActivation.ProfileLocksAvailable(profile, sid, chromium);
}
