using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Backend;

public sealed record ElevationFrame(string Kind, Guid OperationId, string Nonce, string? ReceiptHash = null, string? FileIdentity = null);
public sealed record ElevationReceipt(int Version, Guid OperationId, Guid ReceiptId, string Nonce, ProcessStamp NormalBackend, ProcessStamp NormalHost, ProcessStamp Bootstrap, string BackendHash, string HostHash, string ArchiveHash, string ProfileRoot, string ChromiumRoot, DateTimeOffset ExpiresAt, StartupContinuationTicket? StartupContinuation = null);

// State is retained for the lifetime of the normal backend. Mutating ticket calls never replay
// a launch, even after a transport timeout. No profile or domain owner is opened by bootstrap.
internal sealed class ManagerElevation : IDisposable
{
    readonly object sync = new();
    readonly ParentProof parent;
    readonly Func<bool> quiescent;
    readonly string backend, host, archive, profile;
    readonly Func<ManagerElevation, Task>? beginBoundary;
    readonly Func<ManagerElevation, CancellationToken, Task>? commitBoundary;
    readonly Action? prepareBoundary;
    readonly Func<Process?>? dispatchBoundary;
    readonly Func<StartupContinuationTicket, Task<bool>>? commitStartup;
    StartupContinuationTicket? startupContinuation;
    Guid operationId, receiptId;
    long revision;
    ElevationState state;
    ReasonCode? reason;
    bool canResume = true, cancelled, committed, disposed, dispatchStarted;
    readonly Dictionary<Guid, ElevationStatus> history = [];
    string nonce = "", receiptHash = "", receiptIdentity = "";
    NamedPipeServerStream? pipe;
    NativeProcessIdentity? candidate;
    ProcessStamp candidateStamp;
    Process? candidateProcess;
    ElevationFiles? files;
    TaskCompletionSource changed = NewSignal();
    TaskCompletionSource ready = NewSignal();
    TaskCompletionSource ack = NewSignal();
    Task? launchTask;
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ManagerElevation(ParentProof parent, Func<bool> quiescent, Func<StartupContinuationTicket, Task<bool>>? commitStartup = null)
    {
        this.parent = parent; this.quiescent = quiescent; this.commitStartup = commitStartup;
        backend = ScriptPaths.Canonical(Environment.ProcessPath!, false);
        host = ScriptPaths.Canonical(parent.ParentExecutable, false);
        archive = Path.Combine(Path.GetDirectoryName(host)!, "resources", "app.asar");
        profile = ProfileRoot;
    }
    // The injected boundaries exercise the real ticket lifecycle without dispatching UAC.
    internal ManagerElevation(ParentProof parent, Func<bool> quiescent, Func<ManagerElevation, Task> beginBoundary, Func<ManagerElevation, CancellationToken, Task> commitBoundary)
    { this.parent = parent; this.quiescent = quiescent; this.beginBoundary = beginBoundary; this.commitBoundary = commitBoundary; backend = host = archive = profile = ""; }
    internal ManagerElevation(ParentProof parent, Func<bool> quiescent, Action prepareBoundary, Func<Process?> dispatchBoundary)
    { this.parent = parent; this.quiescent = quiescent; this.prepareBoundary = prepareBoundary; this.dispatchBoundary = dispatchBoundary; backend = host = archive = profile = ""; }
    internal Task Completion => launchTask ?? Task.CompletedTask;
    internal ElevationStatus Snapshot { get { lock (sync) return Current(); } }
    internal bool ObserveReady()
    {
        lock (sync) { if (cancelled || disposed || committed || state != ElevationState.PendingApproval) return false; Set(ElevationState.Ready); ready.TrySetResult(); return true; }
    }
    internal bool ObserveAcknowledgement(Guid receipt)
    {
        lock (sync) { if (!committed || receipt == Guid.Empty || receipt != receiptId || cancelled) return false; Set(ElevationState.CommitAcked); ack.TrySetResult(); return true; }
    }
    internal void ObserveCandidateExit() { lock (sync) { if (state != ElevationState.CommitAcked) Set(cancelled ? ElevationState.Cancelled : ElevationState.Failed, cancelled ? ReasonCode.Cancelled : ReasonCode.DispatchFailed, true); } }
    internal void ObserveDenied() { lock (sync) Set(cancelled ? ElevationState.Cancelled : ElevationState.Denied, ReasonCode.Cancelled, true); }
    internal static string ProfileRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManagerElectron");
    internal static string ChromiumRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bezoro", "AhkManagerElectron", "Chromium");
    internal static bool ProfileRootsMatch(string operational, string chromium) => ScriptPaths.Equal(operational, ProfileRoot) && ScriptPaths.Equal(chromium, ChromiumRoot);
    public bool Frozen { get { lock (sync) return operationId != Guid.Empty && !canResume; } }
    public bool ShutdownAllowed { get { lock (sync) return !Frozen || state == ElevationState.CommitAcked; } }
    ElevationStatus Current() => new(operationId, revision, state, reason, receiptId == Guid.Empty ? null : receiptId, canResume);
    void Set(ElevationState value, ReasonCode? code = null, bool resume = false)
    {
        lock (sync) { state = value; reason = code; canResume = resume; revision++; var previous = changed; changed = NewSignal(); previous.TrySetResult(); }
    }
    public ElevationStatus Begin(Guid id)
    {
        if (id == Guid.Empty) throw new ProtocolException("InvalidRequest");
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous)) return previous;
            if (operationId != Guid.Empty)
            {
                if (id == operationId) return Current();
                if (!canResume || committed || state is not (ElevationState.Denied or ElevationState.Cancelled or ElevationState.Failed or ElevationState.Expired) || candidate is not null && !candidate.Exited) throw new ProtocolException("PendingOperation");
                // A terminal worker can still be unwinding. Report a definite, effect-free
                // refusal for this new ID instead of creating an ambiguous transport failure.
                if (launchTask?.IsCompleted == false)
                {
                    var busy = new ElevationStatus(id, revision, ElevationState.Failed, ReasonCode.PendingOperation, null, true);
                    history.Add(id, busy); return busy;
                }
                history.Add(operationId, Current());
                pipe?.Dispose(); candidate?.Dispose(); candidateProcess?.Dispose(); files?.Dispose();
                pipe = null; candidate = null; candidateProcess = null; files = null;
                cancelled = committed = dispatchStarted = false; receiptId = Guid.Empty; receiptHash = receiptIdentity = "";
                ready = NewSignal(); ack = NewSignal();
            }
            if (startingStartupId != id) startupContinuation = null;
            operationId = id;
            if (parent.AuthorityMode != AuthorityMode.Normal || !quiescent()) { Set(ElevationState.Failed, ReasonCode.PendingOperation, true); return Current(); }
            nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Set(ElevationState.PendingApproval);
            // Task.Run is essential: ShellExecute's UAC wait must not block Begin's response.
            launchTask = Task.Run(beginBoundary is null ? RunNormalAsync : () => beginBoundary(this));
            return Current();
        }
    }
    Guid startingStartupId;
    internal ElevationStatus BeginStartup(Guid id, StartupContinuationTicket ticket)
    {
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous)) return previous;
            if (operationId == id) return Current();
            if (operationId != Guid.Empty && !canResume) throw new ProtocolException("PendingOperation");
            startupContinuation = ticket;
            startingStartupId = id;
            try { return Begin(id); } finally { startingStartupId = Guid.Empty; }
        }
    }
    public async Task<ElevationStatus> Status(Guid id, long afterRevision, CancellationToken ct)
    {
        Task wait;
        lock (sync) { if (afterRevision < 0) throw new ProtocolException("InvalidRequest"); if (history.TryGetValue(id, out var previous)) return previous; Require(id); if (revision != afterRevision) return Current(); wait = changed.Task; }
        try { await wait.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); } catch (TimeoutException) { }
        lock (sync) return history.TryGetValue(id, out var previous) ? previous : Current();
    }
    public async Task<ElevationStatus> Commit(Guid id, CancellationToken ct)
    {
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous)) return previous;
            Require(id);
            if (committed || state != ElevationState.Ready) return Current();
            if (cancelled || !quiescent() || beginBoundary is null && (candidate is null || !candidate.TryRead(out var stamp) || stamp != candidateStamp)) { Set(ElevationState.Unknown, ReasonCode.IdentityChanged); return Current(); }
            committed = true; receiptId = Guid.NewGuid(); Set(ElevationState.CommitPending);
        }
        try
        {
            if (commitBoundary is not null) { await commitBoundary(this, ct).ConfigureAwait(false); lock (sync) return Current(); }
            using var self = new NativeProcessIdentity(Environment.ProcessId);
            using var normalHost = new NativeProcessIdentity(parent.ParentPid);
            if (!self.TryRead(out var own) || !normalHost.TryRead(out var hostStamp) || own.Created != parent.ChildCreated || hostStamp.Created != parent.ParentCreated || files is null || !files.Valid()) throw new ProtocolException("IdentityChanged");
            if (startupContinuation is { } startup && (commitStartup is null || !await commitStartup(startup).ConfigureAwait(false))) throw new ProtocolException("IdentityChanged");
            var receipt = new ElevationReceipt(1, operationId, receiptId, nonce, own, hostStamp, candidateStamp, files.BackendHash, files.HostHash, files.ArchiveHash, profile, ChromiumRoot, DateTimeOffset.UtcNow.AddSeconds(120), startupContinuation);
            string directory = CoordinationPath(nonce);
            ElevationNative.CreatePrivateDirectory(directory, own.Sid);
            string path = Path.Combine(directory, "commit.json");
            byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Protocol.Json.ElevationReceipt);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { output.Write(receiptBytes); output.Flush(true); }
            using var input = PinReceipt(path, receiptBytes, out receiptHash, out receiptIdentity);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await Send(pipe!, new("Commit", id, nonce, receiptHash, receiptIdentity), deadline.Token).ConfigureAwait(false);
            await ack.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Known(ex)) { lock (sync) if (state != ElevationState.CommitAcked) Set(ElevationState.Unknown, ReasonCode.LaunchUncertain); }
        lock (sync) return Current();
    }
    public ElevationStatus Cancel(Guid id)
    {
        lock (sync)
        {
            if (history.TryGetValue(id, out var previous)) return previous;
            Require(id);
            if (committed) return Current();
            cancelled = true; pipe?.Dispose(); Set(ElevationState.Cancelled, ReasonCode.Cancelled, canResume || !dispatchStarted && beginBoundary is null);
            return Current();
        }
    }
    void Require(Guid id) { if (id == Guid.Empty || id != operationId) throw new ProtocolException("InvalidRequest"); }
    async Task RunNormalAsync()
    {
        try
        {
            lock (sync) if (cancelled || disposed) throw new OperationCanceledException();
            if (prepareBoundary is not null) prepareBoundary();
            else { files = new(backend, host, archive); pipe = ElevationNative.CreateServer("Bezoro.AhkManager.Administration." + nonce, parent.UserSid); }
            var connect = pipe?.WaitForConnectionAsync();
            var info = new ProcessStartInfo(backend) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(backend)! };
            info.Arguments = "--administrator-bootstrap " + operationId.ToString("D") + " " + nonce + " " + Environment.ProcessId + " " + parent.ChildCreated + " " + parent.ParentPid + " " + parent.ParentCreated;
            lock (sync)
            {
                if (cancelled || disposed) throw new OperationCanceledException();
                // Cancellation before this point proves no dispatch. Afterwards, a caller
                // must retain the ticket until refusal or candidate exit is established.
                dispatchStarted = true;
            }
            candidateProcess = dispatchBoundary is null ? Process.Start(info) : dispatchBoundary();
            if (candidateProcess is null) throw new ProtocolException("LaunchUncertain");
            var sessionFiles = files ?? throw new ProtocolException("LaunchUncertain");
            var sessionPipe = pipe ?? throw new ProtocolException("LaunchUncertain");
            var connection = connect ?? throw new ProtocolException("LaunchUncertain");
            // Access Process.Handle now: it is the ShellExecute creation receipt, not a JSON PID.
            if (!ProcessNative.GetProcessTimes(candidateProcess.SafeHandle, out long createdAt, out _, out _, out _)) throw new ProtocolException("IdentityChanged");
            candidate = new(candidateProcess.Id);
            if (!candidate.TryRead(out candidateStamp) || candidateStamp.Created != createdAt || candidateStamp.Sid != parent.UserSid || candidateStamp.Session != parent.SessionId || !ScriptPaths.Equal(candidateStamp.Executable, backend) || !candidate.TryReadSecurity(out var security) || ParentGuard.TokenMode(security) != AuthorityMode.Administrator || !sessionFiles.Valid()) throw new ProtocolException("ParentMismatch");
            lock (sync) if (cancelled || disposed) { sessionPipe.Dispose(); throw new OperationCanceledException(); }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await connection.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!ElevationNative.GetNamedPipeClientProcessId(sessionPipe.SafePipeHandle, out uint peer) || peer != candidateStamp.Pid || !candidate.Matches(ToIdentity(candidateStamp))) throw new ProtocolException("ParentMismatch");
            var message = await Receive(sessionPipe, deadline.Token).ConfigureAwait(false); Check(message, "Ready");
            if (!ObserveReady()) throw new OperationCanceledException();
            // One reader owns this pipe. A commit frame is written only by Commit, never by status.
            message = await Receive(sessionPipe, deadline.Token).ConfigureAwait(false);
            Check(message, "CommitAck");
            if (!committed || message.ReceiptHash != receiptHash || message.FileIdentity != receiptIdentity) throw new ProtocolException("InvalidRequest");
            if (!ObserveAcknowledgement(receiptId)) throw new ProtocolException("InvalidRequest");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { ObserveDenied(); }
        catch (Exception ex) when (Known(ex))
        {
            lock (sync)
            {
                if (!dispatchStarted)
                {
                    Set(cancelled || ex is OperationCanceledException ? ElevationState.Cancelled : ElevationState.Failed, cancelled || ex is OperationCanceledException ? ReasonCode.Cancelled : ReasonCode.DispatchFailed, true);
                    pipe?.Dispose(); files?.Dispose(); files = null; return;
                }
            }
            bool exited = candidate is not null && candidate.Exited;
            Set(cancelled ? ElevationState.Cancelled : ex is OperationCanceledException ? ElevationState.Expired : ElevationState.Unknown, cancelled ? ReasonCode.Cancelled : ReasonCode.LaunchUncertain, exited);
            pipe?.Dispose();
            if (candidate is not null && !exited)
            {
                bool gone = await candidate.WaitForExitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
                if (gone) Set(cancelled ? ElevationState.Cancelled : ElevationState.Failed, cancelled ? ReasonCode.Cancelled : ReasonCode.DispatchFailed, true);
            }
        }
    }
    void Check(ElevationFrame frame, string kind) { if (frame.Kind != kind || frame.OperationId != operationId || frame.Nonce != nonce) throw new ProtocolException("NotAuthenticated"); }
    public void Dispose()
    {
        lock (sync) { disposed = true; if (!committed) cancelled = true; pipe?.Dispose(); }
        // After CommitAck EOF is expected. It must never kill or cancel the bootstrap.
        if (launchTask?.IsCompleted == true) { candidate?.Dispose(); candidateProcess?.Dispose(); files?.Dispose(); }
    }
    internal static bool Known(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or OperationCanceledException or JsonException or TimeoutException or ArgumentException;
    internal static ProcessIdentity ToIdentity(ProcessStamp stamp) => new(stamp.Pid, stamp.Created, stamp.Executable, "", stamp.Sid, stamp.Session);
    internal static string CoordinationPath(string nonce) => Path.Combine(Path.GetTempPath(), "Bezoro.AhkManager.Administration." + nonce);
    internal static FileStream PinReceipt(string path, byte[] expected, out string hash, out string identity)
    {
        hash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(); identity = "";
        var input = new FileStream(ScriptPaths.Canonical(path, false), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length > StartupContinuationStore.MaximumBytes || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != hash) throw new ProtocolException("IdentityChanged");
            identity = FileIdentity(input); return input;
        }
        catch { input.Dispose(); throw; }
    }
    internal static string FileIdentity(FileStream file)
    {
        if (!ProcessNative.GetFileInformationByHandle(file.SafeFileHandle, out var stamp) || stamp.Links != 1) throw new ProtocolException("IdentityChanged");
        return stamp.Volume.ToString("x8") + stamp.IndexHigh.ToString("x8") + stamp.IndexLow.ToString("x8") + ":" + file.Length;
    }
    internal static async Task Send(Stream pipe, ElevationFrame frame, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(frame, Protocol.Json.ElevationFrame);
        if (body.Length > 4096) throw new ProtocolException("FrameTooLarge");
        await pipe.WriteAsync(BitConverter.GetBytes(body.Length), ct).ConfigureAwait(false); await pipe.WriteAsync(body, ct).ConfigureAwait(false); await pipe.FlushAsync(ct).ConfigureAwait(false);
    }
    internal static async Task<ElevationFrame> Receive(Stream pipe, CancellationToken ct)
    {
        byte[] length = new byte[4]; await pipe.ReadExactlyAsync(length, ct).ConfigureAwait(false); int count = BitConverter.ToInt32(length);
        if (count is < 2 or > 4096) throw new ProtocolException("FrameTooLarge");
        byte[] body = new byte[count]; await pipe.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(body); Protocol.RejectDuplicates(parsed.RootElement);
        return JsonSerializer.Deserialize(body, Protocol.Json.ElevationFrame) ?? throw new ProtocolException("InvalidRequest");
    }

    // This entry has no manager composition and never reads settings, journal or Chromium.
    internal static async Task<int> Bootstrap(string[] args)
    {
        if (args.Length != 7 || args[0] != "--administrator-bootstrap" || !Guid.TryParseExact(args[1], "D", out var operation) || operation == Guid.Empty || args[2].Length != 64 || args[2].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || !int.TryParse(args[3], out int backendPid) || !long.TryParse(args[4], out long backendCreated) || !int.TryParse(args[5], out int hostPid) || !long.TryParse(args[6], out long hostCreated)) throw new ProtocolException("NotAuthenticated");
        string nonce = args[2], executable = ScriptPaths.Canonical(Environment.ProcessPath!, false);
        string hostPath = ScriptPaths.Canonical(Path.Combine(Path.GetDirectoryName(executable)!, "..", "Bezoro.AhkManager.exe"), false);
        using var self = new NativeProcessIdentity(Environment.ProcessId); using var normal = new NativeProcessIdentity(backendPid); using var host = new NativeProcessIdentity(hostPid);
        if (!self.TryRead(out var own) || !normal.TryRead(out var normalStamp) || !host.TryRead(out var hostStamp) || own.Sid == "S-1-5-18" || own.Sid != normalStamp.Sid || own.Sid != hostStamp.Sid || own.Session != normalStamp.Session || own.Session != hostStamp.Session || normalStamp.Created != backendCreated || hostStamp.Created != hostCreated || !ScriptPaths.Equal(normalStamp.Executable, executable) || !ScriptPaths.Equal(hostStamp.Executable, hostPath) || !self.TryReadSecurity(out var security) || ParentGuard.TokenMode(security) != AuthorityMode.Administrator || !normal.TryReadSecurity(out var lower) || ParentGuard.TokenMode(lower) != AuthorityMode.Normal || !host.TryReadSecurity(out lower) || ParentGuard.TokenMode(lower) != AuthorityMode.Normal || ActualParent(backendPid) != hostPid) throw new ProtocolException("ParentMismatch");
        using var files = new ElevationFiles(executable, hostPath, Path.Combine(Path.GetDirectoryName(hostPath)!, "resources", "app.asar"));
        using var pipe = new NamedPipeClientStream(".", "Bezoro.AhkManager.Administration." + nonce, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var beforeCommit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(beforeCommit.Token).ConfigureAwait(false);
        if (!ElevationNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer != backendPid || !normal.Matches(ToIdentity(normalStamp)) || !host.Matches(ToIdentity(hostStamp)) || !files.Valid()) throw new ProtocolException("ParentMismatch");
        await Send(pipe, new("Ready", operation, nonce), beforeCommit.Token).ConfigureAwait(false);
        // Any EOF before this authenticated Commit aborts. A receipt file alone has no authority.
        ElevationFrame commit = await Receive(pipe, beforeCommit.Token).ConfigureAwait(false);
        if (commit.Kind != "Commit" || commit.OperationId != operation || commit.Nonce != nonce || commit.ReceiptHash is not { Length: 64 } || commit.FileIdentity is null || !normal.Matches(ToIdentity(normalStamp)) || !host.Matches(ToIdentity(hostStamp))) throw new ProtocolException("NotAuthenticated");
        string directory = ScriptPaths.LocalPath(CoordinationPath(nonce)), path = ScriptPaths.Canonical(Path.Combine(directory, "commit.json"), false);
        using var receiptLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (receiptLease.Length > StartupContinuationStore.MaximumBytes || FileIdentity(receiptLease) != commit.FileIdentity || Convert.ToHexString(SHA256.HashData(receiptLease)).ToLowerInvariant() != commit.ReceiptHash) throw new ProtocolException("IdentityChanged");
        receiptLease.Position = 0;
        var receipt = JsonSerializer.Deserialize(receiptLease, Protocol.Json.ElevationReceipt) ?? throw new ProtocolException("InvalidRequest");
        if (receipt.Version != 1 || receipt.OperationId != operation || receipt.Nonce != nonce || receipt.ReceiptId == Guid.Empty || receipt.NormalBackend != normalStamp || receipt.NormalHost != hostStamp || receipt.Bootstrap != own || receipt.BackendHash != files.BackendHash || receipt.HostHash != files.HostHash || receipt.ArchiveHash != files.ArchiveHash || !ProfileRootsMatch(receipt.ProfileRoot, receipt.ChromiumRoot) || receipt.ExpiresAt <= DateTimeOffset.UtcNow || receipt.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(120) || !files.Valid()) throw new ProtocolException("IdentityChanged");
        if (receipt.StartupContinuation is { } continuation && (continuation.OperationId == Guid.Empty || continuation.UserSid != own.Sid || continuation.SessionId != own.Session || !ScriptPaths.Equal(continuation.ProfileRoot, receipt.ProfileRoot) || continuation.ExpiresAt <= DateTimeOffset.UtcNow)) throw new ProtocolException("IdentityChanged");
        using var adoption = receipt.StartupContinuation is { } continuationTicket ? new StartupAdoptionServer(continuationTicket, nonce, own, executable, hostPath, files.BackendHash) : null;
        // From here EOF/normal-parent exit is expected, including a lost Ack response.
        // Authenticated commit is pinned. Never revert to the pre-commit EOF rule.
        try { using var ackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await Send(pipe, new("CommitAck", operation, nonce, commit.ReceiptHash, commit.FileIdentity), ackDeadline.Token).ConfigureAwait(false); }
        catch (Exception ex) when (Known(ex)) { }
        async Task<bool> WaitExit(TimeSpan limit)
        {
            var elapsed = Stopwatch.StartNew();
            while (!normal.Exited || !host.Exited) { if (elapsed.Elapsed >= limit || receipt.ExpiresAt <= DateTimeOffset.UtcNow) return false; await Task.Delay(100).ConfigureAwait(false); }
            return true;
        }
        async Task<bool> WaitLocks(TimeSpan limit)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ProfileLocksAvailable(receipt.ProfileRoot, own.Sid, receipt.ChromiumRoot)) { if (elapsed.Elapsed >= limit || receipt.ExpiresAt <= DateTimeOffset.UtcNow) return false; await Task.Delay(100).ConfigureAwait(false); }
            return true;
        }
        bool Consume()
        {
            using var consume = new FileStream(Path.Combine(directory, "consumed"), FileMode.CreateNew, FileAccess.Write, FileShare.Read); consume.Write(receipt.ReceiptId.ToByteArray()); consume.Flush(true); return true;
        }
        bool Launch()
        {
            using var launched = Process.Start(CommittedHostStartInfo(hostPath, adoption?.Descriptor.Encode()));
            if (launched is null || !ProcessNative.GetProcessTimes(launched.SafeHandle, out long launchedAt, out _, out _, out _)) return false;
            using var delivered = new NativeProcessIdentity(launched.Id);
            bool valid = delivered.TryRead(out var stamp) && stamp.Created == launchedAt && ScriptPaths.Equal(stamp.Executable, hostPath) && stamp.Sid == own.Sid && stamp.Session == own.Session && delivered.TryReadSecurity(out var actual) && ParentGuard.TokenMode(actual) == AuthorityMode.Administrator;
            if (valid) adoption?.HostLaunched(stamp);
            return valid;
        }
        int result = await ActivateCommitted(WaitExit, WaitLocks, () => files.Valid() && receipt.ExpiresAt > DateTimeOffset.UtcNow, Consume, Launch).ConfigureAwait(false);
        if (result == 0 && adoption is not null)
        {
            try { var continuationResult = await adoption.DeliverAsync().ConfigureAwait(false); result = continuationResult.Outcome == Outcome.Unknown ? 75 : 0; }
            catch (Exception ex) when (Known(ex)) { result = 75; }
        }
        if (result != 0 && normal.Exited && host.Exited) ElevationNative.RecoveryError();
        return result;
    }
    internal static ProcessStartInfo CommittedHostStartInfo(string hostPath, string? startupAdoption = null)
    {
        var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(hostPath)! };
        // This requests only window activation. Authority still comes from native token proof.
        start.ArgumentList.Add("--foreground");
        if (startupAdoption is not null) start.ArgumentList.Add("--startup-adoption=" + startupAdoption);
        return start;
    }
    // Called only after an authenticated Commit has been validated and pinned. It does not
    // inspect a pipe or parent liveness: EOF is expected after commit, even if Ack was lost.
    internal static async Task<int> ActivateCommitted(Func<TimeSpan, Task<bool>> waitExit, Func<TimeSpan, Task<bool>> waitLocks, Func<bool> valid, Func<bool> consume, Func<bool> launch)
    {
        try
        {
            if (!valid() || !await waitExit(TimeSpan.FromSeconds(60)).ConfigureAwait(false) || !valid() || !await waitLocks(TimeSpan.FromSeconds(10)).ConfigureAwait(false) || !valid()) return 75;
            if (!consume()) return 75;
            return launch() ? 0 : 75;
        }
        catch (Exception ex) when (Known(ex)) { return 75; }
    }
    internal static int ActualParent(int pid)
    {
        using var query = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid); query.Options.Timeout = TimeSpan.FromSeconds(5);
        using var rows = query.Get(); foreach (ManagementObject row in rows) using (row) return checked((int)(uint)row["ParentProcessId"]);
        return 0;
    }
    internal static bool ProfileLocksAvailable(string profile, string sid, string? chromium = null)
    {
        try
        {
            chromium ??= ChromiumRoot;
            if (!Directory.Exists(profile) || !Directory.Exists(chromium)) return false;
            using var mutex = new Mutex(false, "Global\\Bezoro.AhkManager.Effects." + sid); bool held;
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            try
            {
                foreach (string file in new[] { "SingletonLock", "lockfile", "Local State", "Default/Preferences" })
                { string path = Path.Combine(chromium, file); if (File.Exists(path)) { using var lease = new FileStream(ScriptPaths.Canonical(path, false), FileMode.Open, FileAccess.Read, FileShare.None); } }
                return true;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (Known(ex)) { return false; }
    }
}

internal sealed class ElevationFiles : IDisposable
{
    readonly string backend, host, archive;
    readonly FileStream backendLease, hostLease, archiveLease;
    public string BackendHash { get; }
    public string HostHash { get; }
    public string ArchiveHash { get; }
    public ElevationFiles(string backend, string host, string archive)
    {
        this.backend = ScriptPaths.Canonical(backend, false); this.host = ScriptPaths.Canonical(host, false); this.archive = ScriptPaths.Canonical(archive, false);
        backendLease = new(this.backend, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { hostLease = new(this.host, FileMode.Open, FileAccess.Read, FileShare.Read); try { archiveLease = new(this.archive, FileMode.Open, FileAccess.Read, FileShare.Read); } catch { hostLease.Dispose(); throw; } } catch { backendLease.Dispose(); throw; }
        BackendHash = Hash(backendLease); HostHash = Hash(hostLease); ArchiveHash = Hash(archiveLease);
    }
    static string Hash(FileStream file) { file.Position = 0; return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    public bool Valid() => ScriptPaths.Equal(ScriptPaths.Canonical(backend, false), backend) && ScriptPaths.Equal(ScriptPaths.Canonical(host, false), host) && ScriptPaths.Equal(ScriptPaths.Canonical(archive, false), archive) && Hash(backendLease) == BackendHash && Hash(hostLease) == HostHash && Hash(archiveLease) == ArchiveHash;
    public void Dispose() { archiveLease.Dispose(); hostLease.Dispose(); backendLease.Dispose(); }
}

internal static class ElevationNative
{
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    static nint Descriptor(string sid)
    {
        // Explicit protected user DACL; no cross-IL CurrentUserOnly comparison and no broad groups.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;" + sid + ")", 1, out nint descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return descriptor;
    }
    internal static NamedPipeServerStream CreateServer(string name, string sid)
    {
        nint descriptor = Descriptor(sid);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000 | 0x00080000, 8, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { ProcessNative.LocalFree(descriptor); }
    }
    internal static void CreatePrivateDirectory(string path, string sid)
    {
        for (string? current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Coordination ancestor unavailable or redirected.");
        nint descriptor = Descriptor(sid);
        try { var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor }; if (!CreateDirectory(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        finally { ProcessNative.LocalFree(descriptor); }
    }
    internal static void RecoveryError() => MessageBox(0, "Não foi possível concluir a abertura como administrador. Abra o AHK Manager normalmente para recuperar. Nenhum script foi encerrado.", "AHK Manager", 0x10);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")] static extern int MessageBox(nint window, string text, string caption, uint kind);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
