using Bezoro.AhkManager;

static class NativePrivilegeChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        check(Native.DecodeElevation(true, 4, 0) == false, "target token zero elevation is normal");
        check(Native.DecodeElevation(true, 4, 1) == true, "target token nonzero elevation is administrator");
        check(Native.DecodeElevation(true, 4, -1) == true, "target token DWORD high bits remain nonzero");
        check(Native.DecodeElevation(false, 4, 0) is null && Native.DecodeElevation(false, 4, 1) is null, "refused or unsupported token elevation is unknown");
        foreach (int size in new[] { -1, 0, 3, 5, int.MaxValue })
            check(Native.DecodeElevation(true, size, 0) is null && Native.DecodeElevation(true, size, 1) is null, "token elevation malformed returned size is unknown " + size);

        const string exe = @"C:\Fixture\AutoHotkey64.exe";
        string script = Path.Combine(root, "privilege fixture.ahk");
        string command = $"\"{exe}\" \"{script}\"";
        var core = new NativeStamp(42, 100, exe, "fixture-owner", 3);
        foreach (bool? before in new bool?[] { false, true, null })
        foreach (bool? after in new bool?[] { false, true, null })
        foreach (bool? final in new bool?[] { false, true, null })
        {
            bool? expected = before.HasValue && before == after && before == final ? before : null;
            var held = new PrivilegeObservation(core with { Elevated = before }, core with { Elevated = after }, core with { Elevated = final }, command);
            check(Native.Observe(held, core.Pid, core.Sid, core.Session, out var observed) && observed!.Elevated == expected && Native.Matches(new(42, 100, exe, script, !expected), observed), "runtime elevation stable or unknown preserves core held proof " + before + "/" + after + "/" + final);
            check(held.Commands == 1 && held.Reads == 3, "elevation comes from same before after final observation");
            var snapshot = new Snapshot([observed!], true, "fixture");
            check(!snapshot.Unknown, "unknown runtime elevation does not make sound snapshot incomplete");
        }
        foreach (var changed in new[] { core with { Pid = 43 }, core with { Created = 101 }, core with { Executable = @"C:\Other\AutoHotkey64.exe" }, core with { Sid = "foreign" }, core with { Session = 4 } })
            check(!Native.Observe(new PrivilegeObservation(core with { Elevated = false }, changed with { Elevated = false }, changed with { Elevated = false }, command), core.Pid, core.Sid, core.Session, out _), "known normal elevation cannot hide core identity change");
        check(Native.SameStamp(core with { Elevated = true }, core with { Elevated = null }), "stamp core comparison excludes nullable runtime elevation");
        check(new ProcessIdentity(42, 100, exe, script).Elevated is null, "older four field process fixtures preserve unknown elevation");
    }
    sealed class PrivilegeObservation(NativeStamp before, NativeStamp after, NativeStamp final, string command) : IHeldObservation
    {
        public int Reads, Commands;
        public bool Live => true;
        public bool Read(out NativeStamp stamp) { stamp = Reads++ == 0 ? before : Reads == 2 ? after : final; return true; }
        public string? Command() { Commands++; return command; }
    }
}
