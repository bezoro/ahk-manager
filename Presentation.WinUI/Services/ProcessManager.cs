using System.Collections.Immutable;
using System.Diagnostics;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed record LaunchResult(OperationResult Result, ProcessSnapshot? Process);
public interface IProcessManager : IAsyncDisposable
{
    event EventHandler? Invalidated;
    bool Supports(ScriptOperation operation);
    bool IsAdministrator => false;
    ReasonCode? GetNormalLaunchUnavailableReason() => null;
    Task<ObservedProcesses> ObserveAsync(IReadOnlyCollection<string> knownInterpreters, CancellationToken ct = default);
    Task<LaunchResult> StartAsync(string interpreter, string script, bool manuallyElevated, CancellationToken ct = default);
    Task<OperationResult> ControlAsync(ProcessIdentity expected, ScriptOperation operation, CancellationToken ct = default);
}

public sealed class ProcessManager(IEffectOwner owner, bool documentedControlsProven = false,
    IManualElevationDispatcher? elevationDispatcher = null, IProcessObservationSource? observationSource = null) : IProcessManager
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<int, HeldProcess> held = [];
    readonly Dictionary<int, Process> monitors = [];
    readonly Dictionary<int, HeldProcess> launchProofs = [];
    readonly IManualElevationDispatcher elevation = elevationDispatcher ?? new ManualElevation();
    public event EventHandler? Invalidated;
    public bool IsAdministrator
    {
        get { using var self = new NativeProcessIdentity(Environment.ProcessId); return self.TryReadSecurity(out var security) && security.Elevated && security.IntegrityRid == 0x3000; }
    }
    public ReasonCode? GetNormalLaunchUnavailableReason()
    {
        using var self = new NativeProcessIdentity(Environment.ProcessId);
        if (!self.TryReadSecurity(out var security)) return ReasonCode.AccessDenied;
        return security.Elevated || security.IntegrityRid >= 0x3000 ? UnelevatedProcessLauncher.UnavailableReason() : null;
    }
    public bool Supports(ScriptOperation operation) => operation is ScriptOperation.Start or ScriptOperation.Edit or ScriptOperation.OpenFolder or ScriptOperation.Recover || documentedControlsProven && operation is ScriptOperation.Stop or ScriptOperation.Restart or ScriptOperation.ToggleSuspend or ScriptOperation.TogglePause;

    public async Task<ObservedProcesses> ObserveAsync(IReadOnlyCollection<string> interpreters, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => Observe(interpreters, ct), ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    ObservedProcesses Observe(IReadOnlyCollection<string> interpreters, CancellationToken ct)
    {
        PruneExitedLaunchProofs(launchProofs);
        var fresh = ProcessObservation.Read(interpreters, out bool complete, out var issues, ct, observationSource, launchProofs.Values);
        var replacement = new Dictionary<int, HeldProcess>();
        foreach (var item in fresh)
        {
            var snapshot = item.Snapshot;
            var identity = snapshot.Identity;
            if (held.TryGetValue(identity.Pid, out var previous) && CoreSame(previous.Snapshot.Identity, identity) && previous.Handle.Matches(identity))
            {
                // A file edit does not rewrite the immutable identity of an already-held process.
                snapshot = snapshot with { Identity = previous.Snapshot.Identity, Origin = previous.Snapshot.Origin };
            }

            // Pause's menu checkmark is stale outside actual menu entry: it is never treated as state.
            snapshot = snapshot with { Paused = null, Suspended = documentedControlsProven ? AhkWindowControl.ReadSuspend(identity) : null };
            replacement[identity.Pid] = item with { Snapshot = snapshot };
        }
        foreach (var item in held.Values) item.Handle.Dispose();
        held.Clear(); foreach (var pair in replacement) held[pair.Key] = pair.Value;
        foreach (var pid in monitors.Keys.Where(pid => monitors[pid].HasExited).ToArray()) { monitors[pid].Dispose(); monitors.Remove(pid); }
        return new(held.Values.Select(h => h.Snapshot).ToImmutableArray(), complete, issues);
    }
    static bool CoreSame(ProcessIdentity left, ProcessIdentity right) => left.Pid == right.Pid && left.CreationFileTime == right.CreationFileTime && ScriptPaths.Equal(left.ScriptPath, right.ScriptPath) && ScriptPaths.Equal(left.ExecutablePath, right.ExecutablePath) && left.UserSid == right.UserSid && left.SessionId == right.SessionId;
    internal static void PruneExitedLaunchProofs(Dictionary<int, HeldProcess> proofs, Func<NativeProcessIdentity, bool>? exited = null)
    {
        foreach (int pid in proofs.Where(pair => exited?.Invoke(pair.Value.Handle) ?? pair.Value.Handle.Exited).Select(pair => pair.Key).ToArray())
        {
            proofs[pid].Handle.Dispose();
            proofs.Remove(pid);
        }
    }

    public async Task<LaunchResult> StartAsync(string interpreter, string script, bool manuallyElevated, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        bool dispatched = false;
        try
        {
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                var version = AutoHotkeyDiscovery.Validate(interpreter);
                string canonicalScript = ScriptPaths.Canonical(script);
                using var scriptGuard = new FileStream(canonicalScript, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var interpreterGuard = new FileStream(version.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await AutoHotkeyDiscovery.ValidateScriptVersionAsync(canonicalScript, version, ct).ConfigureAwait(false);
                if (!owner.Held || !owner.Trusted || !owner.Allows(canonicalScript, version.Path)) return new LaunchResult(new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable), null);
                var recent = Observe([version.Path], ct);
                if (!recent.Complete) return new LaunchResult(new(Outcome.Failed, ReasonCode.ObservationIncomplete), null);
                if (recent.Processes.Any(p => ScriptPaths.Equal(p.Identity.ScriptPath, canonicalScript))) return new LaunchResult(new(Outcome.Failed, ReasonCode.AlreadyRunning), null);
                ct.ThrowIfCancellationRequested();
                if (!owner.Held || !owner.Allows(canonicalScript, version.Path)) return new LaunchResult(new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable), null);
                Process? process;
                dispatched = true;
                if (manuallyElevated)
                {
                    var elevated = elevation.Dispatch(version.Path, canonicalScript);
                    process = elevated.Process;
                    if (process is null) return new LaunchResult(elevated.Result, null);
                }
                else
                {
                    var info = new ProcessStartInfo(version.Path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(canonicalScript)! };
                    info.ArgumentList.Add(canonicalScript);
                    using var self = new NativeProcessIdentity(Environment.ProcessId);
                    if (!self.TryReadSecurity(out var security)) return new LaunchResult(new(Outcome.Failed, ReasonCode.AccessDenied), null);
                    if (security.Elevated || security.IntegrityRid >= 0x3000)
                    {
                        if (GetNormalLaunchUnavailableReason() is ReasonCode denied) return new LaunchResult(new(Outcome.Failed, denied), null);
                        process = UnelevatedProcessLauncher.Launch(version.Path, canonicalScript);
                    }
                    else process = Process.Start(info);
                    if (process is null) return new LaunchResult(new(Outcome.Unknown, ReasonCode.LaunchUncertain), null);
                }
                int pid = process.Id;
                try
                {
                    using var launched = new NativeProcessIdentity(pid);
                    if (!ProcessNative.GetProcessTimes(process.SafeHandle, out long receiptCreated, out _, out _, out _) || !launched.TryRead(out var stamp) || stamp.Created != receiptCreated || stamp.Sid != owner.Sid || stamp.Session != Process.GetCurrentProcess().SessionId || !ScriptPaths.Equal(stamp.Executable, version.Path)) return new LaunchResult(new(Outcome.Unknown, ReasonCode.LaunchUncertain), null);
                    string scriptHash = ScriptPaths.Fingerprint(canonicalScript);
                    var identity = new ProcessIdentity(pid, stamp.Created, version.Path, canonicalScript, stamp.Sid, stamp.Session, scriptHash, version.Hash);
                    var receiptHandle = new NativeProcessIdentity(pid);
                    if (!receiptHandle.Matches(identity)) { receiptHandle.Dispose(); return new LaunchResult(new(Outcome.Unknown, ReasonCode.LaunchUncertain), null); }
                    if (launchProofs.Remove(pid, out var oldProof)) oldProof.Handle.Dispose();
                    launchProofs.Add(pid, new(receiptHandle, new(identity, ExecutionOrigin.Manager, version.Version, DateTimeOffset.FromFileTime(stamp.Created))));
                    process.EnableRaisingEvents = true;
                    process.Exited += OnExit;
                    monitors[pid] = process; process = null;
                    var deadline = Stopwatch.StartNew();
                    do
                    {
                        if (ct.IsCancellationRequested) return new LaunchResult(new(Outcome.Unknown, ReasonCode.LaunchUncertain), null);
                        var observed = Observe([version.Path], CancellationToken.None);
                        var proof = observed.Processes.FirstOrDefault(p => p.Identity.Pid == pid && p.Identity.CreationFileTime == stamp.Created && ScriptPaths.Equal(p.Identity.ScriptPath, canonicalScript));
                        if (proof is not null && launched.Matches(proof.Identity)) return new LaunchResult(new(Outcome.Confirmed), proof);
                        if (launched.Exited) return new LaunchResult(new(Outcome.Failed, ReasonCode.DispatchFailed), null);
                        await Task.Delay(150).ConfigureAwait(false);
                    } while (deadline.Elapsed < TimeSpan.FromSeconds(5));
                    return new LaunchResult(new(Outcome.Unknown, ReasonCode.LaunchUncertain), null);
                }
                finally { process?.Dispose(); }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(new(dispatched ? Outcome.Unknown : Outcome.CancelledBeforeDispatch, dispatched ? ReasonCode.LaunchUncertain : ReasonCode.Cancelled), null); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(new(dispatched ? Outcome.Unknown : Outcome.Failed, dispatched ? ReasonCode.LaunchUncertain : ReasonCode.DispatchFailed, ex.Message), null); }
        finally { gate.Release(); }
    }
    void OnExit(object? sender, EventArgs e) => Invalidated?.Invoke(this, EventArgs.Empty);

    public async Task<OperationResult> ControlAsync(ProcessIdentity expected, ScriptOperation operation, CancellationToken ct = default)
    {
        if (!Supports(operation)) return new(Outcome.Failed, ReasonCode.CapabilityUnverified);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                if (!owner.Held || !owner.Trusted || !owner.Allows(expected.ScriptPath, expected.ExecutablePath)) return new OperationResult(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
                var current = Observe([expected.ExecutablePath], ct);
                if (!held.TryGetValue(expected.Pid, out var target) || !NativeProcessIdentity.Same(expected, target.Snapshot.Identity) || !target.Handle.Matches(expected)) return new(Outcome.Failed, ReasonCode.IdentityChanged);
                if (current.Processes.Count(p => ScriptPaths.Equal(p.Identity.ScriptPath, expected.ScriptPath)) != 1) return new(Outcome.Failed, ReasonCode.MultipleInstances);
                ct.ThrowIfCancellationRequested();
                if (target.Handle.ControlUnavailableReason() is ReasonCode denied) return new(Outcome.Failed, denied);
                var requested = AhkWindowControl.Request(target.Handle, expected, operation);
                if (requested.Outcome == Outcome.Failed) return requested;
                if (requested.Code == ReasonCode.DispatchFailed && operation != ScriptOperation.Stop) return requested;
                if (operation == ScriptOperation.Stop)
                    return await target.Handle.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) ? new(Outcome.Confirmed) : new(Outcome.Unknown, ReasonCode.ExitNotObserved);
                if (operation == ScriptOperation.ToggleSuspend)
                {
                    bool? after = AhkWindowControl.ReadSuspend(expected);
                    if (target.Handle.Matches(expected) && after.HasValue && target.Snapshot.Suspended.HasValue && after != target.Snapshot.Suspended) return new(Outcome.Confirmed);
                }
                return new(Outcome.Unknown, ReasonCode.StateNotObservable);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(Outcome.Unknown, ReasonCode.IdentityChanged, ex.Message); }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        foreach (var process in monitors.Values) { process.Exited -= OnExit; process.Dispose(); }
        foreach (var item in held.Values) item.Handle.Dispose();
        foreach (var item in launchProofs.Values) item.Handle.Dispose();
        monitors.Clear(); held.Clear(); launchProofs.Clear();
        return ValueTask.CompletedTask;
    }
}
