using System.Collections.Immutable;
using System.Security.Cryptography;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Tests.Backend.Fixtures;

internal interface IFixtureNativeProcess : IDisposable
{
    bool Live { get; }
    bool TryRead(out ProcessStamp stamp);
    bool TryReadSecurity(out ProcessSecurity security);
}

// Only the test executable composes this source. Production always observes the
// complete global WMI population and keeps its conservative incomplete refusal.
internal sealed class FixtureProcessObservation : IProcessObservationSource
{
    readonly IProcessObservationSource windows;
    readonly string interpreter;
    readonly Func<int, IFixtureNativeProcess> open;
    readonly int harnessPid;
    readonly string root;
    readonly Dictionary<string, string> files;
    readonly HashSet<string> confirmedRecycled = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, long> olderOpaque;
    readonly Dictionary<int, ForeignProof> olderForeign = [];
    readonly NativeProof? harness;
    readonly long freshRootCreated;

    public FixtureProcessObservation(string root, IEnumerable<string> scripts, string interpreter)
        : this(
            root,
            scripts,
            interpreter,
            new WmiProcessObservationSource(),
            pid => new NativeHandle(pid),
            Environment.ProcessId
        ) { }

    internal FixtureProcessObservation(
        string root,
        IEnumerable<string> scripts,
        string interpreter,
        IProcessObservationSource windows,
        Func<int, IFixtureNativeProcess> open,
        int harnessPid
    )
    {
        this.windows = windows;
        this.interpreter = ScriptPaths.LocalPath(interpreter);
        this.open = open;
        this.harnessPid = harnessPid;
        this.root = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        freshRootCreated = Directory.GetCreationTimeUtc(root).ToFileTimeUtc();
        files = scripts.ToDictionary(Path.GetFullPath, Hash, StringComparer.OrdinalIgnoreCase);
        if (
            files.Count == 0
            || files.Keys.Any(p =>
                !p.StartsWith(
                    this.root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
                || File.GetCreationTimeUtc(p).ToFileTimeUtc() < freshRootCreated
            )
        )
            throw new InvalidOperationException("Fresh exclusive fixture root proof failed");
        var initial = windows.Read(CancellationToken.None);
        if (!initial.Complete)
            throw new InvalidOperationException("Initial WMI source is incomplete");
        olderOpaque = initial
            .Rows.Where(row =>
                row.Name.StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase)
                && (row.ExecutablePath is null || row.CommandLine is null)
                && row.CreationFileTime is long created
                && created < freshRootCreated
            )
            .ToDictionary(row => row.Pid, row => row.CreationFileTime!.Value);
        if (TryHarness(out var context))
        {
            harness = context;
            foreach (var row in initial.Rows)
                if (TryForeign(row, context, out var proof))
                    olderForeign.TryAdd(proof.Native.Stamp.Pid, proof);
        }
        File.WriteAllLines(
            Path.Combine(root, "fixture-observation-scope.txt"),
            new[] { "root=" + this.root, "rootCreationFileTime=" + freshRootCreated }
                .Concat(
                    olderOpaque.Select(pair =>
                        "excludedOlderOpaquePid=" + pair.Key + ";creation=" + pair.Value
                    )
                )
                .Concat(files.Select(pair => "ownedFile=" + pair.Key + ";sha256=" + pair.Value))
        );
    }

    public ObservedProcessCommands Read(CancellationToken ct)
    {
        if (
            Directory.GetCreationTimeUtc(root).ToFileTimeUtc() != freshRootCreated
            || files.Any(pair =>
                confirmedRecycled.Contains(pair.Key)
                    ? !DefinitelyAbsent(pair.Key)
                    : !File.Exists(pair.Key) || Hash(pair.Key) != pair.Value
            )
        )
            throw new InvalidOperationException("Fixture scope identity changed");
        var raw = windows.Read(ct);
        var rows = raw.Rows.Where(row =>
            !olderOpaque.TryGetValue(row.Pid, out long created)
            || row.CreationFileTime != created
            || row.ExecutablePath is not null && row.CommandLine is not null
        );
        if (
            raw.Complete
            && harness is NativeProof baseline
            && TryHarness(out var context)
            && SameProof(baseline, context)
        )
            rows = rows.Where(row => !ExcludeForeign(row, context));
        var scoped = rows.ToImmutableArray();
        if (confirmedRecycled.Count != 0)
            foreach (var row in scoped)
            {
                if (
                    !row.Name.StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase)
                    && (
                        row.ExecutablePath is not string image
                        || !ScriptPaths.Equal(image, interpreter)
                    )
                )
                    continue;
                if (row.CommandLine is not string command)
                    throw new InvalidOperationException(
                        "Opaque candidate after confirmed fixture recycle"
                    );
                var argv = ScriptPaths.SplitCommand(command);
                int scriptIndex =
                    argv.Length == 2 ? 1
                    : argv.Length == 3
                    && argv[1].Equals("/script", StringComparison.OrdinalIgnoreCase)
                        ? 2
                    : -1;
                if (
                    scriptIndex < 0
                    || confirmedRecycled.Contains(ScriptPaths.Lexical(argv[scriptIndex]))
                )
                    throw new InvalidOperationException(
                        "Recycled fixture still has a process candidate or unproved argv"
                    );
            }
        return raw with { Rows = scoped };
    }

    // Called only after confirmed native trash, committed catalog removal and exact known exits.
    // Keep the immutable baseline; an unexplained absence or reappearance still fails closed.
    internal void ConfirmRecycledFile(string path, string expectedHash)
    {
        path = Path.GetFullPath(path);
        if (
            !files.TryGetValue(path, out var original)
            || original != expectedHash
            || !DefinitelyAbsent(path)
            || confirmedRecycled.Contains(path)
        )
            throw new InvalidOperationException("Unproved fixture recycle acknowledgment");
        confirmedRecycled.Add(path);
    }

    static bool DefinitelyAbsent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    readonly record struct NativeProof(ProcessStamp Stamp, ProcessSecurity Security);

    readonly record struct ForeignProof(NativeProof Native, string ScriptPath);

    static bool SameStamp(ProcessStamp left, ProcessStamp right) =>
        left.Pid == right.Pid
        && left.Created == right.Created
        && ScriptPaths.Equal(left.Executable, right.Executable)
        && left.Sid == right.Sid
        && left.Session == right.Session;

    static bool SameProof(NativeProof left, NativeProof right) =>
        SameStamp(left.Stamp, right.Stamp) && left.Security == right.Security;

    static bool ReadProof(IFixtureNativeProcess native, out NativeProof proof)
    {
        proof = default;
        if (
            !native.Live
            || !native.TryRead(out var stamp)
            || stamp.Pid <= 0
            || stamp.Created <= 0
            || string.IsNullOrWhiteSpace(stamp.Executable)
            || string.IsNullOrWhiteSpace(stamp.Sid)
            || stamp.Session < 0
            || !native.TryReadSecurity(out var security)
        )
            return false;
        proof = new(stamp, security);
        return true;
    }

    bool TryHarness(out NativeProof proof)
    {
        proof = default;
        try
        {
            using var native = open(harnessPid);
            if (
                !ReadProof(native, out var before)
                || before.Stamp.Pid != harnessPid
                || before.Security.Elevated
                || before.Security.IntegrityRid == 0
                || !ReadProof(native, out var after)
                || !SameProof(before, after)
                || !native.Live
            )
                return false;
            proof = before;
            return true;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception
            )
        {
            return false;
        }
    }

    bool TryForeign(ProcessCommandRow row, NativeProof context, out ForeignProof proof)
    {
        proof = default;
        if (
            row.ExecutablePath is not string image
            || row.CommandLine is not string command
            || row.SessionId is not int session
            || row.CreationFileTime is not long created
        )
            return false;
        try
        {
            using var native = open(row.Pid);
            if (!ReadProof(native, out var before))
                return false;
            var stamp = before.Stamp;
            if (
                stamp.Pid != row.Pid
                || stamp.Created >= freshRootCreated
                || stamp.Sid != context.Stamp.Sid
                || stamp.Session != context.Stamp.Session
                || session != stamp.Session
                || created != stamp.Created - stamp.Created % 10
                || !ScriptPaths.Equal(stamp.Executable, interpreter)
                || !ScriptPaths.Equal(image, stamp.Executable)
                || before.Security.Elevated
                || before.Security != context.Security
            )
                return false;
            var argv = ScriptPaths.SplitCommand(command);
            int scriptIndex =
                argv.Length == 2 ? 1
                : argv.Length == 3 && argv[1].Equals("/script", StringComparison.OrdinalIgnoreCase)
                    ? 2
                : -1;
            if (scriptIndex < 0 || !ScriptPaths.Equal(argv[0], stamp.Executable))
                return false;
            string scriptPath = ScriptPaths.Lexical(argv[scriptIndex]);
            if (
                ScriptPaths.Equal(scriptPath, root)
                || scriptPath.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return false;
            if (!ReadProof(native, out var after) || !SameProof(before, after) || !native.Live)
                return false;
            proof = new(before, scriptPath);
            return true;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception
            )
        {
            return false;
        }
    }

    bool ExcludeForeign(ProcessCommandRow row, NativeProof context) =>
        olderForeign.TryGetValue(row.Pid, out var baseline)
        && TryForeign(row, context, out var current)
        && SameProof(baseline.Native, current.Native)
        && ScriptPaths.Equal(baseline.ScriptPath, current.ScriptPath);

    static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    sealed class NativeHandle(int pid) : IFixtureNativeProcess
    {
        readonly NativeProcessIdentity native = new(pid);
        public bool Live => native.Live;

        public bool TryRead(out ProcessStamp stamp) => native.TryRead(out stamp);

        public bool TryReadSecurity(out ProcessSecurity security) =>
            native.TryReadSecurity(out security);

        public void Dispose() => native.Dispose();
    }
}
