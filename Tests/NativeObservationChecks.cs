using Bezoro.AhkManager;
using System.Runtime.InteropServices;

static class NativeObservationChecks
{
    public static async Task StopBoundary(string root, Action<bool, string> check)
    {
        using var requester = System.Security.Principal.WindowsIdentity.GetCurrent();
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        const string exe = @"C:\Fixture\AutoHotkey64.exe";
        string script = Path.Combine(root, "stop fixture.ahk");
        var requested = new ProcessIdentity(42, 100, exe, script);
        var stamp = new NativeStamp(42, 100, exe, requester.User!.Value, current.SessionId);
        string command = $"\"{exe}\" \"{script}\"";
        var matchingSnapshot = new Snapshot([requested], true, "separate fixture snapshot");
        foreach (string scenario in new[] { "exact", "creation", "script", "image", "pid", "owner", "session", "unknown", "expired", "invalid", "changed", "cancel-query", "cancel-before", "terminate-refused", "wait-unconfirmed" })
        {
            var held = new StopFixture(stamp, command);
            using var cancellation = new CancellationTokenSource();
            switch (scenario)
            {
                case "creation": held.Stamp = stamp with { Created = 101 }; break;
                case "script": held.CommandText = $"\"{exe}\" \"{script}.ahk\""; break;
                case "image": held.Stamp = stamp with { Executable = @"C:\Other\AutoHotkey64.exe" }; break;
                case "pid": held.Stamp = stamp with { Pid = 43 }; break;
                case "owner": held.Stamp = stamp with { Sid = "foreign-sid" }; break;
                case "session": held.Stamp = stamp with { Session = stamp.Session + 1 }; break;
                case "unknown": held.CommandText = null; break;
                case "expired": held.Alive = false; break;
                case "invalid": held.InvalidHandle = true; break;
                case "changed": held.AfterQuery = stamp with { Created = 101 }; break;
                case "cancel-query": held.OnCommand = cancellation.Cancel; break;
                case "cancel-before": cancellation.Cancel(); break;
                case "terminate-refused": held.TerminateResult = false; break;
                case "wait-unconfirmed": held.WaitResult = 258; break;
            }
            int opens = 0, snapshots = 0;
            var adapter = new WindowsProcesses(pid => { opens++; check(pid == requested.Pid, "stop opens only requested PID " + scenario); return held; }, token => { snapshots++; return Task.FromResult(matchingSnapshot); });
            check((await adapter.Snapshot(CancellationToken.None)).Processes.Single() == requested, "separate snapshot matches requested identity " + scenario);
            snapshots = 0;
            Result? outcome = null; bool cancelled = false;
            try { outcome = await adapter.Stop(requested, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            bool effectAttempted = scenario is "exact" or "terminate-refused" or "wait-unconfirmed";
            check(held.Terminations == (effectAttempted ? 1 : 0), "actual Stop held identity effect count " + scenario);
            check(snapshots == 0, "actual Stop does not consult matching separate snapshot " + scenario);
            check(opens == (scenario == "cancel-before" ? 0 : 1), "actual Stop owns one held handle " + scenario);
            check(held.Waits == (scenario is "exact" or "wait-unconfirmed" ? 1 : 0), "actual Stop waits only after accepted termination " + scenario);
            check(held.Disposed == (scenario != "cancel-before"), "actual Stop releases held handle " + scenario);
            check(cancelled == scenario.StartsWith("cancel-", StringComparison.Ordinal) && (cancelled || outcome!.Success == (scenario == "exact")), "actual Stop reports confirmed result " + scenario);
            if (effectAttempted)
            {
                string[] expected = scenario == "terminate-refused" ? ["live", "read", "command", "read", "live", "read", "live", "terminate", "dispose"] : ["live", "read", "command", "read", "live", "read", "live", "terminate", "wait", "dispose"];
                check(held.Events.SequenceEqual(expected), "actual Stop same handle query revalidation effect wait ordering " + scenario);
            }
        }
    }
    public static void Run(string root, Action<bool, string> check)
    {
        const string exe = @"C:\Fixture\AutoHotkey64.exe";
        string script = Path.Combine(root, "%USERNAME%", "literal script.ahk");
        string command = $"\"{exe}\" \"{script}\"";
        check(Paths.Equal(Native.Script(command, exe), script), "native argv0 image and literal script accepted");
        check(Paths.Equal(Native.Script($"\"{exe}\" /script \"{script}\"", exe), script), "native known interpreter switch accepted");
        foreach (string bad in new[] { $"\"C:\\Other\\AutoHotkey64.exe\" \"{script}\"", $"\"{script}\"", $"\"{exe}\" relative.ahk", $"\"{exe}\" /unknown \"{script}\"", $"\"{exe}\" \"{script}", " " + command, command + "\0", $"\"{exe}\" \"C:\\wrong.txt\"", $"\"{exe}\" \"\\\\server\\share\\script.ahk\"", new string('x', 32769) })
            check(Native.Script(bad, exe) is null, "native malformed foreign or unsupported command refused");
        check(Native.Script(command, @"C:\Fixture\Other.exe") is null, "native non AHK image cannot assign script");

        foreach (int width in new[] { 4, 8 })
        {
            int header = width == 8 ? 16 : 8;
            check(Native.CommandRange(8, 10, header, header + 10, 65536, width), "native Unicode range valid for pointer width " + width);
            check(!Native.CommandRange(0, 0, header, header, 65536, width), "native empty string refused");
            check(!Native.CommandRange(7, 10, header, header + 10, 65536, width), "native odd length refused");
            check(!Native.CommandRange(8, 9, header, header + 10, 65536, width), "native odd maximum refused");
            check(!Native.CommandRange(8, 6, header, header + 10, 65536, width), "native maximum below length refused");
            check(!Native.CommandRange(8, 10, header - 2, header + 10, 65536, width), "native pointer inside header refused");
            check(!Native.CommandRange(8, 10, -2, header + 10, 65536, width), "native pointer below allocation refused");
            check(!Native.CommandRange(8, 10, header + 1, header + 12, 65536, width), "native unaligned pointer refused");
            check(!Native.CommandRange(8, 10, header, header + 9, 65536, width), "native truncated return refused");
            check(!Native.CommandRange(8, 10, long.MaxValue, header + 10, 65536, width), "native overflowing pointer range refused");
            check(!Native.CommandRange(8, 10, header, 65537, 65536, width), "native oversized return refused");
            check(!Native.CommandRange(8, 10, header, header + 10, 65537, width), "native oversized allocation refused");
        }
        check(!Native.CommandRange(8, 10, 16, 26, 65536, 2), "native unsupported pointer width refused");
        BufferChecks(command, check);

        var stamp = new NativeStamp(42, 100, exe, "fixture-sid", 3);
        var good = new FixtureObservation(stamp, command);
        check(Native.Observe(good, 42, stamp.Sid, stamp.Session, out var observed) && Native.Matches(new(42, 100, exe, script), observed), "same held object positive identity");
        check(good.Events.SequenceEqual(new[] { "live", "read", "command", "read", "live", "read", "live" }), "same handle before after and final liveness checks");
        foreach (var changed in new[] { stamp with { Pid = 43 }, stamp with { Created = 101 }, stamp with { Executable = @"C:\Other\AutoHotkey64.exe" }, stamp with { Sid = "foreign" }, stamp with { Session = 4 } })
        {
            var unstable = new FixtureObservation(stamp, command) { After = changed };
            check(!Native.Observe(unstable, 42, stamp.Sid, stamp.Session, out _), "same handle identity changes during command refused");
        }
        check(!Native.Observe(new FixtureObservation(stamp with { Sid = "foreign" }, command), 42, stamp.Sid, 3, out _), "foreign owner unknown");
        check(!Native.Observe(new FixtureObservation(stamp with { Session = 4 }, command), 42, stamp.Sid, 3, out _), "foreign session unknown");
        check(!Native.Observe(new FixtureObservation(stamp, null), 42, stamp.Sid, 3, out _), "unsupported denied or unreadable command unknown");
        check(!Native.Observe(new FixtureObservation(stamp, command) { RefuseRead = true }, 42, stamp.Sid, 3, out _), "unreadable identity unknown");
        check(!Native.Observe(new FixtureObservation(stamp, command) { DeadAfterCommand = true }, 42, stamp.Sid, 3, out _), "exited during command unknown");
        check(!Native.Observe(new FixtureObservation(stamp, command) { Final = stamp with { Created = 101 } }, 42, stamp.Sid, 3, out _), "identity changed during script parse unknown");
        foreach (var requested in new[] { new ProcessIdentity(43, 100, exe, script), new(42, 101, exe, script), new(42, 100, @"C:\Other\AutoHotkey64.exe", script), new(42, 100, exe, script + ".ahk"), new(42, 100, exe, null) })
            check(!Native.Matches(requested, observed), "requested full identity mismatch never authorizes effect");
        var unknown = new Snapshot([new(42, 0, "", null)], false, "fixture");
        check(!Policy.Check(new() { Path = script, Interpreter = exe }, unknown, ScriptAction.Start, true).Success, "new native unknown still blocks duplicate start");

        // Only the current test executable is opened. No AHK process is started or stopped.
        using var self = Native.OpenProcess(Native.QueryAccess, false, Environment.ProcessId);
        check(!self.IsInvalid && Native.Identity(self, out var created, out var image) && created > 0 && !string.IsNullOrEmpty(image), "native read only self identity");
        check(Native.WaitForSingleObject(self, 0) == 258, "native held self handle is live");
        check(Native.Command(self) is { Length: > 0 }, "native bounded same handle class60 self command query");
        check(!Native.Observe(self, Environment.ProcessId, out _), "native self non AHK command cannot become script identity");
    }
    static void BufferChecks(string command, Action<bool, string> check)
    {
        int header = IntPtr.Size == 8 ? 16 : 8;
        IntPtr buffer = Marshal.AllocHGlobal(65536);
        try
        {
            int bytes = command.Length * 2;
            Marshal.WriteInt16(buffer, (short)bytes); Marshal.WriteInt16(buffer, 2, (short)(bytes + 2));
            Marshal.WriteIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4, IntPtr.Add(buffer, header));
            Marshal.Copy((command + '\0').ToCharArray(), 0, IntPtr.Add(buffer, header), command.Length + 1);
            check(Native.DecodeCommand(buffer, header + bytes + 2, 65536) == command, "native bounded Unicode descriptor decoded");
            Marshal.WriteInt16(buffer, header + 2, 0);
            check(Native.DecodeCommand(buffer, header + bytes + 2, 65536) is null, "native embedded null refused");
            Marshal.WriteIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4, IntPtr.Add(buffer, 65534));
            check(Native.DecodeCommand(buffer, header + bytes + 2, 65536) is null, "native pointer outside returned buffer refused");
            check(Native.DecodeCommand(buffer, header - 1, 65536) is null, "native truncated descriptor refused before reading");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    sealed class FixtureObservation(NativeStamp initial, string? command) : IHeldObservation
    {
        public List<string> Events { get; } = [];
        public NativeStamp? After, Final;
        public bool RefuseRead, DeadAfterCommand;
        int reads; bool queried;
        public bool Live { get { Events.Add("live"); return !queried || !DeadAfterCommand; } }
        public bool Read(out NativeStamp stamp) { Events.Add("read"); stamp = reads++ == 0 ? initial : reads == 2 ? After ?? initial : Final ?? After ?? initial; return !RefuseRead; }
        public string? Command() { Events.Add("command"); queried = true; return command; }
    }
    sealed class StopFixture(NativeStamp stamp, string? command) : IStopHandle
    {
        public NativeStamp Stamp = stamp;
        public NativeStamp? AfterQuery;
        public string? CommandText = command;
        public Action? OnCommand;
        public bool Alive = true, InvalidHandle, Disposed, TerminateResult = true;
        public uint WaitResult;
        public int Terminations, Waits;
        public List<string> Events { get; } = [];
        bool queried;
        public bool Invalid => InvalidHandle;
        public bool Live { get { Events.Add("live"); return Alive; } }
        public bool Read(out NativeStamp value) { Events.Add("read"); value = queried ? AfterQuery ?? Stamp : Stamp; return true; }
        public string? Command() { Events.Add("command"); queried = true; OnCommand?.Invoke(); return CommandText; }
        public bool Terminate() { Events.Add("terminate"); Terminations++; return TerminateResult; }
        public uint Wait(uint milliseconds) { if (milliseconds != 5000) throw new Exception("Unexpected wait bound"); Events.Add("wait"); Waits++; return WaitResult; }
        public void Dispose() { Events.Add("dispose"); Disposed = true; }
    }
}
