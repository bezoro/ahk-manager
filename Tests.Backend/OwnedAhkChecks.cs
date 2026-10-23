using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;
using Bezoro.AhkManager.WinUI.Tests.Integration;
using Microsoft.Win32.SafeHandles;

namespace Tests.Backend;

// Opt-in console fixture only. No UI, startup registration, elevation or forceful cleanup.
internal static class OwnedAhkChecks
{
    const string Interpreter = "C:/Program Files/AutoHotkey/v2/AutoHotkey64.exe";
    const string InterpreterHash = "2eab99848ebc07f8ab0cc448bf69af8c700b2fc7fcd1ff8794df743dcc58da51";
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task RunAsync(string root)
    {
        root = ScriptPaths.LocalPath(root);
        NormalToken(); ValidateInterpreter(); Directories(Path.GetDirectoryName(root)!);
        if (!CreateDirectory(root, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Fresh exclusive fixture directory required.");
        using var lease = new FileStream(Path.Combine(root, "fixture-lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var scenario = new Scenario(root);
        Exception? failure = null;
        try { await scenario.RunAsync(); }
        catch (Exception ex) { failure = ex; scenario.Record("sequence-failed", ex.GetType().Name + ": " + ex.Message); }
        finally { await scenario.CleanupAsync(); }
        if (failure is not null || scenario.CleanupPending) throw new InvalidOperationException("Owned AHK validation failed; preserve fixture receipts and inspect cleanup-pending before another action.", failure);
        scenario.Record("PASS", "owned fixture complete; all owned exits confirmed; startup/elevation writes zero");
    }

    static void NormalToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, 20, out int elevated, sizeof(int), out int bytes) || bytes != sizeof(int) || elevated != 0 || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("A normal non-elevated token is required.");
    }
    static void ValidateInterpreter()
    {
        var verified = AutoHotkeyDiscovery.Validate(Interpreter);
        if (!Version.TryParse(verified.Version, out var version) || version.Major != 2 || verified.Hash != InterpreterHash || !ScriptPaths.Equal(verified.Path, Path.GetFullPath(Interpreter)))
            throw new InvalidOperationException("Approved AutoHotkey v2 interpreter identity changed.");
    }
    static void Directories(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (!Directory.Exists(current) || (File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Fixture directory or ancestor is missing or redirected.");
    }
    static string Hash(string path) => ScriptPaths.Fingerprint(ScriptPaths.Canonical(path, false));
    static void NewFile(string path, string text)
    {
        Directories(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(Utf8.GetBytes(text)); stream.Flush(true);
    }
    static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    // A record must be newly appended, complete, singular, and exactly tied to this attempt.
    static bool Receipt(byte[] checkpoint, byte[] current, string nonce, int pid, int attempt, int decision)
    {
        if (current.Length > 65536 || current.Length <= checkpoint.Length || !current.AsSpan(0, checkpoint.Length).SequenceEqual(checkpoint)) return false;
        try
        {
            string added = Utf8.GetString(current.AsSpan(checkpoint.Length));
            string[] lines = added.Split('\n');
            if (lines.Length != 2 || lines[1] != "") return false;
            string[] fields = lines[0].TrimEnd('\r').Split('|');
            return fields.Length == 5 && fields[0] == nonce && fields[1] == pid.ToString(System.Globalization.CultureInfo.InvariantCulture) && fields[2] == attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) && fields[3] == "Close" && fields[4] == decision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (DecoderFallbackException) { return false; }
    }
    static bool VetoProof(OperationResult result, bool sameAlive, byte[] checkpoint, byte[] current, string nonce, int pid) =>
        result.Outcome == Outcome.Unknown && result.Code == ReasonCode.ExitNotObserved && sameAlive && Receipt(checkpoint, current, nonce, pid, 1, 1);

    sealed class Scenario(string root)
    {
        readonly string script = Path.Combine(root, "owned harmless.ahk"), veto = Path.Combine(root, "veto harmless.ahk");
        readonly string nonce = Guid.NewGuid().ToString("N");
        readonly Dictionary<(int, long), ProcessIdentity> known = [];
        readonly Dictionary<string, string> expectedFiles = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<(int, long)> uncertainStops = [];
        readonly NoElevation elevation = new(); readonly NoStartup startup = new();
        readonly Stopwatch sequence = Stopwatch.StartNew();
        readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(180));
        FixtureProcessObservation? observation;
        bool uncertainLaunch;
        public bool CleanupPending { get; private set; }
        string ReceiptPath => Path.Combine(root, "onexit-receipt.txt");
        string Marker => Path.Combine(root, "allow-exit");
        static (int, long) Key(ProcessIdentity id) => (id.Pid, id.CreationFileTime);
        public void Record(string stage, string detail)
        {
            Directories(root);
            string line = DateTimeOffset.UtcNow.ToString("O") + "\t" + Escape(stage) + "\t" + Escape(detail) + Environment.NewLine;
            File.AppendAllText(Path.Combine(root, "owned-ahk-receipts.txt"), line, Utf8); Console.Write(line);
        }
        void Check(bool value, string name) { Record(value ? "PASS" : "FAIL", name); if (!value) throw new InvalidOperationException(name); }
        void ScriptFile(string path, string text)
        {
            string expected = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));
            NewFile(path, text); expectedFiles.Add(Path.GetFullPath(path), expected);
            Check(Hash(path) == expected, "emitted-fixture-bytes-match-immutable-expected-hash");
        }
        void Identity(string stage, ProcessIdentity id) => Record(stage, string.Join("\t", id.Pid, id.CreationFileTime, id.ExecutablePath, id.ScriptPath, id.UserSid, id.SessionId, id.ScriptHash, id.InterpreterHash));
        void Result(string stage, OperationResult result) => Record(stage, result.Outcome + "/" + result.Code + "/" + result.Detail);
        void Room(int seconds = 20)
        {
            budget.Token.ThrowIfCancellationRequested();
            if (TimeSpan.FromSeconds(180) - sequence.Elapsed < TimeSpan.FromSeconds(seconds)) throw new TimeoutException("No budget remains for another effect and its existing confirmation deadline.");
        }
        bool Exited(ProcessIdentity id)
        {
            using var held = new NativeProcessIdentity(id.Pid);
            if (held.Exited) return true;
            if (held.TryRead(out var stamp) && stamp.Created != id.CreationFileTime) return true;
            try { using var process = Process.GetProcessById(id.Pid); return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        async Task ProofAsync(ProcessManager manager, ProcessIdentity expected, CancellationToken ct)
        {
            ValidateInterpreter(); Directories(root);
            using var user = WindowsIdentity.GetCurrent(); using var self = Process.GetCurrentProcess();
            Check(expected.UserSid == user.User!.Value && expected.SessionId == self.SessionId && ScriptPaths.Equal(expected.ExecutablePath, Path.GetFullPath(Interpreter)) && expected.InterpreterHash == InterpreterHash && expected.ScriptHash == Hash(expected.ScriptPath), "approved-user-session-interpreter-and-file-hashes");
            var rows = await manager.ObserveAsync([Interpreter], ct);
            var matches = rows.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, expected.ScriptPath)).ToArray();
            using var held = new NativeProcessIdentity(expected.Pid);
            Check(rows.Complete && matches.Length == 1 && NativeProcessIdentity.Same(matches[0].Identity, expected) && held.Matches(expected) && matches[0].WindowVerified && AhkWindowControl.FindWindow(expected) != 0, "full-eight-field-live-identity-and-hidden-window");
            Check(matches[0].Paused is null, "pause-remains-unobservable-no-toggle-dispatched");
            Identity("identity-proof", expected);
        }
        async Task<ProcessIdentity> StartAsync(ProcessManager manager, string path)
        {
            Room(); var launch = await manager.StartAsync(Interpreter, path, false, budget.Token); Result("start", launch.Result);
            if (launch.Result.Outcome == Outcome.Unknown || launch.Process is null && launch.Result.Succeeded) uncertainLaunch = true;
            Check(launch.Result.Succeeded && launch.Process is not null, "real-start-confirmed");
            var id = launch.Process!.Identity; known[Key(id)] = id;
            await ProofAsync(manager, id, budget.Token); return id;
        }
        async Task<OperationResult> StopAsync(ProcessManager manager, ProcessIdentity id, CancellationToken ct, bool cleanup = false)
        {
            await ProofAsync(manager, id, ct);
            if (!cleanup) Room();
            uncertainStops.Add(Key(id)); Identity("before-stop", id);
            var result = await manager.ControlAsync(id, ScriptOperation.Stop, ct); Result(cleanup ? "cleanup-stop" : "stop", result);
            if (result.Succeeded && Exited(id)) uncertainStops.Remove(Key(id));
            return result;
        }
        void ReceiptRegressions()
        {
            byte[] Good(string text) => Utf8.GetBytes(text);
            string line = nonce + "|42|1|Close|1\n";
            var expected = new OperationResult(Outcome.Unknown, ReasonCode.ExitNotObserved);
            Check(VetoProof(expected, true, [], Good(line), nonce, 42), "receipt-valid-veto-proof");
            foreach (var row in new[]
            {
                ("absent", Array.Empty<byte>()), ("wrong-nonce", Good("old|42|1|Close|1\n")),
                ("wrong-pid", Good(nonce+"|43|1|Close|1\n")), ("wrong-attempt", Good(nonce+"|42|2|Close|1\n")),
                ("wrong-decision", Good(nonce+"|42|1|Close|0\n")), ("wrong-reason", Good(nonce+"|42|1|Exit|1\n")),
                ("partial", Good(line.TrimEnd('\n'))), ("duplicate", Good(line+line))
            }) Check(!VetoProof(expected, true, [], row.Item2, nonce, 42) && !File.Exists(Marker), "reject-receipt-"+row.Item1+"-without-release");
            Check(!VetoProof(expected, true, Good(line), Good(line), nonce, 42), "reject-old-checkpoint-receipt");
            Check(!VetoProof(new(Outcome.Failed, ReasonCode.WindowUnverified), true, [], Good(line), nonce, 42), "reject-refusal-before-callback");
            Check(!VetoProof(new(Outcome.Unknown, ReasonCode.DispatchFailed), true, [], Good(line), nonce, 42), "reject-dispatch-unknown-without-expected-result");
            Check(!VetoProof(expected, false, [], Good(line), nonce, 42), "reject-changed-or-not-live-identity");
        }
        byte[] ReadReceipt()
        {
            Directories(root);
            if (!File.Exists(ReceiptPath)) return [];
            _ = ScriptPaths.Canonical(ReceiptPath, false);
            using var input = new FileStream(ReceiptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (input.Length > 65536) throw new InvalidDataException("Fixture receipt exceeded bound.");
            byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
        }
        async Task<byte[]> WaitReceiptAsync(byte[] checkpoint, ProcessIdentity id, int attempt, int decision)
        {
            var deadline = Stopwatch.StartNew(); byte[] current;
            do
            {
                budget.Token.ThrowIfCancellationRequested(); current = ReadReceipt();
                if (Receipt(checkpoint, current, nonce, id.Pid, attempt, decision)) return current;
                await Task.Delay(25, budget.Token);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(1));
            return current;
        }
        public async Task RunAsync()
        {
            Record("fixture", "nonce="+nonce+";pid="+Environment.ProcessId+";argv="+string.Join(" ", Environment.GetCommandLineArgs())+";sequenceBudget=180s;cleanupBudget=30s");
            ScriptFile(script, "#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\nPersistent\nSetTimer(() => 0, 1000)\n");
            ScriptFile(veto, $$"""
                #Requires AutoHotkey v2.0
                #SingleInstance Off
                #NoTrayIcon
                Persistent
                attempt := 0
                OwnedExit(reason, code) {
                    global attempt
                    attempt += 1
                    decision := FileExist(A_ScriptDir "\allow-exit") ? 0 : 1
                    FileAppend("{{nonce}}|" ProcessExist() "|" attempt "|" reason "|" decision "`n", A_ScriptDir "\onexit-receipt.txt", "UTF-8-RAW")
                    return decision
                }
                OnExit(OwnedExit)
                SetTimer(() => 0, 1000)
                """);
            ReceiptRegressions(); observation = new FixtureProcessObservation(root, [script, veto]);
            using var owner = new FixtureOwner(root, expectedFiles);
            await using var manager = new ProcessManager(owner, true, elevation, observation);
            var active = await StartAsync(manager, script);
            Room(); var duplicate = await manager.StartAsync(Interpreter, script, false, budget.Token); Result("duplicate", duplicate.Result);
            Check(duplicate.Result.Outcome == Outcome.Failed && duplicate.Result.Code == ReasonCode.AlreadyRunning && duplicate.Process is null, "duplicate-refused-without-new-instance");
            foreach (var operation in new[] { "suspend", "resume" })
            {
                await ProofAsync(manager, active, budget.Token); Room();
                var before = await manager.ObserveAsync([Interpreter], budget.Token); bool? was = before.Processes.Single(p => NativeProcessIdentity.Same(p.Identity, active)).Suspended;
                var result = await manager.ControlAsync(active, ScriptOperation.ToggleSuspend, budget.Token); Result(operation, result);
                var after = await manager.ObserveAsync([Interpreter], budget.Token); var target = after.Processes.Single(p => NativeProcessIdentity.Same(p.Identity, active));
                Check(result.Succeeded && was.HasValue && target.Suspended.HasValue && was != target.Suspended && target.Paused is null, operation+"-confirmed-by-observation");
            }
            foreach (var negative in new[] { active with { CreationFileTime = active.CreationFileTime + 1 }, active with { ScriptHash = new string('0', 64) }, active with { ScriptPath = Path.Combine(root, "not-owned.ahk") } })
            {
                await ProofAsync(manager, active, budget.Token); Room();
                var result = await manager.ControlAsync(negative, ScriptOperation.Stop, budget.Token); Result("synthetic-identity-rejection", result);
                Check(result.Outcome == Outcome.Failed && result.Code is ReasonCode.IdentityChanged or ReasonCode.EffectOwnerUnavailable, "synthetic-owner-path-hash-or-creation-refused");
                await ProofAsync(manager, active, budget.Token);
            }
            owner.Permit = false;
            try { Room(); var denied = await manager.ControlAsync(active, ScriptOperation.Stop, budget.Token); Result("owner-denied", denied); Check(denied.Outcome == Outcome.Failed && denied.Code == ReasonCode.EffectOwnerUnavailable, "owner-refused-before-dispatch"); }
            finally { owner.Permit = true; }
            await ProofAsync(manager, active, budget.Token);
            var stopped = await StopAsync(manager, active, budget.Token); Check(stopped.Succeeded && Exited(active), "normal-graceful-stop-exit-confirmed");

            var vetoId = await StartAsync(manager, veto);
            Check(!File.Exists(ReceiptPath) && !File.Exists(Marker), "onexit-receipt-and-release-marker-initially-absent");
            byte[] checkpoint = ReadReceipt(); Record("onexit-checkpoint", "attempt=1;bytes="+checkpoint.Length); Identity("before-veto", vetoId);
            var vetoResult = await StopAsync(manager, vetoId, budget.Token);
            byte[] receipt = await WaitReceiptAsync(checkpoint, vetoId, 1, 1);
            await ProofAsync(manager, vetoId, budget.Token);
            Check(VetoProof(vetoResult, !Exited(vetoId), checkpoint, receipt, nonce, vetoId.Pid), "fresh-onexit-close-veto-receipt-and-same-live-identity");
            Record("veto-proven", "Only this expected veto permits the explicit fixture release and deliberate second Stop.");
            Room();
            NewFile(Marker, nonce); checkpoint = receipt; Record("onexit-checkpoint", "attempt=2;bytes="+checkpoint.Length);
            var allowed = await StopAsync(manager, vetoId, budget.Token); receipt = await WaitReceiptAsync(checkpoint, vetoId, 2, 0);
            Check(allowed.Succeeded && Receipt(checkpoint, receipt, nonce, vetoId.Pid, 2, 0) && Exited(vetoId), "explicit-release-second-receipt-and-confirmed-exit");
            await ServiceLifecycleAsync();
            Check(startup.Writes == 0 && elevation.Attempts == 0, "zero-startup-writes-and-uac-attempts");
        }
        async Task ServiceLifecycleAsync()
        {
            string data = Path.Combine(root, "service-data"); Directory.CreateDirectory(data);
            var store = new SettingsStore(Path.Combine(data, "settings.json")); var loaded = await store.LoadAsync(budget.Token);
            Check(!loaded.ReadOnly, "fixture-settings-loaded-empty");
            var seeded = await store.SaveAsync(loaded.Document with { Settings = new() { InterpreterPath = Interpreter, LaunchManagerAtLogin = false, ShowWindowAtLogin = false } }, budget.Token);
            Check(seeded.Succeeded, "fixture-settings-preseeded-with-approved-interpreter-startup-off");
            using var owner = new FixtureOwner(root, expectedFiles);
            var processes = new ProcessManager(owner, true, elevation, observation);
            var service = new ManagerService(data, owner, processes, new UserStartupService(startup, Environment.ProcessPath!), Path.Combine(root, "legacy-absent"));
            ProcessIdentity? replacement = null;
            try
            {
                await service.LoadAsync(budget.Token); var added = await service.AddFilesAsync([script], budget.Token);
                Check(added.Result.Succeeded && added.Added.Length == 1, "real-service-adds-only-owned-script"); Guid id = added.Added[0];
                SelectionContext Selection() => new(id, new ScriptQuery(), service.Snapshot.Revision);
                Room(45); var plan = await service.PrepareAsync(Selection(), ScriptOperation.Start, budget.Token); Check(plan.Allowed, "service-start-plan");
                var result = await service.PerformAsync(plan, Selection(), true, budget.Token); Result("service-start", result);
                if (!result.Succeeded) uncertainLaunch = true;
                Check(result.Succeeded && service.Snapshot.Scripts.Single().Process is not null, "service-start-confirmed");
                var old = service.Snapshot.Scripts.Single().Process!.Identity; known[Key(old)] = old; await ProofAsync(processes, old, budget.Token);
                Room(45); plan = await service.PrepareAsync(Selection(), ScriptOperation.Restart, budget.Token); Check(plan.Allowed && NativeProcessIdentity.Same(plan.ExpectedIdentity, old), "service-restart-plan-bound-to-original");
                uncertainStops.Add(Key(old)); result = await service.PerformAsync(plan, Selection(), true, budget.Token); Result("service-restart", result);
                if (!result.Succeeded) uncertainLaunch = true;
                Check(result.Succeeded && Exited(old), "service-restart-confirms-old-exit"); uncertainStops.Remove(Key(old));
                replacement = service.Snapshot.Scripts.Single().Process?.Identity;
                Check(replacement is not null && !NativeProcessIdentity.Same(old, replacement), "service-restart-new-complete-identity");
                known[Key(replacement!)] = replacement!; await ProofAsync(processes, replacement!, budget.Token);
                var observed = await processes.ObserveAsync([Interpreter], budget.Token);
                Check(observed.Complete && observed.Processes.Count(p => ScriptPaths.Equal(p.Identity.ScriptPath, script)) == 1, "service-restart-exactly-one-replacement");
            }
            finally { await service.DisposeAsync(); Record("service-disposed", "Disposal releases handles and owner, without Stop."); }
            Check(replacement is not null && !Exited(replacement), "core-dispose-leaves-same-replacement-alive");
            using var proofOwner = new FixtureOwner(root, expectedFiles); await using var proof = new ProcessManager(proofOwner, true, elevation, observation);
            await ProofAsync(proof, replacement!, budget.Token);
        }
        public async Task CleanupAsync()
        {
            using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var clock = Stopwatch.StartNew();
            try
            {
                if (observation is null) return;
                using var owner = new FixtureOwner(root, expectedFiles); await using var manager = new ProcessManager(owner, true, elevation, observation);
                foreach (var id in known.Values)
                {
                    if (Exited(id)) { Identity("cleanup-exit-already-confirmed", id); continue; }
                    if (uncertainStops.Contains(Key(id)) || TimeSpan.FromSeconds(30) - clock.Elapsed < TimeSpan.FromSeconds(20)) { Pending(id, "Prior Stop uncertainty/veto or insufficient independent cleanup budget; no retry."); continue; }
                    var result = await StopAsync(manager, id, cleanupBudget.Token, true);
                    if (!result.Succeeded || !Exited(id)) Pending(id, "Graceful cleanup did not confirm exit; no retry.");
                }
                var final = await manager.ObserveAsync([Interpreter], cleanupBudget.Token);
                if (!final.Complete) { CleanupPending = true; Record("cleanup-pending", "Final observation incomplete."); }
                foreach (var row in final.Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, script) || ScriptPaths.Equal(p.Identity.ScriptPath, veto))) Pending(row.Identity, "Owned fixture still observed; preserve and ask coordinator.");
                if (uncertainLaunch) { CleanupPending = true; Record("cleanup-pending", "Earlier launch/restart uncertainty; observation cannot authorize automatic retry or PASS."); }
                if (startup.Writes != 0 || elevation.Attempts != 0) { CleanupPending = true; Record("cleanup-pending", "Forbidden startup/elevation adapter was attempted."); }
                Record(CleanupPending ? "cleanup-pending" : "cleanup-confirmed", "All files and receipts retained; no forced termination or recursive deletion.");
            }
            catch (Exception ex) { CleanupPending = true; Record("cleanup-pending", ex.GetType().Name+": "+ex.Message); }
            finally { budget.Dispose(); }
        }
        void Pending(ProcessIdentity id, string why) { CleanupPending = true; Identity("cleanup-pending-identity", id); Record("cleanup-pending", why); }
    }
    sealed class FixtureOwner : IEffectOwner
    {
        readonly string root; readonly Dictionary<string, string> scripts; bool disposed;
        public bool Permit { get; set; } = true;
        public bool Held => !disposed;
        public bool Trusted => Held;
        public string Sid { get; } = WindowsIdentity.GetCurrent().User!.Value;
        public string LoginKey { get; } = "owned-fixture-"+Guid.NewGuid().ToString("N");
        public FixtureOwner(string root, IReadOnlyDictionary<string, string> scripts) { this.root = Path.GetFullPath(root)+Path.DirectorySeparatorChar; this.scripts = scripts.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase); }
        public bool Allows(string scriptPath, string interpreterPath)
        {
            try
            {
                NormalToken(); ValidateInterpreter(); Directories(root.TrimEnd(Path.DirectorySeparatorChar));
                string path = ScriptPaths.Canonical(scriptPath);
                return Held && Permit && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && scripts.TryGetValue(path, out string? hash) && Hash(path) == hash && ScriptPaths.Equal(ScriptPaths.Canonical(interpreterPath, false), Path.GetFullPath(Interpreter)) && Hash(Interpreter) == InterpreterHash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException) { return false; }
        }
        public void Dispose() => disposed = true;
    }
    sealed class NoElevation : IManualElevationDispatcher
    {
        public int Attempts;
        public ElevationDispatch Dispatch(string interpreter, string script) { Attempts++; throw new InvalidOperationException("Owned console fixture must never request UAC."); }
    }
    sealed class NoStartup : IUserStartupAdapter
    {
        public int Writes;
        public string? ReadEntry() => null;
        public bool CompareExchangeEntry(string? expected, string? replacement) { Writes++; throw new InvalidOperationException("Owned console fixture must never write startup."); }
        public Task<StartupInspection> InspectAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new StartupInspection(true, ImmutableArray<string>.Empty, ImmutableArray<Issue>.Empty)); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateDirectory(string path, nint securityAttributes);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int value, int size, out int returned);
}
