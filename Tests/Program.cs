using Bezoro.AhkManager;
using System.Diagnostics;
using System.Text.Json;

static class Tests
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
    static bool Throws(Action action) { try { action(); return false; } catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException) { return true; } }
    [STAThread]
    static int Main(string[] args)
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BezoroAhkFixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--native-privilege")) NativePrivilegeChecks.Run(root, Check);
            else if (args.Contains("--native-stop-boundary")) NativeObservationChecks.StopBoundary(root, Check).GetAwaiter().GetResult();
            else if (args.Contains("--native-query")) NativeObservationChecks.Run(root, Check);
            else if (args.Contains("--native")) NativeIntegration(root).GetAwaiter().GetResult();
            else Run(root).GetAwaiter().GetResult();
            Console.WriteLine($"RESULT PASS {passed} checks. Isolated fixtures only."); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
        // Fixture files remain in Temp for inspection; no broad recursive cleanup.
    }
    static async Task Run(string root)
    {
        string script = System.IO.Path.Combine(root, "á script espaço.ahk"); File.WriteAllText(script, "#Requires AutoHotkey v2.0\n; harmless fixture\n");
        var entry = new ScriptEntry { Path = Paths.Normalize(script), Interpreter = @"C:\Fixture\AutoHotkey64.exe", Description = "Private fixture description" };
        Check(Paths.Equal(Paths.Normalize(script.ToUpperInvariant()), entry.Path), "paths case and Unicode");
        Check(Throws(() => Paths.Normalize("relative.ahk")), "relative refused"); Check(Throws(() => Paths.Normalize(@"\\server\share\file.ahk")), "UNC refused"); Check(Throws(() => Paths.Normalize(@"C:\file:alternate.ahk")), "alternate stream refused"); Check(Throws(() => Paths.Normalize(@"C:\*.ahk")), "wildcard refused");
        Check(Paths.Matches(entry, "privATE") && Paths.Matches(entry, "espaço"), "search description and filename");
        var quote = Commands.Launch(entry.Interpreter, entry.Path); Check(!quote.UseShellExecute && quote.ArgumentList.Count == 1 && quote.ArgumentList[0] == script && quote.WorkingDirectory == root, "ArgumentList preserves spaces Unicode and working directory");
        Check(Paths.Equal(Commands.Script($"\"{entry.Interpreter}\" \"{script}\""), script), "quoted interpreter command"); Check(Paths.Equal(Commands.Script($"\"{script}\""), script), "direct AHK source"); Check(Commands.Script("cmd.exe /c example.ahk") is null, "wrapper opaque"); Check(Commands.Script("AutoHotkey.exe relative.ahk") is null, "relative interpreter opaque"); Check(Commands.Script($"\"{entry.Interpreter}\" /unknown \"{script}\"") is null, "unknown switch opaque");
        string literalDir = System.IO.Path.Combine(root, "%USERNAME%"); string expandedDir = System.IO.Path.Combine(root, Environment.GetEnvironmentVariable("USERNAME") ?? "fixture-user"); Directory.CreateDirectory(literalDir); Directory.CreateDirectory(expandedDir); string literalScript = System.IO.Path.Combine(literalDir, "a.ahk"), expandedScript = System.IO.Path.Combine(expandedDir, "a.ahk"); File.WriteAllText(literalScript, "#Requires AutoHotkey v2.0\n"); File.WriteAllText(expandedScript, "#Requires AutoHotkey v2.0\n");
        var parsedLiteral = Commands.Script($"\"{entry.Interpreter}\" \"{literalScript}\""); Check(Paths.Equal(parsedLiteral, literalScript) && !Paths.Equal(parsedLiteral, expandedScript), "process percent path literal despite expanded sibling existing");
        var literalSnapshot = new Snapshot([new(12, 100, entry.Interpreter, parsedLiteral)], true, "fixture"); Check(!Policy.Check(new() { Path = literalScript, Interpreter = entry.Interpreter }, literalSnapshot, ScriptAction.Start, true).Success, "literal process duplicate refused"); Check(!Policy.Check(new() { Path = expandedScript, Interpreter = entry.Interpreter }, literalSnapshot, ScriptAction.Stop, true).Success, "expanded sibling never authorizes wrong stop"); Check(Commands.StartupScript($"\"{entry.Interpreter}\" \"{literalScript}\"") is null, "startup percent variables remain unresolved");
        var scan = System.IO.Path.Combine(root, "scan"); Directory.CreateDirectory(System.IO.Path.Combine(scan, "backups")); Directory.CreateDirectory(System.IO.Path.Combine(scan, "scripts")); File.WriteAllText(System.IO.Path.Combine(scan, "one.ahk"), ""); File.WriteAllText(System.IO.Path.Combine(scan, "scripts", "two.ahk"), ""); File.WriteAllText(System.IO.Path.Combine(scan, "backups", "old.ahk"), ""); var limits = new List<string>(); Check(Discovery.Scan(scan, limits).Count == 2, "discovery excludes backups");
        string lockedScan = System.IO.Path.Combine(scan, "locked.ahk"); File.WriteAllText(lockedScan, "fixture"); using (var locked = new FileStream(lockedScan, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { var partialLimits = new List<string>(); var partial = Discovery.Scan(scan, partialLimits); Check(partial.Count == 2 && partial.Any(p => p.EndsWith("one.ahk")) && partial.Any(p => p.EndsWith("two.ahk")) && partialLimits.Count > 0, "locked discovery file preserves siblings and child traversal"); }
        string fixtureProfile = System.IO.Path.Combine(root, "profile"); string profileAhk = System.IO.Path.Combine(fixtureProfile, "Documents", "AutoHotkey"); Directory.CreateDirectory(profileAhk); Check(Paths.Equal(Discovery.ConventionalRoot(System.IO.Path.Combine(root, "absent-special-documents"), fixtureProfile), profileAhk) && Paths.Equal(Discovery.ConventionalRoot("", fixtureProfile), profileAhk), "missing special Documents uses existing conventional profile root");
        string missingDocs = System.IO.Path.Combine(root, "both-missing-documents"); Check(Paths.Equal(Discovery.ConventionalRoot(missingDocs, System.IO.Path.Combine(root, "both-missing-profile")), System.IO.Path.Combine(missingDocs, "AutoHotkey")), "both roots missing preserves conventional Documents scope");
        var plain = new Snapshot([], true, "fixture"); var identity = new ProcessIdentity(42, 100, entry.Interpreter, entry.Path); var active = new Snapshot([identity], true, "fixture"); var unknown = new Snapshot([new(99, 0, "", null)], false, "fixture");
        Check(Policy.Check(entry, plain, ScriptAction.Start, true).Success, "stopped start allowed"); Check(!Policy.Check(entry, active, ScriptAction.Start, true).Success, "duplicate start refused"); Check(!Policy.Check(entry, unknown, ScriptAction.Start, true).Success, "inaccessible AHK blocks start"); Check(!Policy.Check(entry, unknown, ScriptAction.Recycle, true).Success, "inaccessible AHK blocks recycle"); Check(!Policy.Check(entry, active, ScriptAction.Recycle, true).Success, "active recycle refused"); Check(!Policy.Check(entry, plain, ScriptAction.Start, false).Success, "missing start refused"); Check(!Policy.Check(entry, new([identity, identity with { Pid = 43 }], true, ""), ScriptAction.Stop, true).Success, "multiple instances refused");
        entry.LaunchBlocked = true; Check(!Policy.Check(entry, active, ScriptAction.Restart, true).Success && Policy.Check(entry, active, ScriptAction.Stop, true).Success, "local launch block preserves stop"); entry.LaunchBlocked = false;
        string supervisor = System.IO.Path.Combine(root, "AutoHotkey", "startup.ahk"); string child = System.IO.Path.Combine(root, "AutoHotkey", "scripts", "startup", "child.ahk"); var childEntry = new ScriptEntry { Path = child, Interpreter = entry.Interpreter }; var supervisorEntry = new ScriptEntry { Path = supervisor, Interpreter = entry.Interpreter };
        var supervised = new Snapshot([identity with { Script = child }, identity with { Pid = 50, Script = supervisor }], true, "", supervisor);
        Check(!Policy.Check(childEntry, supervised, ScriptAction.Restart, true).Success, "supervisor blocks child restart"); Check(Policy.Check(childEntry, supervised, ScriptAction.Stop, true).Success, "supervised child explicit stop allowed"); Check(!Policy.Check(supervisorEntry, supervised, ScriptAction.Start, true).Success, "children block supervisor launch"); Check(!Policy.Check(supervisorEntry, supervised, ScriptAction.Restart, true).Success, "children block supervisor restart"); Check(Policy.Check(new() { Path = System.IO.Path.Combine(root, "startup.ahk"), Interpreter = entry.Interpreter }, new([identity with { Script = child }], true, "", supervisor), ScriptAction.Start, true).Success, "unrelated startup filename not supervisor");
        var waitingSupervisor = new Snapshot([identity with { Script = supervisor }], true, "fixture", supervisor); Check(!Policy.Check(childEntry, waitingSupervisor, ScriptAction.Start, true).Success, "supervisor blocks manual child start during relaunch interval");
        var data = System.IO.Path.Combine(root, "data"); var original = new Catalog { Authority = new() { LedgerId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Version = 2, Sid = "fixture-core", Fixture = true }, Scripts = [entry.Copy()] };
        using (var store = new CatalogStore(data))
        {
            Check(store.Load().Scripts.Count == 0, "new catalog"); store.Save(original); var loaded = store.Load(); Check(loaded.Scripts.Single().Description == entry.Description, "description persists");
            loaded.Scripts[0].DescriptionSource = "Informada"; loaded.Scripts[0].LaunchBlocked = true; store.Save(loaded); Check(File.Exists(store.FilePath + ".bak") && store.Load().Scripts[0].LaunchBlocked && store.Load().Scripts[0].DescriptionSource == "Informada", "backup metadata and source persist");
            using (var locked = new FileStream(script, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { var accessibleCatalog = store.Load(); accessibleCatalog.Scripts[0].Description = "Metadata remains editable"; store.Save(accessibleCatalog); Check(!store.ReadOnly && store.Load().Scripts[0].Description == "Metadata remains editable", "locked script never corrupts catalog or blocks metadata save"); }
            Check(Throws(() => { using var second = new CatalogStore(data); }), "second writer refused");
            var duplicate = new Catalog { Authority = new() { LedgerId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Version = 2, Sid = "fixture-core", Fixture = true }, Scripts = [entry.Copy(), entry.Copy()] }; Check(Throws(() => store.Save(duplicate)), "duplicate config identity refused"); Check(store.Load().Scripts.Count == 1, "invalid save preserves previous catalog");
            File.SetAttributes(store.FilePath, FileAttributes.ReadOnly); try { Check(Throws(() => store.Save(original)), "readonly save fails safely"); } finally { File.SetAttributes(store.FilePath, FileAttributes.Normal); } Check(store.Load().Scripts[0].LaunchBlocked, "failed write preserves original");
            store.Log("Start", false); Check(!File.ReadAllText(System.IO.Path.Combine(data, "operations.log")).Contains(entry.Description) && !File.ReadAllText(System.IO.Path.Combine(data, "operations.log")).Contains(entry.Path), "log omits private content and path");
        }
        File.WriteAllText(System.IO.Path.Combine(data, "catalog.json"), "{truncated");
        using (var recovered = new CatalogStore(data)) { Check(recovered.Load().Scripts.Count == 1 && recovered.ReadOnly, "corrupt primary reads backup readonly"); Check(Throws(() => recovered.Save(original)), "corrupt original never overwritten"); }
        File.WriteAllText(System.IO.Path.Combine(data, "catalog.json.bak"), "{\"Schema\":99}"); using (var corrupt = new CatalogStore(data)) Check(corrupt.Load().Scripts.Count == 0 && corrupt.ReadOnly, "total corruption protected");
        var fake = new FakeProcesses(plain); var recycler = new FakeRecycler(); var controller = new ActionController(fake, recycler);
        var lockedActionFake = new FakeProcesses(plain); using (var locked = new FileStream(script, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(!(await new ActionController(lockedActionFake, recycler).Execute(entry, ScriptAction.Start, true)).Success && lockedActionFake.Starts == 0, "action boundary refuses inaccessible physical script before effects");
        foreach (var reason in new[] { "missing interpreter", "incompatible version", "unsupported requires" })
        {
            var preflightFake = new FakeProcesses(active) { PreflightFailure = reason }; var restartPreflight = new ActionController(preflightFake, recycler); var refused = await restartPreflight.Execute(entry, ScriptAction.Restart, true); Check(!refused.Success && preflightFake.Stops == 0 && preflightFake.Starts == 0 && preflightFake.Current.For(entry.Path).Count == 1, "restart preflight preserves healthy process: " + reason);
        }
        var postPreflight = new FakeProcesses(active); postPreflight.OnStopped = () => postPreflight.PreflightFailure = "interpreter removed after stop"; Check(!(await new ActionController(postPreflight, recycler).Execute(entry, ScriptAction.Restart, true)).Success && postPreflight.Stops == 1 && postPreflight.Starts == 0, "restart preflight revalidated after stop");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(child)!); File.WriteAllText(child, "#Requires AutoHotkey v2.0\n"); var childFake = new FakeProcesses(waitingSupervisor); Check(!(await new ActionController(childFake, recycler).Execute(childEntry, ScriptAction.Start, true)).Success && childFake.Starts == 0, "controller never manually launches supervised child");
        var installed = Discovery.DefaultInterpreter(); if (File.Exists(installed))
        {
            var encodedEntry = entry.Copy(); encodedEntry.Interpreter = installed; string bomPath = System.IO.Path.Combine(root, "utf8bom.ahk"); File.WriteAllText(bomPath, "#Requires AutoHotkey v2.0\n", new System.Text.UTF8Encoding(true)); encodedEntry.Path = bomPath; Check(Discovery.Compatible(encodedEntry).Success, "UTF8 BOM requires recognized");
            string utf16Path = System.IO.Path.Combine(root, "utf16bom.ahk"); File.WriteAllText(utf16Path, "#Requires AutoHotkey v2.0\n", System.Text.Encoding.Unicode); encodedEntry.Path = utf16Path; Check(Discovery.Compatible(encodedEntry).Success, "UTF16 BOM requires recognized");
            string encodingDir = System.IO.Path.Combine(root, "utf32-case"); Directory.CreateDirectory(encodingDir); string utf32Path = System.IO.Path.Combine(encodingDir, "utf32.ahk"), neighborPath = System.IO.Path.Combine(encodingDir, "neighbor.ahk"); File.WriteAllText(utf32Path, "#Requires AutoHotkey v2.0\n", new System.Text.UTF32Encoding(false, true)); File.WriteAllText(neighborPath, "#Requires AutoHotkey v2.0\n");
            Check(Discovery.Infer(utf32Path).Contains("UTF-32", StringComparison.Ordinal), "UTF32 inference returns accessible description without throwing");
            var encodingLimits = new List<string>(); var encodingPaths = Discovery.Scan(encodingDir, encodingLimits); Check(encodingPaths.Count == 2 && encodingPaths.Any(p => Paths.Equal(p, neighborPath)), "UTF32 discovery preserves normal sibling");
            var encodingCatalog = new Catalog { Authority = new() { LedgerId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Version = 2, Sid = "fixture-core", Fixture = true }, Scripts = encodingPaths.Select(p => new ScriptEntry { Path = Paths.Normalize(p), Description = Discovery.Infer(p), Interpreter = installed }).ToList() }; using (var encodingStore = new CatalogStore(System.IO.Path.Combine(root, "utf32-metadata"))) { encodingStore.Save(encodingCatalog); var preserved = encodingStore.Load(); Check(preserved.Scripts.Count == 2 && preserved.Scripts.Any(e => Paths.Equal(e.Path, utf32Path) && e.Description.Contains("UTF-32")) && preserved.Scripts.Any(e => Paths.Equal(e.Path, neighborPath) && !string.IsNullOrEmpty(e.Description)), "UTF32 registration metadata and normal sibling survive persistence"); }
            encodedEntry.Path = utf32Path; var unsupported = Discovery.Compatible(encodedEntry); Check(!unsupported.Success && unsupported.Message.Contains("UTF-32", StringComparison.Ordinal), "UTF32 execution remains explicitly refused");
        }
        Check((await controller.Execute(entry, ScriptAction.Start, true)).Success, "controller starts once"); Check(!(await controller.Execute(entry, ScriptAction.Start, true)).Success && fake.Starts == 1, "repeated start refuses duplicate");
        Check(!(await controller.Execute(entry, ScriptAction.Stop, false)).Success && fake.Stops == 0, "cancel stop no effect");
        fake.StopFailure = true; Check(!(await controller.Execute(entry, ScriptAction.Restart, true)).Success && fake.Starts == 1, "restart stop failure never starts"); fake.StopFailure = false;
        fake.StartFailure = true; Check(!(await controller.Execute(entry, ScriptAction.Restart, true)).Success && fake.Current.For(entry.Path).Count == 0, "restart partial failure state observed"); fake.StartFailure = false;
        fake.Current = active; using var afterStop = new CancellationTokenSource(); fake.OnStopped = () => afterStop.Cancel(); var cancelledRestart = await controller.Execute(entry, ScriptAction.Restart, true, afterStop.Token); Check(!cancelledRestart.Success && fake.Starts == 2 && fake.Current.For(entry.Path).Count == 0, "cancel after stop never restarts and preserves observed stopped state"); fake.OnStopped = null;
        fake.Current = active; fake.OnStopped = () => fake.Current = unknown; Check(!(await controller.Execute(entry, ScriptAction.Restart, true)).Success && fake.Starts == 2, "unknown process appearing after stop blocks restart"); fake.OnStopped = null; fake.Current = plain;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Check(!(await controller.Execute(entry, ScriptAction.Start, true, cancellation.Token)).Success && fake.Starts == 2, "pre-cancel no start");
        fake.HoldSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously); var first = controller.Execute(entry, ScriptAction.Start, true); var parallel = await controller.Execute(entry, ScriptAction.Start, true); Check(!parallel.Success, "concurrent action rejected instead of queued restart"); fake.HoldSnapshot.SetResult(); fake.HoldSnapshot = null; await first;
        fake.Current = plain; Check(!(await controller.Execute(entry, ScriptAction.Recycle, false)).Success && recycler.Calls == 0, "cancel recycle no adapter call"); recycler.Outcome = Result.Fail("fixture refusal"); Check(!(await controller.Execute(entry, ScriptAction.Recycle, true)).Success && File.Exists(entry.Path), "recycle refusal preserves file and catalog"); Check(original.Scripts.Count == 1 && original.Scripts[0].Description == entry.Description, "actions preserve metadata");
        Check(!JsonSerializer.IsReflectionEnabledByDefault, "JSON reflection disabled");
        Check(Native.CimCreationMatches(1009, 1000) && !Native.CimCreationMatches(1010, 1000) && !Native.CimCreationMatches(1000, 1001) && !Native.CimCreationMatches(0, 0), "CIM microsecond precision exact truncation only");
    }
    static async Task NativeIntegration(string root)
    {
        // Only native adapter integration. Normal UI safety policy is not bypassed or exposed.
        var exe = Discovery.DefaultInterpreter(); Check(File.Exists(exe), "installed AHK available");
        var adapter = new WindowsProcesses();
        for (int round = 0; round < 2; round++)
        {
            string roundRoot = round == 0 ? root : System.IO.Path.Combine(root, "%USERNAME%"); Directory.CreateDirectory(roundRoot); var path = System.IO.Path.Combine(roundRoot, "harmless fixture.ahk"); const string harmless = "#Requires AutoHotkey v2.0\n#SingleInstance Off\n#NoTrayIcon\nPersistent\nSetTimer(() => 0, 1000)\n"; File.WriteAllText(path, harmless);
            string? expandedSibling = null; if (round == 1) { var siblingDir = System.IO.Path.Combine(root, Environment.GetEnvironmentVariable("USERNAME") ?? "fixture-user"); Directory.CreateDirectory(siblingDir); expandedSibling = System.IO.Path.Combine(siblingDir, "harmless fixture.ahk"); File.WriteAllText(expandedSibling, harmless); }
            using var launched = Process.Start(Commands.Launch(exe, path)) ?? throw new IOException("fixture launch failed");
            using var handle = Native.OpenProcess(0x1000 | 1 | 0x00100000, false, launched.Id);
            Check(!handle.IsInvalid && Native.Identity(handle, out _, out _), "native owned handle opened");
            try
            {
                Snapshot snapshot = await adapter.Snapshot(CancellationToken.None); var identity = snapshot.Processes.SingleOrDefault(p => p.Pid == launched.Id && Paths.Equal(p.Script, path));
                if (identity is null)
                {
                    Native.Identity(handle, out var timestamp, out _); Console.WriteLine("OWNED_FIXTURE nativeCreation=" + timestamp + " nativeVisible=" + snapshot.Processes.Any(p => p.Pid == launched.Id));
                    Console.WriteLine(await PrivateProbe.Run("$p=Get-CimInstance Win32_Process -Filter 'ProcessId=" + launched.Id + "';@{Created=$p.CreationDate.ToFileTimeUtc();HasCommand=[bool]$p.CommandLine;HasExecutable=[bool]$p.ExecutablePath}|ConvertTo-Json -Compress"));
                }
                Check(identity is not null, "native CIM script identity detected");
                if (expandedSibling is not null) Check(Paths.Equal(identity!.Script, path) && snapshot.For(expandedSibling).Count == 0 && !Policy.Check(new() { Path = expandedSibling, Interpreter = exe }, snapshot, ScriptAction.Stop, true).Success, "native literal percent script never assigns expanded sibling");
                Check(!(await adapter.Stop(identity! with { Created = identity!.Created + 1 }, CancellationToken.None)).Success && !launched.HasExited, "stale creation refuses termination");
                Check(!(await adapter.Stop(identity with { Executable = @"C:\Fixture\Wrong.exe" }, CancellationToken.None)).Success && !launched.HasExited, "mismatched executable refuses termination");
                Check(!(await adapter.Stop(identity with { Script = System.IO.Path.Combine(root, "other.ahk") }, CancellationToken.None)).Success && !launched.HasExited, "mismatched script refuses termination");
                Check(!Policy.Check(new() { Path = path, Interpreter = exe }, snapshot, ScriptAction.Start, true).Success, "native duplicate request refused");
                Check((await adapter.Stop(identity, CancellationToken.None)).Success && launched.HasExited, "native verified same-handle stop");
            }
            finally
            {
                if (!launched.HasExited && Native.Identity(handle, out var created, out var executable) && Paths.Equal(executable, exe) && created == launched.StartTime.ToUniversalTime().ToFileTimeUtc()) { Native.TerminateProcess(handle, 0); Native.WaitForSingleObject(handle, 5000); }
            }
        }
    }
}

sealed class FakeProcesses(Snapshot initial) : IProcessAccess
{
    public Snapshot Current = initial; public int Starts, Stops; public bool StartFailure, StopFailure; public TaskCompletionSource? HoldSnapshot; public Action? OnStopped; public string? PreflightFailure;
    public Result Preflight(ScriptEntry entry) => PreflightFailure is null ? Result.Ok("fixture preflight") : Result.Fail(PreflightFailure);
    public async Task<Snapshot> Snapshot(CancellationToken token) { if (HoldSnapshot is { } hold) await hold.Task.WaitAsync(token); return Current; }
    public Task<Result> Start(ScriptEntry entry, CancellationToken token) { Starts++; if (StartFailure) return Task.FromResult(Result.Fail("fixture start failure")); Current = new([new(42, 100, entry.Interpreter, entry.Path)], true, "fixture"); return Task.FromResult(Result.Ok("fixture start")); }
    public Task<Result> Stop(ProcessIdentity identity, CancellationToken token) { Stops++; if (StopFailure || !Current.Processes.Contains(identity)) return Task.FromResult(Result.Fail("fixture stop failure")); Current = new([], true, "fixture"); OnStopped?.Invoke(); return Task.FromResult(Result.Ok("fixture stop")); }
}
sealed class FakeRecycler : IRecycler { public int Calls; public Result Outcome = Result.Ok("fixture"); public Result Recycle(string path) { Calls++; return Outcome; } }
