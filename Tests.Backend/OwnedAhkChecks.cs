using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;
using Microsoft.Win32.SafeHandles;
using Tests.Backend.Fixtures;

namespace Tests.Backend;

// Opt-in console fixture only. No UI, startup registration, elevation or forceful cleanup.
internal static class OwnedAhkChecks
{
    const string Interpreter = "C:/Program Files/AutoHotkey/v2/AutoHotkey64.exe";
    const string InterpreterHash =
        "2eab99848ebc07f8ab0cc448bf69af8c700b2fc7fcd1ff8794df743dcc58da51";
    const long ElectronBytes = 249797632;
    const string ElectronHash = "8e84549b05d27d444da2957653200d1e0ac287eff0fc5722b92835b9e0821a59";
    static readonly UTF8Encoding Utf8 = new(false, true);

    enum DiagnosticStage
    {
        Arguments,
        RootValidation,
        FixturePreparation,
        ScopeChecks,
        FixtureSourceConstruction,
        SourceReadStarted,
        SourceRead,
        SourceIncomplete,
        Candidate,
        CandidateIdentityUnavailable,
        CandidateImageChanged,
        UnapprovedArgv,
        UnprovedOpaque,
        ScopeApproved,
        ProjectionIncompleteReasonUnavailable,
        ObservationComplete,
        ReportWriteFailed,
    }

    enum DiagnosticStatus
    {
        InvalidRequest,
        Failed,
        ObservedComplete,
        ObservedIncomplete,
        UnexpectedOwnedProcesses,
        TimedOut,
        Cancelled,
    }

    sealed class ReadOnlyDiagnostic
    {
        public Guid RunNonce = Guid.NewGuid();
        public DateTimeOffset StartedUtc = DateTimeOffset.UtcNow,
            EndedUtc;
        public DiagnosticStatus Status = DiagnosticStatus.InvalidRequest;
        public DiagnosticStage Stage = DiagnosticStage.Arguments;
        public bool? Complete;
        public ReasonCode[] Issues = [];
        public int ScopeChecksPassed,
            DiagnosticChecksPassed,
            Candidates,
            Approvals;
        public int? SourceRows,
            ObservedOwned;
        public bool TimedOut,
            Cancelled,
            Persisted;
        public string? ExceptionClass;
        public int? ProviderCode;

        public void Record(DiagnosticStage stage, int count)
        {
            Stage = stage;
            if (stage == DiagnosticStage.SourceRead)
                SourceRows = count;
            if (stage == DiagnosticStage.Candidate)
                Candidates++;
            if (stage == DiagnosticStage.ScopeApproved)
                Approvals++;
        }
    }

    static ReasonCode[] DiagnosticIssueCodes(ImmutableArray<Issue> issues) =>
        issues.Select(issue => issue.Code).Distinct().Take(64).ToArray();

    static byte[] DiagnosticJson(ReadOnlyDiagnostic value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("runNonce", value.RunNonce);
            writer.WriteString("startedUtc", value.StartedUtc);
            writer.WriteString("endedUtc", value.EndedUtc);
            writer.WriteString("status", value.Status.ToString());
            writer.WriteString("scopeStage", value.Stage.ToString());
            if (value.Complete is bool complete)
                writer.WriteBoolean("complete", complete);
            else
                writer.WriteNull("complete");
            writer.WriteStartArray("issueCodes");
            foreach (var issue in value.Issues)
                writer.WriteStringValue(issue.ToString());
            writer.WriteEndArray();
            writer.WriteNumber("scopeChecksPassed", value.ScopeChecksPassed);
            writer.WriteNumber("diagnosticChecksPassed", value.DiagnosticChecksPassed);
            if (value.SourceRows is int rows)
                writer.WriteNumber("sourceRows", rows);
            else
                writer.WriteNull("sourceRows");
            writer.WriteNumber("candidatesExamined", value.Candidates);
            writer.WriteNumber("scopeApprovals", value.Approvals);
            if (value.ObservedOwned is int owned)
                writer.WriteNumber("observedOwnedProcesses", owned);
            else
                writer.WriteNull("observedOwnedProcesses");
            writer.WriteBoolean("timedOut", value.TimedOut);
            writer.WriteBoolean("cancelled", value.Cancelled);
            writer.WriteBoolean("persisted", value.Persisted);
            if (value.ExceptionClass is string type)
                writer.WriteString("exceptionClass", type);
            else
                writer.WriteNull("exceptionClass");
            if (value.ProviderCode is int code)
                writer.WriteNumber("providerCode", code);
            else
                writer.WriteNull("providerCode");
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static async Task<int> RunReadOnlyDiagnosticAsync(string[] args)
    {
        // This selector owns all error redaction, including failures before a root exists.
        try
        {
            return await ReadOnlyDiagnosticCoreAsync(args).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                Console.WriteLine(
                    "{\"schemaVersion\":1,\"status\":\"Failed\",\"complete\":null,\"scopeStage\":\"ReportingFailed\"}"
                );
            }
            catch { }
            return 1;
        }
    }

    static async Task<int> ReadOnlyDiagnosticCoreAsync(string[] args)
    {
        var result = new ReadOnlyDiagnostic();
        string? ownedRoot = null;
        FileStream? lease = null;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (args.Length != 3 || args[0] != "--inventory-scoped-readonly" || args[1] != "--root")
                throw new ArgumentException();
            result.Stage = DiagnosticStage.RootValidation;
            string root = ScriptPaths.LocalPath(args[2]);
            NormalToken();
            ValidateInterpreter();
            Directories(Path.GetDirectoryName(root)!);
            budget.Token.ThrowIfCancellationRequested();
            if (!CreateDirectory(root, 0))
                throw new IOException();
            ownedRoot = root;
            lease = new FileStream(
                Path.Combine(root, "fixture-lease"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None
            );
            result.Stage = DiagnosticStage.FixturePreparation;
            string[] approved =
            [
                Path.Combine(root, "owned harmless.ahk"),
                Path.Combine(root, "veto harmless.ahk"),
            ];
            foreach (string path in approved)
                NewFile(
                    path,
                    "#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\nPersistent\n"
                );
            var files = approved.ToDictionary(path => path, Hash, StringComparer.OrdinalIgnoreCase);
            result.Stage = DiagnosticStage.ScopeChecks;
            ScopeChecks(
                root,
                (passed, _) =>
                {
                    if (!passed)
                        throw new InvalidOperationException();
                    result.ScopeChecksPassed++;
                }
            );
            ReadOnlyDiagnosticContractChecks(
                root,
                (passed, _) =>
                {
                    if (!passed)
                        throw new InvalidOperationException();
                    result.DiagnosticChecksPassed++;
                }
            );
            budget.Token.ThrowIfCancellationRequested();
            result.Stage = DiagnosticStage.FixtureSourceConstruction;
            var source = new ScopedObservation(
                new FixtureProcessObservation(root, approved, Interpreter),
                approved,
                diagnostic: result.Record
            );
            budget.Token.ThrowIfCancellationRequested();
            using var owner = new FixtureOwner(root, files);
            await using var manager = new ProcessManager(owner, true, new NoElevation(), source);
            var observed = await manager
                .ObserveAsync([Interpreter], budget.Token)
                .ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            result.Complete = observed.Complete;
            result.Issues = DiagnosticIssueCodes(observed.Issues);
            result.ObservedOwned = observed.Processes.Count(process =>
                approved.Any(path => ScriptPaths.Equal(path, process.Identity.ScriptPath))
            );
            result.Status = observed.Complete
                ? result.ObservedOwned == 0
                    ? DiagnosticStatus.ObservedComplete
                    : DiagnosticStatus.UnexpectedOwnedProcesses
                : DiagnosticStatus.ObservedIncomplete;
            if (observed.Complete)
                result.Stage = DiagnosticStage.ObservationComplete;
            else if (result.Stage == DiagnosticStage.ScopeApproved)
                result.Stage = DiagnosticStage.ProjectionIncompleteReasonUnavailable;
        }
        catch (Exception error)
        {
            result.Cancelled = error is OperationCanceledException;
            result.TimedOut = budget.IsCancellationRequested;
            result.Complete = null;
            result.Status =
                result.TimedOut ? DiagnosticStatus.TimedOut
                : result.Cancelled ? DiagnosticStatus.Cancelled
                : error is ArgumentException ? DiagnosticStatus.InvalidRequest
                : DiagnosticStatus.Failed;
            result.ExceptionClass = error switch
            {
                OperationCanceledException => "Cancellation",
                System.Management.ManagementException => "Management",
                Win32Exception => "Win32",
                UnauthorizedAccessException => "Access",
                IOException => "IO",
                ArgumentException => "Argument",
                InvalidOperationException => "InvalidOperation",
                _ => "Other",
            };
            result.ProviderCode = error switch
            {
                System.Management.ManagementException provider => provider.HResult,
                Win32Exception native => native.NativeErrorCode,
                _ => null,
            };
        }
        finally
        {
            lease?.Dispose();
            result.EndedUtc = DateTimeOffset.UtcNow;
        }
        if (ownedRoot is not null)
        {
            try
            {
                result.Persisted = true;
                NewFile(
                    Path.Combine(ownedRoot, "diagnostic-result.json"),
                    Utf8.GetString(DiagnosticJson(result))
                );
            }
            catch
            {
                result.Persisted = false;
                result.Status = DiagnosticStatus.Failed;
                result.Stage = DiagnosticStage.ReportWriteFailed;
            }
        }
        Console.WriteLine(Utf8.GetString(DiagnosticJson(result)));
        return
            result.Status
                is DiagnosticStatus.ObservedComplete
                    or DiagnosticStatus.ObservedIncomplete
            ? 0
            : 1;
    }

    internal static void ReadOnlyDiagnosticContractChecks(string root, Action<bool, string> check)
    {
        using var self = Process.GetCurrentProcess();
        var ordinary = new ProcessCommandRow(
            int.MaxValue,
            "ordinary-host.exe",
            null,
            null,
            null,
            self.SessionId
        );
        var approved = new[] { Path.Combine(root, "first-inert.ahk") };
        var stages = new List<DiagnosticStage>();
        _ = new ScopedObservation(
            new ScopeRows(ordinary),
            approved,
            diagnostic: (stage, _) => stages.Add(stage)
        ).Read(CancellationToken.None);
        check(
            stages.SequenceEqual(
                new[]
                {
                    DiagnosticStage.SourceReadStarted,
                    DiagnosticStage.SourceRead,
                    DiagnosticStage.ScopeApproved,
                }
            ),
            "fixed diagnostic stages for synthetic source approval"
        );
        bool callbackFailed = false;
        try
        {
            _ = new ScopedObservation(
                new ScopeRows(ordinary),
                approved,
                diagnostic: (_, _) => throw new InvalidOperationException()
            ).Read(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            callbackFailed = true;
        }
        check(callbackFailed, "diagnostic callback failure cannot return an approved source");
        string outside = Path.Combine(root, "never-created-private.ahk");
        stages.Clear();
        var outsideArgv = ordinary with
        {
            Name = "AutoHotkey64.exe",
            Pid = self.Id,
            CommandLine =
                ScriptPaths.QuoteArgument(Environment.ProcessPath!)
                + " "
                + ScriptPaths.QuoteArgument(outside),
        };
        bool refused = false;
        try
        {
            _ = new ScopedObservation(
                new ScopeRows(outsideArgv),
                approved,
                diagnostic: (stage, _) => stages.Add(stage)
            ).Read(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        check(
            refused
                && stages.Contains(DiagnosticStage.UnapprovedArgv)
                && !stages.Contains(DiagnosticStage.ScopeApproved)
                && !File.Exists(outside),
            "fixed diagnostic refusal occurs before unapproved body access"
        );
        var frame = new ReadOnlyDiagnostic
        {
            Issues = DiagnosticIssueCodes([
                new(ReasonCode.ObservationIncomplete, "SYNTHETIC_PRIVATE_DETAIL_DO_NOT_PRINT"),
            ]),
        };
        string json = Utf8.GetString(DiagnosticJson(frame));
        check(
            json.Contains("ObservationIncomplete", StringComparison.Ordinal)
                && !json.Contains(
                    "SYNTHETIC_PRIVATE_DETAIL_DO_NOT_PRINT",
                    StringComparison.Ordinal
                ),
            "diagnostic JSON retains only sanitized issue code"
        );
    }

    public static async Task RunAsync(string root)
    {
        root = ScriptPaths.LocalPath(root);
        NormalToken();
        ValidateInterpreter();
        Directories(Path.GetDirectoryName(root)!);
        if (!CreateDirectory(root, 0))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Fresh exclusive fixture directory required."
            );
        using var lease = new FileStream(
            Path.Combine(root, "fixture-lease"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var scenario = new Scenario(root);
        Exception? failure = null;
        try
        {
            await scenario.RunAsync();
        }
        catch (Exception ex)
        {
            failure = ex;
            scenario.Record("sequence-failed", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            await scenario.CleanupAsync();
        }
        if (failure is not null || scenario.CleanupPending)
            throw new InvalidOperationException(
                "Owned AHK validation failed; preserve fixture receipts and inspect cleanup-pending before another action.",
                failure
            );
        scenario.Record(
            "PASS",
            "owned fixture complete; all owned exits confirmed; startup/elevation writes zero"
        );
    }

    static void NormalToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (
            !GetTokenInformation(
                identity.AccessToken,
                20,
                out int elevated,
                sizeof(int),
                out int bytes
            )
            || bytes != sizeof(int)
            || elevated != 0
            || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
        )
            throw new InvalidOperationException("A normal non-elevated token is required.");
    }

    static void ValidateInterpreter()
    {
        var verified = AutoHotkeyDiscovery.Validate(Interpreter);
        if (
            !Version.TryParse(verified.Version, out var version)
            || version.Major != 2
            || verified.Hash != InterpreterHash
            || !ScriptPaths.Equal(verified.Path, Path.GetFullPath(Interpreter))
        )
            throw new InvalidOperationException(
                "Approved AutoHotkey v2 interpreter identity changed."
            );
    }

    static void Directories(string path)
    {
        for (
            string? current = Path.GetFullPath(path);
            current is not null;
            current = Path.GetDirectoryName(current)
        )
            if (
                !Directory.Exists(current)
                || (
                    File.GetAttributes(current)
                    & (FileAttributes.Directory | FileAttributes.ReparsePoint)
                ) != FileAttributes.Directory
            )
                throw new IOException("Fixture directory or ancestor is missing or redirected.");
    }

    static string Hash(string path) => ScriptPaths.Fingerprint(ScriptPaths.Canonical(path, false));

    static string NativeBinaryHash(string path, long expectedBytes, string expectedHash)
    {
        const long maximumBinaryBytes = 512L * 1024 * 1024;
        if (expectedBytes <= 0 || expectedBytes > maximumBinaryBytes || expectedHash.Length != 64)
            throw new IOException("Invalid native executable binding.");
        using var input = new FileStream(
            ScriptPaths.Canonical(path, false),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        if (
            input.Length != expectedBytes
            || !ProcessNative.GetFileInformationByHandle(input.SafeFileHandle, out var before)
            || before.Links != 1
        )
            throw new IOException("Native executable identity changed.");
        string hash = Convert.ToHexStringLower(SHA256.HashData(input));
        if (
            !ProcessNative.GetFileInformationByHandle(input.SafeFileHandle, out var after)
            || before.Volume != after.Volume
            || before.IndexHigh != after.IndexHigh
            || before.IndexLow != after.IndexLow
            || before.Creation != after.Creation
            || before.Write != after.Write
            || before.SizeHigh != after.SizeHigh
            || before.SizeLow != after.SizeLow
            || after.Links != 1
            || input.Length != expectedBytes
            || hash != expectedHash
        )
            throw new IOException("Native executable identity changed.");
        return hash;
    }

    internal static void NativeBinaryHashChecks(string path, Action<bool, string> check)
    {
        string approved = Path.GetFullPath(
            "V:/Repos/Bezoro.AhkManager/Presentation.Electron/node_modules/electron/dist/electron.exe"
        );
        if (!ScriptPaths.Equal(Path.GetFullPath(path), approved))
            throw new IOException("Only the approved native binary is a regression input.");
        bool scriptLimit = false;
        try
        {
            _ = Hash(approved);
        }
        catch (IOException)
        {
            scriptLimit = true;
        }
        check(
            scriptLimit && new FileInfo(approved).Length == ElectronBytes,
            "existing script fingerprint size cap remains unchanged for approved large binary"
        );
        check(
            NativeBinaryHash(approved, ElectronBytes, ElectronHash) == ElectronHash,
            "pinned executable gets complete SHA256 with exact size and stable native file identity"
        );
        bool wrongSize = false,
            wrongHash = false;
        try
        {
            _ = NativeBinaryHash(approved, ElectronBytes - 1, ElectronHash);
        }
        catch (IOException)
        {
            wrongSize = true;
        }
        try
        {
            _ = NativeBinaryHash(approved, ElectronBytes, new string('0', 64));
        }
        catch (IOException)
        {
            wrongHash = true;
        }
        check(wrongSize, "native binary expected-size mismatch is refused");
        check(wrongHash, "native binary expected-hash mismatch is refused");
    }

    static void NewFile(string path, string text)
    {
        Directories(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read
        );
        stream.Write(Utf8.GetBytes(text));
        stream.Flush(true);
    }

    static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    // A record must be newly appended, complete, singular, and exactly tied to this attempt.
    static bool Receipt(
        byte[] checkpoint,
        byte[] current,
        string nonce,
        int pid,
        int attempt,
        int decision
    )
    {
        if (
            current.Length > 65536
            || current.Length <= checkpoint.Length
            || !current.AsSpan(0, checkpoint.Length).SequenceEqual(checkpoint)
        )
            return false;
        try
        {
            string added = Utf8.GetString(current.AsSpan(checkpoint.Length));
            string[] lines = added.Split('\n');
            if (lines.Length != 2 || lines[1] != "")
                return false;
            string[] fields = lines[0].TrimEnd('\r').Split('|');
            return fields.Length == 5
                && fields[0] == nonce
                && fields[1] == pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && fields[2] == attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && fields[3] == "Close"
                && fields[4]
                    == decision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    static bool VetoProof(
        OperationResult result,
        bool sameAlive,
        byte[] checkpoint,
        byte[] current,
        string nonce,
        int pid
    ) =>
        result.Outcome == Outcome.Unknown
        && result.Code == ReasonCode.ExitNotObserved
        && sameAlive
        && Receipt(checkpoint, current, nonce, pid, 1, 1);

    sealed class Scenario(string root)
    {
        readonly string script = Path.Combine(root, "owned harmless.ahk"),
            veto = Path.Combine(root, "veto harmless.ahk");
        readonly string nonce = Guid.NewGuid().ToString("N");
        readonly Dictionary<(int, long), ProcessIdentity> known = [];
        readonly Dictionary<string, string> expectedFiles = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<(int, long)> uncertainStops = [];
        readonly NoElevation elevation = new();
        readonly NoStartup startup = new();
        readonly Stopwatch sequence = Stopwatch.StartNew();
        readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(180));
        ScopedObservation? observation;
        FixtureProcessObservation? fixtureObservation;
        bool uncertainLaunch;
        public bool CleanupPending { get; private set; }
        string ReceiptPath => Path.Combine(root, "onexit-receipt.txt");
        string Marker => Path.Combine(root, "allow-exit");

        static (int, long) Key(ProcessIdentity id) => (id.Pid, id.CreationFileTime);

        public void Record(string stage, string detail)
        {
            Directories(root);
            string line =
                DateTimeOffset.UtcNow.ToString("O")
                + "\t"
                + Escape(stage)
                + "\t"
                + Escape(detail)
                + Environment.NewLine;
            File.AppendAllText(Path.Combine(root, "owned-ahk-receipts.txt"), line, Utf8);
            Console.Write(line);
        }

        void Check(bool value, string name)
        {
            Record(value ? "PASS" : "FAIL", name);
            if (!value)
                throw new InvalidOperationException(name);
        }

        void ScriptFile(string path, string text)
        {
            string expected = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));
            NewFile(path, text);
            expectedFiles.Add(Path.GetFullPath(path), expected);
            Check(Hash(path) == expected, "emitted-fixture-bytes-match-immutable-expected-hash");
        }

        void Identity(string stage, ProcessIdentity id) =>
            Record(
                stage,
                string.Join(
                    "\t",
                    id.Pid,
                    id.CreationFileTime,
                    id.ExecutablePath,
                    id.ScriptPath,
                    id.UserSid,
                    id.SessionId,
                    id.ScriptHash,
                    id.InterpreterHash
                )
            );

        void Result(string stage, OperationResult result) =>
            Record(stage, result.Outcome + "/" + result.Code + "/" + result.Detail);

        void Room(int seconds = 20)
        {
            budget.Token.ThrowIfCancellationRequested();
            if (TimeSpan.FromSeconds(180) - sequence.Elapsed < TimeSpan.FromSeconds(seconds))
                throw new TimeoutException(
                    "No budget remains for another effect and its existing confirmation deadline."
                );
        }

        bool Exited(ProcessIdentity id)
        {
            using var held = new NativeProcessIdentity(id.Pid);
            if (held.Exited)
                return true;
            if (held.TryRead(out var stamp) && stamp.Created != id.CreationFileTime)
                return true;
            try
            {
                using var process = Process.GetProcessById(id.Pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        async Task ProofAsync(
            ProcessManager manager,
            ProcessIdentity expected,
            CancellationToken ct
        )
        {
            ValidateInterpreter();
            Directories(root);
            using var user = WindowsIdentity.GetCurrent();
            using var self = Process.GetCurrentProcess();
            Check(
                expected.UserSid == user.User!.Value
                    && expected.SessionId == self.SessionId
                    && ScriptPaths.Equal(expected.ExecutablePath, Path.GetFullPath(Interpreter))
                    && expected.InterpreterHash == InterpreterHash
                    && expected.ScriptHash == Hash(expected.ScriptPath),
                "approved-user-session-interpreter-and-file-hashes"
            );
            var rows = await manager.ObserveAsync([Interpreter], ct);
            var matches = rows
                .Processes.Where(p => ScriptPaths.Equal(p.Identity.ScriptPath, expected.ScriptPath))
                .ToArray();
            using var held = new NativeProcessIdentity(expected.Pid);
            Check(
                rows.Complete
                    && matches.Length == 1
                    && NativeProcessIdentity.Same(matches[0].Identity, expected)
                    && held.Matches(expected)
                    && matches[0].WindowVerified
                    && AhkWindowControl.FindWindow(expected) != 0,
                "full-eight-field-live-identity-and-hidden-window"
            );
            Check(matches[0].Paused is null, "pause-remains-unobservable-no-toggle-dispatched");
            Identity("identity-proof", expected);
        }

        async Task<ProcessIdentity> StartAsync(ProcessManager manager, string path)
        {
            Room();
            var launch = await manager.StartAsync(Interpreter, path, false, budget.Token);
            Result("start", launch.Result);
            if (
                launch.Result.Outcome == Outcome.Unknown
                || launch.Process is null && launch.Result.Succeeded
            )
                uncertainLaunch = true;
            Check(launch.Result.Succeeded && launch.Process is not null, "real-start-confirmed");
            var id = launch.Process!.Identity;
            known[Key(id)] = id;
            await ProofAsync(manager, id, budget.Token);
            return id;
        }

        async Task<OperationResult> StopAsync(
            ProcessManager manager,
            ProcessIdentity id,
            CancellationToken ct,
            bool cleanup = false
        )
        {
            await ProofAsync(manager, id, ct);
            if (!cleanup)
                Room();
            uncertainStops.Add(Key(id));
            Identity("before-stop", id);
            var result = await manager.ControlAsync(id, ScriptOperation.Stop, ct);
            Result(cleanup ? "cleanup-stop" : "stop", result);
            if (result.Succeeded && Exited(id))
                uncertainStops.Remove(Key(id));
            return result;
        }

        void ReceiptRegressions()
        {
            byte[] Good(string text) => Utf8.GetBytes(text);
            string line = nonce + "|42|1|Close|1\n";
            var expected = new OperationResult(Outcome.Unknown, ReasonCode.ExitNotObserved);
            Check(VetoProof(expected, true, [], Good(line), nonce, 42), "receipt-valid-veto-proof");
            foreach (
                var row in new[]
                {
                    ("absent", Array.Empty<byte>()),
                    ("wrong-nonce", Good("old|42|1|Close|1\n")),
                    ("wrong-pid", Good(nonce + "|43|1|Close|1\n")),
                    ("wrong-attempt", Good(nonce + "|42|2|Close|1\n")),
                    ("wrong-decision", Good(nonce + "|42|1|Close|0\n")),
                    ("wrong-reason", Good(nonce + "|42|1|Exit|1\n")),
                    ("partial", Good(line.TrimEnd('\n'))),
                    ("duplicate", Good(line + line)),
                }
            )
                Check(
                    !VetoProof(expected, true, [], row.Item2, nonce, 42) && !File.Exists(Marker),
                    "reject-receipt-" + row.Item1 + "-without-release"
                );
            Check(
                !VetoProof(expected, true, Good(line), Good(line), nonce, 42),
                "reject-old-checkpoint-receipt"
            );
            Check(
                !VetoProof(
                    new(Outcome.Failed, ReasonCode.WindowUnverified),
                    true,
                    [],
                    Good(line),
                    nonce,
                    42
                ),
                "reject-refusal-before-callback"
            );
            Check(
                !VetoProof(
                    new(Outcome.Unknown, ReasonCode.DispatchFailed),
                    true,
                    [],
                    Good(line),
                    nonce,
                    42
                ),
                "reject-dispatch-unknown-without-expected-result"
            );
            Check(
                !VetoProof(expected, false, [], Good(line), nonce, 42),
                "reject-changed-or-not-live-identity"
            );
        }

        byte[] ReadReceipt()
        {
            Directories(root);
            if (!File.Exists(ReceiptPath))
                return [];
            _ = ScriptPaths.Canonical(ReceiptPath, false);
            using var input = new FileStream(
                ReceiptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            if (input.Length > 65536)
                throw new InvalidDataException("Fixture receipt exceeded bound.");
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            return bytes;
        }

        async Task<byte[]> WaitReceiptAsync(
            byte[] checkpoint,
            ProcessIdentity id,
            int attempt,
            int decision
        )
        {
            var deadline = Stopwatch.StartNew();
            byte[] current;
            do
            {
                budget.Token.ThrowIfCancellationRequested();
                current = ReadReceipt();
                if (Receipt(checkpoint, current, nonce, id.Pid, attempt, decision))
                    return current;
                await Task.Delay(25, budget.Token);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(1));
            return current;
        }

        public async Task RunAsync()
        {
            Record(
                "fixture",
                "nonce="
                    + nonce
                    + ";pid="
                    + Environment.ProcessId
                    + ";argv="
                    + string.Join(" ", Environment.GetCommandLineArgs())
                    + ";sequenceBudget=180s;cleanupBudget=30s"
            );
            ScriptFile(
                script,
                "#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\nPersistent\nWinSetTitle(A_ScriptDir \"\\veto harmless.ahk - AutoHotkey v2\", A_ScriptHwnd)\nSetTimer(() => 0, 1000)\n"
            );
            ScriptFile(
                veto,
                $$"""
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
                """
            );
            ReceiptRegressions();
            fixtureObservation = new FixtureProcessObservation(root, [script, veto], Interpreter);
            observation = new ScopedObservation(
                fixtureObservation,
                [script, veto],
                count => Record("scope-approved", "rows=" + count)
            );
            using var owner = new FixtureOwner(root, expectedFiles);
            await using var manager = new ProcessManager(owner, true, elevation, observation);
            var active = await StartAsync(manager, script);
            Check(
                ScriptPaths.Equal(active.ScriptPath, script)
                    && !ScriptPaths.Equal(active.ScriptPath, veto),
                "argv-B-remains-B-when-caption-imitates-A"
            );
#if BASELINE_CAPTION_PROOF
            nint captionWindow = AhkWindowControl.FindWindow(active);
            Check(
                captionWindow != 0 && !BaselineAhkWindowControl.VerifyWindow(captionWindow, active),
                "baseline-caption-gate-rejects-independent-B-while-current-gate-accepts-B-readonly-characterization"
            );
#endif
            observation.Opaque = active;
            try
            {
                var proved = await manager.ObserveAsync([Interpreter], budget.Token);
                Check(
                    proved.Complete
                        && proved.Processes.Any(p =>
                            NativeProcessIdentity.Same(p.Identity, active)
                        ),
                    "complete-launch-receipt-retains-own-opaque-row"
                );
                await using var independent = new ProcessManager(
                    owner,
                    true,
                    elevation,
                    observation
                );
                var ambiguous = await independent.ObserveAsync([Interpreter], budget.Token);
                Check(
                    !ambiguous.Complete
                        && ambiguous.Processes.All(p => p.Identity.Pid != active.Pid),
                    "opaque-row-without-argv-or-receipt-remains-ambiguous-caption-never-adopts"
                );
            }
            finally
            {
                observation.Opaque = null;
            }

            Room();
            var duplicate = await manager.StartAsync(Interpreter, script, false, budget.Token);
            Result("duplicate", duplicate.Result);
            Check(
                duplicate.Result.Outcome == Outcome.Failed
                    && duplicate.Result.Code == ReasonCode.AlreadyRunning
                    && duplicate.Process is null,
                "duplicate-refused-without-new-instance"
            );
            foreach (var operation in new[] { "suspend", "resume" })
            {
                await ProofAsync(manager, active, budget.Token);
                Room();
                var before = await manager.ObserveAsync([Interpreter], budget.Token);
                bool? was = before
                    .Processes.Single(p => NativeProcessIdentity.Same(p.Identity, active))
                    .Suspended;
                var result = await manager.ControlAsync(
                    active,
                    ScriptOperation.ToggleSuspend,
                    budget.Token
                );
                Result(operation, result);
                var after = await manager.ObserveAsync([Interpreter], budget.Token);
                var target = after.Processes.Single(p =>
                    NativeProcessIdentity.Same(p.Identity, active)
                );
                Check(
                    result.Succeeded
                        && was.HasValue
                        && target.Suspended.HasValue
                        && was != target.Suspended
                        && target.Paused is null,
                    operation + "-confirmed-by-observation"
                );
            }
            foreach (
                var negative in new[]
                {
                    active with
                    {
                        CreationFileTime = active.CreationFileTime + 1,
                    },
                    active with
                    {
                        ScriptHash = new string('0', 64),
                    },
                    active with
                    {
                        ScriptPath = Path.Combine(root, "not-owned.ahk"),
                    },
                }
            )
            {
                await ProofAsync(manager, active, budget.Token);
                Room();
                var result = await manager.ControlAsync(
                    negative,
                    ScriptOperation.Stop,
                    budget.Token
                );
                Result("synthetic-identity-rejection", result);
                Check(
                    result.Outcome == Outcome.Failed
                        && result.Code
                            is ReasonCode.IdentityChanged
                                or ReasonCode.EffectOwnerUnavailable,
                    "synthetic-owner-path-hash-or-creation-refused"
                );
                await ProofAsync(manager, active, budget.Token);
            }
            owner.Permit = false;
            try
            {
                Room();
                var denied = await manager.ControlAsync(active, ScriptOperation.Stop, budget.Token);
                Result("owner-denied", denied);
                Check(
                    denied.Outcome == Outcome.Failed
                        && denied.Code == ReasonCode.EffectOwnerUnavailable,
                    "owner-refused-before-dispatch"
                );
            }
            finally
            {
                owner.Permit = true;
            }
            await ProofAsync(manager, active, budget.Token);
            var stopped = await StopAsync(manager, active, budget.Token);
            Check(stopped.Succeeded && Exited(active), "normal-graceful-stop-exit-confirmed");

            var vetoId = await StartAsync(manager, veto);
            Check(
                !File.Exists(ReceiptPath) && !File.Exists(Marker),
                "onexit-receipt-and-release-marker-initially-absent"
            );
            byte[] checkpoint = ReadReceipt();
            Record("onexit-checkpoint", "attempt=1;bytes=" + checkpoint.Length);
            Identity("before-veto", vetoId);
            var vetoResult = Environment.GetEnvironmentVariable("AHK_OWNED_TRASH_HELPER") is null
                ? await StopAsync(manager, vetoId, budget.Token)
                : await RecycleVetoAsync(vetoId);
            byte[] receipt = await WaitReceiptAsync(checkpoint, vetoId, 1, 1);
            await ProofAsync(manager, vetoId, budget.Token);
            Check(
                VetoProof(vetoResult, !Exited(vetoId), checkpoint, receipt, nonce, vetoId.Pid),
                "fresh-onexit-close-veto-receipt-and-same-live-identity"
            );
            Record(
                "veto-proven",
                "Only this expected veto permits the explicit fixture release and deliberate second Stop."
            );
            Room();
            NewFile(Marker, nonce);
            checkpoint = receipt;
            Record("onexit-checkpoint", "attempt=2;bytes=" + checkpoint.Length);
            var allowed = await StopAsync(manager, vetoId, budget.Token);
            receipt = await WaitReceiptAsync(checkpoint, vetoId, 2, 0);
            Check(
                allowed.Succeeded
                    && Receipt(checkpoint, receipt, nonce, vetoId.Pid, 2, 0)
                    && Exited(vetoId),
                "explicit-release-second-receipt-and-confirmed-exit"
            );
            await ServiceLifecycleAsync();
            Check(
                startup.Writes == 0 && elevation.Attempts == 0,
                "zero-startup-writes-and-uac-attempts"
            );
        }

        async Task ServiceLifecycleAsync()
        {
            string data = Path.Combine(root, "service-data");
            Directory.CreateDirectory(data);
            var store = new SettingsStore(Path.Combine(data, "settings.json"));
            var loaded = await store.LoadAsync(budget.Token);
            Check(!loaded.ReadOnly, "fixture-settings-loaded-empty");
            var seeded = await store.SaveAsync(
                loaded.Document with
                {
                    Settings = new()
                    {
                        InterpreterPath = Interpreter,
                        LaunchManagerAtLogin = false,
                        ShowWindowAtLogin = false,
                    },
                },
                budget.Token
            );
            Check(
                seeded.Succeeded,
                "fixture-settings-preseeded-with-approved-interpreter-startup-off"
            );
            using var owner = new FixtureOwner(root, expectedFiles);
            var processes = new ProcessManager(owner, true, elevation, observation);
            var service = new ManagerService(
                data,
                owner,
                processes,
                new UserStartupService(startup, Environment.ProcessPath!),
                Path.Combine(root, "legacy-absent")
            );
            ProcessIdentity? replacement = null;
            bool recycled = false;
            try
            {
                await service.LoadAsync(budget.Token);
                var added = await service.AddFilesAsync([script], budget.Token);
                Check(
                    added.Result.Succeeded && added.Added.Length == 1,
                    "real-service-adds-only-owned-script"
                );
                Guid id = added.Added[0];
                SelectionContext Selection() =>
                    new(id, new ScriptQuery(), service.Snapshot.Revision);
                Room(45);
                var plan = await service.PrepareAsync(
                    Selection(),
                    ScriptOperation.Start,
                    budget.Token
                );
                Check(plan.Allowed, "service-start-plan");
                var result = await service.PerformAsync(plan, Selection(), true, budget.Token);
                Result("service-start", result);
                if (!result.Succeeded)
                    uncertainLaunch = true;
                Check(
                    result.Succeeded && service.Snapshot.Scripts.Single().Process is not null,
                    "service-start-confirmed"
                );
                var old = service.Snapshot.Scripts.Single().Process!.Identity;
                known[Key(old)] = old;
                await ProofAsync(processes, old, budget.Token);
                Room(45);
                plan = await service.PrepareAsync(
                    Selection(),
                    ScriptOperation.Restart,
                    budget.Token
                );
                Check(
                    plan.Allowed && NativeProcessIdentity.Same(plan.ExpectedIdentity, old),
                    "service-restart-plan-bound-to-original"
                );
                uncertainStops.Add(Key(old));
                result = await service.PerformAsync(plan, Selection(), true, budget.Token);
                Result("service-restart", result);
                if (!result.Succeeded)
                    uncertainLaunch = true;
                Check(result.Succeeded && Exited(old), "service-restart-confirms-old-exit");
                uncertainStops.Remove(Key(old));
                replacement = service.Snapshot.Scripts.Single().Process?.Identity;
                Check(
                    replacement is not null && !NativeProcessIdentity.Same(old, replacement),
                    "service-restart-new-complete-identity"
                );
                known[Key(replacement!)] = replacement!;
                await ProofAsync(processes, replacement!, budget.Token);
                var observed = await processes.ObserveAsync([Interpreter], budget.Token);
                Check(
                    observed.Complete
                        && observed.Processes.Count(p =>
                            ScriptPaths.Equal(p.Identity.ScriptPath, script)
                        ) == 1,
                    "service-restart-exactly-one-replacement"
                );
                if (Environment.GetEnvironmentVariable("AHK_OWNED_TRASH_HELPER") is string helper)
                {
                    Room(45);
                    var recycle = await service.PrepareRecycleAsync(Selection(), budget.Token);
                    Check(
                        recycle.Allowed
                            && NativeProcessIdentity.Same(recycle.ExpectedIdentity, replacement),
                        "recycle-plan-binds-real-running-replacement"
                    );
                    uncertainStops.Add(Key(replacement!));
                    var authorized = await service.AuthorizeRecycleAsync(
                        recycle.OperationId,
                        recycle.Selection,
                        true,
                        budget.Token
                    );
                    Check(
                        authorized.Result.Outcome == Outcome.Pending
                            && authorized.File is not null
                            && authorized.Stopped
                            && Exited(replacement!),
                        "production-stop-and-exit-before-native-trash"
                    );
                    uncertainStops.Remove(Key(replacement!));
                    var receipt = await NativeTrashAsync(
                        helper,
                        recycle.OperationId,
                        authorized.File!
                    );
                    var completed = await service.CompleteRecycleAsync(
                        recycle.OperationId,
                        receipt,
                        "Owned Electron native fixture receipt",
                        CancellationToken.None
                    );
                    Result("native-recycle-complete", completed);
                    Check(
                        receipt == Outcome.Confirmed
                            && completed.Succeeded
                            && !File.Exists(script)
                            && service.Snapshot.Scripts.IsEmpty,
                        "real-electron-promise-receipt-and-catalog-removal-confirmed"
                    );
                    Check(
                        known
                            .Values.Where(id => ScriptPaths.Equal(id.ScriptPath, script))
                            .All(Exited),
                        "all-known-recycled-script-identities-confirmed-exited"
                    );
                    fixtureObservation!.ConfirmRecycledFile(
                        script,
                        expectedFiles[Path.GetFullPath(script)]
                    );
                    recycled = true;
                }
            }
            finally
            {
                await service.DisposeAsync();
                Record("service-disposed", "Disposal releases handles and owner, without Stop.");
            }
            if (recycled)
            {
                Check(
                    replacement is not null && Exited(replacement),
                    "recycling-never-restarts-stopped-instance"
                );
                return;
            }
            Check(
                replacement is not null && !Exited(replacement),
                "core-dispose-leaves-same-replacement-alive"
            );
            using var proofOwner = new FixtureOwner(root, expectedFiles);
            await using var proof = new ProcessManager(proofOwner, true, elevation, observation);
            await ProofAsync(proof, replacement!, budget.Token);
        }

        async Task<OperationResult> RecycleVetoAsync(ProcessIdentity expected)
        {
            Room();
            using var owner = new FixtureOwner(root, expectedFiles);
            var processes = new ProcessManager(owner, true, elevation, observation);
            await using var service = new ManagerService(
                Path.Combine(root, "recycle-veto-data"),
                owner,
                processes,
                new UserStartupService(startup, Environment.ProcessPath!),
                Path.Combine(root, "legacy-absent")
            );
            await service.LoadAsync(budget.Token);
            var added = await service.AddFilesAsync([veto], budget.Token);
            Guid id = added.Added.Single();
            SelectionContext selection = new(id, new(), service.Snapshot.Revision);
            var plan = await service.PrepareRecycleAsync(selection, budget.Token);
            Check(
                plan.Allowed && NativeProcessIdentity.Same(plan.ExpectedIdentity, expected),
                "recycle-veto-plan-exact-owned-identity"
            );
            uncertainStops.Add(Key(expected));
            var authorization = await service.AuthorizeRecycleAsync(
                plan.OperationId,
                selection,
                true,
                budget.Token
            );
            Check(
                authorization.File is null
                    && authorization.Result.Outcome == Outcome.Unknown
                    && File.Exists(veto)
                    && service.Snapshot.Scripts.Length == 1
                    && !File.Exists(Path.Combine(root, "native-trash-request.json")),
                "production-OnExit-veto-preserves-file-catalog-and-prevents-native-dispatch"
            );
            return authorization.Result;
        }

        async Task<Outcome> NativeTrashAsync(
            string helper,
            Guid operationId,
            ScriptFileIdentity file
        )
        {
            const string evidence =
                "C:/Users/Bezor/Documents/Codex/AhkManager-missing-script-2026-10-25";
            string exactHelper = Path.GetFullPath(
                Path.Combine(evidence, "implementation-native-trash-v1.cjs")
            );
            if (
                !ScriptPaths.Equal(Path.GetFullPath(helper), exactHelper)
                || !Path.GetFullPath(root)
                    .StartsWith(
                        Path.GetFullPath(evidence) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    )
            )
                throw new IOException(
                    "Native helper and owned root are outside approved evidence scope."
                );
            _ = ScriptPaths.Canonical(helper, false);
            string request = Path.Combine(root, "native-trash-request.json");
            string body =
                "{\"version\":1,\"root\":\""
                + Json(root)
                + "\",\"operationId\":\""
                + operationId
                + "\",\"file\":{\"path\":\""
                + Json(file.Path)
                + "\",\"fileId\":\""
                + Json(file.FileId)
                + "\",\"hash\":\""
                + file.Hash
                + "\"}}";
            NewFile(request, body);
            string electron = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../Presentation.Electron/node_modules/electron/dist/electron.exe"
                )
            );
            // Resolve against the repository, never the user profile or system PATH.
            if (!File.Exists(electron))
                electron =
                    "V:/Repos/Bezoro.AhkManager/Presentation.Electron/node_modules/electron/dist/electron.exe";
            electron = ScriptPaths.Canonical(electron, false);
            var info = new ProcessStartInfo(electron)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            info.ArgumentList.Add(exactHelper);
            info.ArgumentList.Add(request);
            Record(
                "native-helper-bound",
                "helperHash="
                    + Hash(exactHelper)
                    + ";electron="
                    + electron
                    + ";electronHash="
                    + NativeBinaryHash(electron, ElectronBytes, ElectronHash)
                    + ";requestHash="
                    + Hash(request)
            );
            using var child =
                Process.Start(info) ?? throw new IOException("Owned native helper did not start.");
            var stdout = new FileStream(
                Path.Combine(root, "native-helper.stdout.log"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            );
            var stderr = new FileStream(
                Path.Combine(root, "native-helper.stderr.log"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            );
            var capture = Task.WhenAll(
                child.StandardOutput.BaseStream.CopyToAsync(stdout),
                child.StandardError.BaseStream.CopyToAsync(stderr)
            );
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await child.WaitForExitAsync(deadline.Token);
                await capture;
                await stdout.DisposeAsync();
                await stderr.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
                CleanupPending = true;
                Record(
                    "native-helper-unknown",
                    "PID="
                        + child.Id
                        + "; no kill/retry; retain late receipt for explicit coordinator recovery."
                );
                return Outcome.Unknown;
            }
            string receiptPath = request + ".receipt.json";
            if (!File.Exists(receiptPath))
                return Outcome.Unknown;
            using var receipt = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllBytesAsync(receiptPath)
            );
            var value = receipt.RootElement;
            if (
                value.GetProperty("operationId").GetGuid() != operationId
                || !ScriptPaths.Equal(value.GetProperty("path").GetString(), file.Path)
                || value.GetProperty("file").GetProperty("hash").GetString() != file.Hash
                || value.GetProperty("versions").GetProperty("electron").GetString() != "45.0.0"
            )
                return Outcome.Unknown;
            Record(
                "native-helper-receipt",
                "exit="
                    + child.ExitCode
                    + ";hash="
                    + Hash(receiptPath)
                    + ";receipt="
                    + value.GetProperty("receipt").GetString()
            );
            return value.GetProperty("receipt").GetString() == "Confirmed" && child.ExitCode == 0
                ? Outcome.Confirmed
                : Outcome.Failed;
            static string Json(string text) =>
                System.Text.Json.JsonEncodedText.Encode(text).ToString();
        }

        public async Task CleanupAsync()
        {
            using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var clock = Stopwatch.StartNew();
            try
            {
                if (observation is null)
                    return;
                using var owner = new FixtureOwner(root, expectedFiles);
                await using var manager = new ProcessManager(owner, true, elevation, observation);
                foreach (var id in known.Values)
                {
                    if (Exited(id))
                    {
                        Identity("cleanup-exit-already-confirmed", id);
                        continue;
                    }
                    if (
                        uncertainStops.Contains(Key(id))
                        || TimeSpan.FromSeconds(30) - clock.Elapsed < TimeSpan.FromSeconds(20)
                    )
                    {
                        Pending(
                            id,
                            "Prior Stop uncertainty/veto or insufficient independent cleanup budget; no retry."
                        );
                        continue;
                    }
                    var result = await StopAsync(manager, id, cleanupBudget.Token, true);
                    if (!result.Succeeded || !Exited(id))
                        Pending(id, "Graceful cleanup did not confirm exit; no retry.");
                }
                var final = await manager.ObserveAsync([Interpreter], cleanupBudget.Token);
                if (!final.Complete)
                {
                    CleanupPending = true;
                    Record("cleanup-pending", "Final observation incomplete.");
                }
                foreach (
                    var row in final.Processes.Where(p =>
                        ScriptPaths.Equal(p.Identity.ScriptPath, script)
                        || ScriptPaths.Equal(p.Identity.ScriptPath, veto)
                    )
                )
                    Pending(
                        row.Identity,
                        "Owned fixture still observed; preserve and ask coordinator."
                    );
                if (uncertainLaunch)
                {
                    CleanupPending = true;
                    Record(
                        "cleanup-pending",
                        "Earlier launch/restart uncertainty; observation cannot authorize automatic retry or PASS."
                    );
                }
                if (startup.Writes != 0 || elevation.Attempts != 0)
                {
                    CleanupPending = true;
                    Record("cleanup-pending", "Forbidden startup/elevation adapter was attempted.");
                }
                Record(
                    CleanupPending ? "cleanup-pending" : "cleanup-confirmed",
                    "All files and receipts retained; no forced termination or recursive deletion."
                );
            }
            catch (Exception ex)
            {
                CleanupPending = true;
                Record("cleanup-pending", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                budget.Dispose();
            }
        }

        void Pending(ProcessIdentity id, string why)
        {
            CleanupPending = true;
            Identity("cleanup-pending-identity", id);
            Record("cleanup-pending", why);
        }
    }

    // Scope classification deliberately parses argv lexically; it never opens or hashes an
    // unapproved script. File identity/hash checks remain the separate owned-fixture guards.
    internal static string RequireScopedScript(
        string command,
        string executable,
        IReadOnlyCollection<string> approved
    )
    {
        string[] argv = ScriptPaths.SplitCommand(command);
        int index =
            argv.Length > 1 && argv[1].Equals("/script", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 1;
        if (argv.Length <= index || !ScriptPaths.Equal(argv[0], executable))
            throw new InvalidOperationException("Fixture AHK argv is not independently proved.");
        string path;
        try
        {
            path = ScriptPaths.Lexical(argv[index]);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Fixture AHK script path is not absolute.");
        }
        if (!approved.Any(candidate => ScriptPaths.Equal(candidate, path)))
            throw new InvalidOperationException(
                "Unrelated AHK script encountered; abort before script body access or control."
            );
        return path;
    }

    internal static void ScopeChecks(string root, Action<bool, string> check)
    {
        string first = Path.Combine(root, "first-inert.ahk"),
            second = Path.Combine(root, "second-inert.ahk"),
            outside = Path.Combine(root, "never-created-private.ahk");
        string Command(string path) =>
            ScriptPaths.QuoteArgument(Interpreter) + " " + ScriptPaths.QuoteArgument(path);
        check(
            RequireScopedScript(Command(first), Interpreter, [first, second]) == first,
            "fixture scope accepts exact approved argv without opening script bodies"
        );
        bool refused = false;
        try
        {
            RequireScopedScript(Command(outside), Interpreter, [first, second]);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        check(
            refused && !File.Exists(outside),
            "outside fixture argv aborts even when private script body is absent and cannot be read"
        );
        using var self = Process.GetCurrentProcess();
        var ordinary = new ProcessCommandRow(
            int.MaxValue,
            "ordinary-host.exe",
            null,
            null,
            null,
            self.SessionId
        );
        int approved = 0;
        var scope = new ScopedObservation(
            new ScopeRows(ordinary),
            [first, second],
            _ => approved++
        );
        check(
            scope.Read(CancellationToken.None).Rows.Length == 1 && approved == 1,
            "fixture records approval after ignoring only out-of-domain inaccessible rows"
        );
        foreach (
            var row in new[]
            {
                ordinary with
                {
                    Name = "AutoHotkey64.exe",
                },
                ordinary with
                {
                    Name = "AutoHotkey64.exe",
                    Pid = self.Id,
                },
            }
        )
        {
            refused = false;
            try
            {
                _ = new ScopedObservation(
                    new ScopeRows(row),
                    [first, second],
                    _ => approved++
                ).Read(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            check(
                refused && approved == 1,
                "fixture rejects recognized inaccessible or new opaque candidate before approval and script body access"
            );
        }
        var outsideArgv = ordinary with
        {
            Name = "AutoHotkey64.exe",
            Pid = self.Id,
            CommandLine =
                ScriptPaths.QuoteArgument(Environment.ProcessPath!)
                + " "
                + ScriptPaths.QuoteArgument(outside),
        };
        refused = false;
        try
        {
            _ = new ScopedObservation(new ScopeRows(outsideArgv), [first, second]).Read(
                CancellationToken.None
            );
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        check(
            refused && !File.Exists(outside),
            "shared relevance guard rejects outside argv without opening unapproved script"
        );
    }

    internal static void FixtureScopeChecks(string root, Action<bool, string> check)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string owned = Path.Combine(root, "owned-inert.ahk");
        File.WriteAllText(owned, "#Requires AutoHotkey v2.0\n");
        long created = Directory.GetCreationTimeUtc(root).ToFileTimeUtc();
        string interpreter = Path.GetFullPath("C:/synthetic-approved-interpreter/AutoHotkey64.exe");
        string outside = Path.Combine(
            Path.GetDirectoryName(root)!,
            "never-created-foreign",
            "sentinel.ahk"
        );
        var self = new ProcessStamp(
            51,
            created - 200000,
            "C:\\synthetic-harness.exe",
            "S-1-5-21-synthetic-fixture",
            7
        );
        var foreign = new ProcessStamp(52, created - 100005, interpreter, self.Sid, self.Session);
        var security = new ProcessSecurity(false, 0x2000);
        var row = new ProcessCommandRow(
            foreign.Pid,
            "AutoHotkey64.exe",
            interpreter,
            ScriptPaths.QuoteArgument(interpreter) + " " + ScriptPaths.QuoteArgument(outside),
            foreign.Created - foreign.Created % 10,
            foreign.Session
        );
        var source = new MutableScopeRows(new([row], true, []));
        var baselineNative = new ScopeNativeState(foreign, foreign, security, security);
        var baselineHarness = new ScopeNativeState(self, self, security, security);
        var currentNative = baselineNative;
        var currentHarness = baselineHarness;
        IFixtureNativeProcess Open(int pid) =>
            new ScopeNativeHandle(
                pid == self.Pid ? currentHarness
                : pid == foreign.Pid ? currentNative
                : baselineNative with { Readable = false }
            );
        var fixture = new FixtureProcessObservation(
            root,
            [owned],
            interpreter,
            source,
            Open,
            self.Pid
        );
        check(!File.Exists(outside), "foreign sentinel remains nonexistent without body access");
        var observed = fixture.Read(CancellationToken.None);
        check(
            observed.Rows.IsEmpty,
            "pre-root normal full-native-identity foreign row excluded before downstream scope gate"
        );
        int approved = 0;
        _ = new ScopedObservation(new MutableScopeRows(observed), [owned], _ => approved++).Read(
            CancellationToken.None
        );
        check(
            approved == 1,
            "filtered synthetic population passes unchanged downstream scope gate"
        );
        void Retained(
            string label,
            ProcessCommandRow changed,
            ScopeNativeState? native = null,
            ScopeNativeState? owner = null
        )
        {
            currentNative = native ?? baselineNative;
            currentHarness = owner ?? baselineHarness;
            source.Frame = new([changed], true, []);
            var retained = fixture.Read(CancellationToken.None);
            check(
                retained.Complete && retained.Rows.Length == 1 && retained.Rows[0] == changed,
                label
            );
        }
        Retained("new PID is never adopted into closed baseline", row with { Pid = row.Pid + 1 });
        var reused = foreign with { Created = foreign.Created + 100 };
        Retained(
            "reused PID with consistent new birth is retained",
            row with
            {
                CreationFileTime = reused.Created - reused.Created % 10,
            },
            baselineNative with
            {
                Before = reused,
                After = reused,
            }
        );
        long oneTick = foreign.Created % 10 < 9 ? 1 : -1;
        var exactChanged = foreign with { Created = foreign.Created + oneTick };
        Retained(
            "exact native birth change inside same CIM microsecond is retained",
            row,
            baselineNative with
            {
                Before = exactChanged,
                After = exactChanged,
            }
        );
        var postRoot = foreign with { Created = created + 100 };
        Retained(
            "post-root native candidate is retained",
            row with
            {
                CreationFileTime = postRoot.Created - postRoot.Created % 10,
            },
            baselineNative with
            {
                Before = postRoot,
                After = postRoot,
            }
        );
        foreach (
            var changedStamp in new[]
            {
                foreign with
                {
                    Pid = foreign.Pid + 1,
                },
                foreign with
                {
                    Executable = "C:\\different-image.exe",
                },
                foreign with
                {
                    Sid = "S-1-5-21-different-synthetic",
                },
                foreign with
                {
                    Session = foreign.Session + 1,
                },
            }
        )
            Retained(
                "individual full native stamp change prevents exclusion",
                row,
                baselineNative with
                {
                    Before = changedStamp,
                    After = changedStamp,
                }
            );
        Retained(
            "elevated security is retained",
            row,
            baselineNative with
            {
                SecurityBefore = security with { Elevated = true },
                SecurityAfter = security with { Elevated = true },
            }
        );
        Retained(
            "different integrity is retained",
            row,
            baselineNative with
            {
                SecurityBefore = security with { IntegrityRid = 0x3000 },
                SecurityAfter = security with { IntegrityRid = 0x3000 },
            }
        );
        Retained(
            "unavailable native proof is retained",
            row,
            baselineNative with
            {
                Readable = false,
            }
        );
        Retained(
            "process ending during proof is retained",
            row,
            baselineNative with
            {
                LiveAfter = false,
            }
        );
        Retained(
            "native stamp changing during proof is retained",
            row,
            baselineNative with
            {
                After = foreign with { Created = foreign.Created + 1 },
            }
        );
        Retained(
            "security changing during proof is retained",
            row,
            baselineNative with
            {
                SecurityAfter = security with { Elevated = true },
            }
        );
        Retained(
            "harness privilege change prevents exclusion",
            row,
            owner: baselineHarness with
            {
                SecurityBefore = security with { Elevated = true },
                SecurityAfter = security with { Elevated = true },
            }
        );
        Retained(
            "unprovable harness prevents exclusion",
            row,
            owner: baselineHarness with
            {
                Readable = false,
            }
        );
        foreach (
            var changedRow in new[]
            {
                row with
                {
                    ExecutablePath = null,
                },
                row with
                {
                    CommandLine = null,
                },
                row with
                {
                    SessionId = null,
                },
                row with
                {
                    CreationFileTime = null,
                },
                row with
                {
                    ExecutablePath = "C:\\different-row-image.exe",
                },
                row with
                {
                    SessionId = row.SessionId + 1,
                },
                row with
                {
                    CreationFileTime = row.CreationFileTime + 10,
                },
            }
        )
            Retained("missing or inconsistent current row metadata is retained", changedRow);
        string Command(string path) =>
            ScriptPaths.QuoteArgument(interpreter) + " " + ScriptPaths.QuoteArgument(path);
        foreach (
            string command in new[]
            {
                Command(owned),
                Command(root),
                Command("relative.ahk"),
                Command(Path.Combine(Path.GetDirectoryName(root)!, "changed-nonexistent.ahk")),
                "\"unterminated",
                ScriptPaths.QuoteArgument(interpreter)
                    + " /ErrorStdOut "
                    + ScriptPaths.QuoteArgument(outside),
                Command(outside) + " extra",
                ScriptPaths.QuoteArgument("C:\\different-argv-image.exe")
                    + " "
                    + ScriptPaths.QuoteArgument(outside),
            }
        )
            Retained(
                "owned equal-root changed malformed relative or unsupported argv stays visible",
                row with
                {
                    CommandLine = command,
                }
            );
        currentNative = baselineNative;
        currentHarness = baselineHarness;
        source.Frame = new(
            [row],
            false,
            [new(ReasonCode.ObservationIncomplete, "synthetic issue not logged")]
        );
        var incomplete = fixture.Read(CancellationToken.None);
        check(
            !incomplete.Complete
                && incomplete.Rows.Length == 1
                && incomplete.Issues.SequenceEqual(source.Frame.Issues),
            "incomplete current WMI and issues propagate without hiding normal row"
        );
        bool rejectedInitial = false;
        try
        {
            _ = new FixtureProcessObservation(root, [owned], interpreter, source, Open, self.Pid);
        }
        catch (InvalidOperationException)
        {
            rejectedInitial = true;
        }
        check(rejectedInitial, "incomplete initial WMI cannot create an approved baseline");
        source.Frame = new([row with { CommandLine = null }], true, []);
        var notAdopted = new FixtureProcessObservation(
            root,
            [owned],
            interpreter,
            source,
            Open,
            self.Pid
        );
        source.Frame = new([row], true, []);
        check(
            notAdopted.Read(CancellationToken.None).Rows.Length == 1,
            "later readable normal row is not adopted into closed initial baseline"
        );
        string sibling = root + "-sibling\\nonexistent.ahk";
        var siblingRow = row with { CommandLine = Command(sibling) };
        source.Frame = new([siblingRow], true, []);
        var siblingFixture = new FixtureProcessObservation(
            root,
            [owned],
            interpreter,
            source,
            Open,
            self.Pid
        );
        check(
            siblingFixture.Read(CancellationToken.None).Rows.IsEmpty && !File.Exists(sibling),
            "separator boundary recognizes sibling-prefix path outside root without opening it"
        );
        var scriptForm = row with
        {
            CommandLine =
                ScriptPaths.QuoteArgument(interpreter)
                + " /script "
                + ScriptPaths.QuoteArgument(outside),
        };
        source.Frame = new([scriptForm], true, []);
        var scriptFixture = new FixtureProcessObservation(
            root,
            [owned],
            interpreter,
            source,
            Open,
            self.Pid
        );
        check(
            scriptFixture.Read(CancellationToken.None).Rows.IsEmpty,
            "explicit script form uses lexical foreign-path proof"
        );
        var opaque = row with
        {
            Pid = 93,
            ExecutablePath = null,
            CommandLine = null,
            CreationFileTime = created - 100000,
        };
        source.Frame = new([opaque], true, []);
        var opaqueFixture = new FixtureProcessObservation(
            root,
            [owned],
            interpreter,
            source,
            Open,
            self.Pid
        );
        check(
            opaqueFixture.Read(CancellationToken.None).Rows.IsEmpty,
            "existing exact older-opaque rule remains effective"
        );
        source.Frame = new(
            [
                opaque with
                {
                    CreationFileTime = opaque.CreationFileTime + 1,
                },
                opaque with
                {
                    Pid = 94,
                },
            ],
            true,
            []
        );
        check(
            opaqueFixture.Read(CancellationToken.None).Rows.Length == 2,
            "existing opaque wrong creation and new PID controls remain visible"
        );
        check(
            !File.Exists(outside) && !File.Exists(sibling),
            "foreign sentinels remain absent after all proof paths"
        );
        ScopeChecks(root, check);
        ReadOnlyDiagnosticContractChecks(root, check);
    }

    internal static void ConfirmedRecycleScopeChecks(string root, Action<bool, string> check)
    {
        string owned = Path.Combine(root, "recycled.ahk"),
            other = Path.Combine(root, "retained.ahk");
        File.WriteAllText(owned, "inert" + Environment.NewLine);
        File.WriteAllText(other, "retained" + Environment.NewLine);
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(owned)));
        var source = new MutableScopeRows(new([], true, []));
        string interpreter = Path.GetFullPath("C:/synthetic-approved-interpreter/AutoHotkey64.exe");
        var stamp = new ProcessStamp(51, 1, "C:\\synthetic-harness.exe", "synthetic", 7);
        var security = new ProcessSecurity(false, 0x2000);
        var native = new ScopeNativeState(stamp, stamp, security, security);
        var fixture = new FixtureProcessObservation(
            root,
            [owned, other],
            interpreter,
            source,
            _ => new ScopeNativeHandle(native),
            51
        );
        void Refused(Action action, string label)
        {
            bool refused = false;
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            check(refused, label);
        }
        Refused(
            () => fixture.ConfirmRecycledFile(owned, hash),
            "present file cannot be acknowledged recycled"
        );
        File.Delete(owned);
        Refused(
            () => fixture.Read(CancellationToken.None),
            "unacknowledged missing baseline remains incomplete"
        );
        Refused(
            () => fixture.ConfirmRecycledFile(other + "-outside", hash),
            "unapproved recycle path refused"
        );
        Refused(() => fixture.ConfirmRecycledFile(owned, "wrong"), "wrong baseline hash refused");
        fixture.ConfirmRecycledFile(owned, hash);
        check(
            fixture.Read(CancellationToken.None).Complete,
            "confirmed recycled absence permits final complete empty observation"
        );
        Refused(
            () => fixture.ConfirmRecycledFile(owned, hash),
            "recycle acknowledgment cannot replay"
        );
        source.Frame = new([], false, [new(ReasonCode.ObservationIncomplete, "synthetic")]);
        var incomplete = fixture.Read(CancellationToken.None);
        check(
            !incomplete.Complete && incomplete.Issues.SequenceEqual(source.Frame.Issues),
            "recycle does not mask source incompleteness or issues"
        );
        var row = new ProcessCommandRow(
            52,
            "AutoHotkey64.exe",
            interpreter,
            ScriptPaths.QuoteArgument(interpreter) + " " + ScriptPaths.QuoteArgument(owned),
            1,
            7
        );
        source.Frame = new([row], true, []);
        Refused(
            () => fixture.Read(CancellationToken.None),
            "recycled-path candidate prevents final confirmation"
        );
        source.Frame = new([row with { CommandLine = null }], true, []);
        Refused(
            () => fixture.Read(CancellationToken.None),
            "unproved opaque candidate after recycle remains refused"
        );
        source.Frame = new([], true, []);
        File.AppendAllText(other, "changed");
        Refused(
            () => fixture.Read(CancellationToken.None),
            "remaining baseline file mutation remains refused"
        );
        File.WriteAllText(other, "retained" + Environment.NewLine);
        File.WriteAllText(owned, "inert" + Environment.NewLine);
        Refused(
            () => fixture.Read(CancellationToken.None),
            "recycled path reappearance remains refused"
        );
    }

    readonly record struct ScopeNativeState(
        ProcessStamp Before,
        ProcessStamp After,
        ProcessSecurity SecurityBefore,
        ProcessSecurity SecurityAfter,
        bool Readable = true,
        bool LiveBefore = true,
        bool LiveAfter = true
    );

    sealed class ScopeNativeHandle(ScopeNativeState state) : IFixtureNativeProcess
    {
        int reads,
            securities,
            lives;
        public bool Live => lives++ == 0 ? state.LiveBefore : state.LiveAfter;

        public bool TryRead(out ProcessStamp stamp)
        {
            stamp = reads++ == 0 ? state.Before : state.After;
            return state.Readable;
        }

        public bool TryReadSecurity(out ProcessSecurity security)
        {
            security = securities++ == 0 ? state.SecurityBefore : state.SecurityAfter;
            return state.Readable;
        }

        public void Dispose() { }
    }

    sealed class MutableScopeRows(ObservedProcessCommands frame) : IProcessObservationSource
    {
        public ObservedProcessCommands Frame = frame;

        public ObservedProcessCommands Read(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Frame;
        }
    }

    sealed class ScopeRows(ProcessCommandRow row) : IProcessObservationSource
    {
        public ObservedProcessCommands Read(CancellationToken ct) => new([row], true, []);
    }

    sealed class ScopedObservation(
        IProcessObservationSource source,
        IReadOnlyCollection<string> approved,
        Action<int>? validated = null,
        Action<DiagnosticStage, int>? diagnostic = null
    ) : IProcessObservationSource
    {
        public ProcessIdentity? Opaque { get; set; }

        public ObservedProcessCommands Read(CancellationToken ct)
        {
            diagnostic?.Invoke(DiagnosticStage.SourceReadStarted, 0);
            var rows = source.Read(ct);
            diagnostic?.Invoke(DiagnosticStage.SourceRead, rows.Rows.Length);
            if (!rows.Complete)
            {
                diagnostic?.Invoke(DiagnosticStage.SourceIncomplete, 0);
                throw new InvalidOperationException(
                    "Fixture inventory enumeration is incomplete; no scope approval is available."
                );
            }
            using var self = Process.GetCurrentProcess();
            using var user = WindowsIdentity.GetCurrent();
            foreach (var row in rows.Rows)
            {
                ct.ThrowIfCancellationRequested();
                if (row.SessionId is int session && session != self.SessionId)
                    continue;
                using var native = new NativeProcessIdentity(row.Pid);
                if (!ProcessRelevance.ReadCandidate(row, [Interpreter], native, out string? image))
                    continue;
                diagnostic?.Invoke(DiagnosticStage.Candidate, 0);
                if (!native.TryRead(out var stamp))
                {
                    diagnostic?.Invoke(DiagnosticStage.CandidateIdentityUnavailable, 0);
                    throw new InvalidOperationException(
                        "Fixture inventory has an unclassified new or inaccessible process; abort before script body access."
                    );
                }
                if (stamp.Sid != user.User!.Value || stamp.Session != self.SessionId)
                    continue;
                if (
                    image is not null && !ScriptPaths.Equal(image, stamp.Executable)
                    || row.ExecutablePath is not null
                        && !ScriptPaths.Equal(row.ExecutablePath, stamp.Executable)
                )
                {
                    diagnostic?.Invoke(DiagnosticStage.CandidateImageChanged, 0);
                    throw new InvalidOperationException(
                        "Fixture candidate image changed before scope proof."
                    );
                }
                if (row.CommandLine is string command)
                {
                    try
                    {
                        _ = RequireScopedScript(command, stamp.Executable, approved);
                    }
                    catch (InvalidOperationException)
                    {
                        diagnostic?.Invoke(DiagnosticStage.UnapprovedArgv, 0);
                        throw;
                    }
                }
                else if (
                    Opaque is not { } receipt
                    || receipt.Pid != stamp.Pid
                    || receipt.CreationFileTime != stamp.Created
                    || !native.Matches(receipt)
                    || !approved.Any(path => ScriptPaths.Equal(path, receipt.ScriptPath))
                )
                {
                    diagnostic?.Invoke(DiagnosticStage.UnprovedOpaque, 0);
                    throw new InvalidOperationException(
                        "New opaque AHK lacks the exact owned launch receipt; abort before body access or control."
                    );
                }
            }
            // Mask only after the unmodified independent source has passed the exact scope gate.
            // The deliberately opaque owned row remains present for the actual production assertion.
            validated?.Invoke(rows.Rows.Length);
            diagnostic?.Invoke(DiagnosticStage.ScopeApproved, rows.Rows.Length);
            return rows with
            {
                Rows = rows
                    .Rows.Select(row =>
                        Opaque is { } expected
                        && row.Pid == expected.Pid
                        && row.CreationFileTime
                            == expected.CreationFileTime - expected.CreationFileTime % 10
                            ? row with
                            {
                                ExecutablePath = null,
                                CommandLine = null,
                            }
                            : row
                    )
                    .ToImmutableArray(),
            };
        }
    }

    sealed class FixtureOwner : IEffectOwner
    {
        readonly string root;
        readonly Dictionary<string, string> scripts;
        bool disposed;
        public bool Permit { get; set; } = true;
        public bool Held => !disposed;
        public bool Trusted => Held;
        public string Sid { get; } = WindowsIdentity.GetCurrent().User!.Value;
        public string LoginKey { get; } = "owned-fixture-" + Guid.NewGuid().ToString("N");

        public FixtureOwner(string root, IReadOnlyDictionary<string, string> scripts)
        {
            this.root = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            this.scripts = scripts.ToDictionary(
                p => Path.GetFullPath(p.Key),
                p => p.Value,
                StringComparer.OrdinalIgnoreCase
            );
        }

        public bool Allows(string scriptPath, string interpreterPath)
        {
            try
            {
                NormalToken();
                ValidateInterpreter();
                Directories(root.TrimEnd(Path.DirectorySeparatorChar));
                string path = ScriptPaths.Canonical(scriptPath);
                return Held
                    && Permit
                    && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && scripts.TryGetValue(path, out string? hash)
                    && Hash(path) == hash
                    && ScriptPaths.Equal(
                        ScriptPaths.Canonical(interpreterPath, false),
                        Path.GetFullPath(Interpreter)
                    )
                    && Hash(Interpreter) == InterpreterHash;
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or UnauthorizedAccessException
                            or ArgumentException
                            or Win32Exception
                            or InvalidOperationException
                )
            {
                return false;
            }
        }

        public void Dispose() => disposed = true;
    }

    sealed class NoElevation : IManualElevationDispatcher
    {
        public int Attempts;

        public ElevationDispatch Dispatch(string interpreter, string script)
        {
            Attempts++;
            throw new InvalidOperationException("Owned console fixture must never request UAC.");
        }
    }

    sealed class NoStartup : IUserStartupAdapter
    {
        public int Writes;

        public string? ReadEntry() => null;

        public bool CompareExchangeEntry(string? expected, string? replacement)
        {
            Writes++;
            throw new InvalidOperationException("Owned console fixture must never write startup.");
        }

        public Task<StartupInspection> InspectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new StartupInspection(
                    true,
                    ImmutableArray<string>.Empty,
                    ImmutableArray<Issue>.Empty
                )
            );
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDirectory(string path, nint securityAttributes);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        out int value,
        int size,
        out int returned
    );
}
