using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    sealed class FakeExistingWindow : IExistingWindow
    {
        public bool Closing { get; set; }
        public bool Minimized { get; set; }
        public bool Visible { get; set; }
        public bool Activation { get; set; } = true;
        public int Restores, Shows, Activations, Flashes;
        public void Restore() { Restores++; Minimized = false; }
        public void Show() { Shows++; Visible = true; }
        public bool Activate() { Activations++; return Activation; }
        public void Flash() => Flashes++;
    }
    sealed class DeadExistingPeer : IExistingPeer
    {
        public uint Pid => 1; public long Created => 1; public bool Alive => false; public void Dispose() { }
    }
    static ExistingScope TestScope(string name)
    {
        var directory = Path.Combine(root,name);
        return ExistingScope.Current(directory) with { Endpoint = "Bezoro.Reopen.TEST." + Guid.NewGuid().ToString("N") };
    }
    static async Task WaitFor(Func<bool> condition, TimeSpan? duration = null)
    {
        var timer = Stopwatch.StartNew(); var limit = duration ?? TimeSpan.FromSeconds(5);
        while (!condition()) { if (timer.Elapsed >= limit) throw new TimeoutException("Isolated worker readiness timeout."); await Task.Delay(20); }
    }
    sealed class ReopenWorker : IAsyncDisposable
    {
        public Process Process { get; }
        public string Directory { get; }
        public ExistingScope Scope { get; }
        readonly Task<string> stdout,stderr;
        ReopenWorker(Process process, string directory, ExistingScope scope)
        { Process = process; Directory = directory; Scope = scope; stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync(); }
        public static async Task<ReopenWorker> Start(ExistingScope scope,string name,string mode)
        {
            string directory = Path.Combine(root,name); System.IO.Directory.CreateDirectory(directory);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--reopen-worker",directory,scope.Endpoint,scope.CatalogDigest,mode }) info.ArgumentList.Add(arg);
            var worker = new ReopenWorker(Process.Start(info) ?? throw new IOException("Worker creation failed."),directory,scope);
            await WaitFor(() => File.Exists(Path.Combine(directory,"ready.txt")) || worker.Process.HasExited);
            if (worker.Process.HasExited && mode != "client") throw new IOException("Worker stopped before readiness.");
            return worker;
        }
        public int Shows => File.Exists(Path.Combine(Directory,"state.txt")) && int.TryParse(File.ReadAllText(Path.Combine(Directory,"state.txt")),out var count) ? count : 0;
        public async ValueTask DisposeAsync()
        {
            File.WriteAllText(Path.Combine(Directory,"stop.txt"),"stop isolated worker");
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            File.WriteAllText(Path.Combine(Directory,"worker.log"),await stdout + await stderr);
            Process.Dispose();
        }
    }
    static async Task<int> RunReopenWorker(string[] args)
    {
        // Test executable only: unique test endpoint, no Application/UI/global mutex/profile.
        string directory = Path.GetFullPath(args[1]); string endpoint = args[2], digest = args[3], mode = args[4];
        if (!endpoint.StartsWith("Bezoro.Reopen.TEST.",StringComparison.Ordinal) || digest.Length != 64 || mode is not ("ready" or "starting" or "never" or "abort" or "client")) return 2;
        Directory.CreateDirectory(directory); var scope = ExistingScope.Current(directory) with { Endpoint = endpoint, CatalogDigest = digest };
        using var own = Process.GetCurrentProcess(); Console.WriteLine($"worker pid={own.Id} session={scope.Session} sid={scope.Sid} exe={scope.Executable}");
        if (mode == "client")
        {
            uint granted = 0; var result = await ExistingInstanceClient.Request(scope,ExistingOperation.ShowExisting,grantForeground: pid => { granted = pid; return true; });
            File.WriteAllText(Path.Combine(directory,"reply.txt"),$"{result.Status}|{granted}|{result.ForegroundGranted}"); File.WriteAllText(Path.Combine(directory,"ready.txt"),"client complete"); return result.Success ? 0 : 1;
        }
        int shows = 0;
        await using var server = new ExistingInstanceServer(scope,requestTimeout: TimeSpan.FromSeconds(2));
        Task<ExistingStatus> Show(CancellationToken token)
        {
            if (mode == "abort") Environment.Exit(0);
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref shows); File.WriteAllText(Path.Combine(directory,"state.txt"),shows.ToString());
            return Task.FromResult(ExistingStatus.Shown);
        }
        File.WriteAllText(Path.Combine(directory,"state.txt"),"0"); File.WriteAllText(Path.Combine(directory,"ready.txt"),own.Id.ToString());
        if (mode is "ready" or "abort") server.MarkReady(Show);
        var timer = Stopwatch.StartNew(); bool marked = mode is "ready" or "abort";
        while (timer.Elapsed < TimeSpan.FromSeconds(15) && !File.Exists(Path.Combine(directory,"stop.txt")))
        {
            if (!marked && (mode == "starting" && timer.ElapsedMilliseconds >= 800 || File.Exists(Path.Combine(directory,"make-ready.txt")))) { marked = true; server.MarkReady(Show); }
            await Task.Delay(20);
        }
        Console.WriteLine($"worker terminal shows={shows} lifecycle={server.Lifecycle}"); return 0;
    }
    static async Task ExpectBadFrame(ExistingScope scope,Func<ExistingFrame,byte[]> payload,string label)
    {
        using var pipe = new NamedPipeClientStream(".",scope.Endpoint,PipeDirection.InOut,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await pipe.ConnectAsync(timeout.Token);
        var hello = await ExistingFrame.Read(pipe,timeout.Token); await pipe.WriteAsync(payload(hello),timeout.Token); await pipe.FlushAsync(timeout.Token);
        bool refused = false;
        try { _ = await ExistingFrame.Read(pipe,timeout.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { refused = true; }
        Check(refused,label + " rejected by actual server");
    }
    static byte[] Wire(ExistingFrame hello,byte operation=1,Guid? nonce=null,string? digest=null,long? deadline=null)
    {
        using var stream = new MemoryStream(); new ExistingFrame(operation,nonce ?? hello.Nonce,digest ?? hello.CatalogDigest,deadline ?? DateTime.UtcNow.AddSeconds(1).Ticks).Write(stream,CancellationToken.None).GetAwaiter().GetResult(); return stream.ToArray();
    }
    static async Task RunReopenReceiptChecks()
    {
        foreach(string kind in new[]{"premature","nonce","catalog","operation"})
        {
            var scope=TestScope("receipt-"+kind);await using var server=new ExistingInstanceServer(scope);var window=new FakeExistingWindow();
            using(var client=new NamedPipeClientStream(".",scope.Endpoint,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly))
            {
                using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(2));await client.ConnectAsync(bound.Token);var hello=await ExistingFrame.Read(client,bound.Token);
                await new ExistingFrame(1,hello.Nonce,scope.CatalogDigest,DateTime.UtcNow.AddSeconds(1).Ticks).Write(client,bound.Token);
                await new ExistingFrame(kind=="operation"?(byte)31:ExistingFrame.Receipt,kind=="nonce"?Guid.NewGuid():hello.Nonce,kind=="catalog"?new string('D',64):scope.CatalogDigest).Write(client,bound.Token);
                bool refused=false;try{_=await ExistingFrame.Read(client,bound.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException or InvalidDataException){refused=true;}
                Check(refused&&window.Shows==0&&server.Lifecycle==ExistingLifecycle.Starting,kind+" receipt refuses Starting request before any display");
            }
            server.MarkReady(token=>Task.FromResult(ExistingWindowDisplay.Show(window,token)));await Task.Delay(50);
            Check(window.Shows==0,kind+" receipt leaves no deferred display after Ready");
            Check((await ExistingInstanceClient.Request(scope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>true)).Success&&window.Shows==1,kind+" receipt refusal preserves listener for a fresh authenticated request");
        }
    }
    static async Task RunReopenExisting()
    {
        foreach (var args in new[] { Array.Empty<string>(),new[] { "--tray" },new[] { "--tray","--login" } })
        {
            Check(WpfProgram.TryLaunch(args,out var launch) && !launch.Fixture && launch.Tray == args.Contains("--tray") && launch.Login == args.Contains("--login"),"actual launch parser retains manual/tray/login origin " + args.Length);
        }
        foreach (var args in new[] { new[] { "--login" },new[] { "--tray","--login","--login" },new[] { "--tray","--login","--data-dir",root },new[] { "--fixture-ui" },new[] { "--unknown" },new[] { "--scale","2" } }) Check(!WpfProgram.TryLaunch(args,out _),"invalid arguments refuse before real ownership/IPC: " + string.Join(" ",args));
        Check(WpfProgram.TryLaunch(["--fixture-ui","--data-dir",root],out var fixture) && fixture.Fixture,"fixture entry remains distinct and bypasses real route");
        Check(WpfProgram.RunNormalCutover(["--normal-cutover","missing"]) == 2,"invalid cutover returns before normal IPC");
        int primary=0,secondary=0; using var owner = new FixtureOwner { Held = false };
        Check(WpfProgram.RouteOwnership(owner,()=>{primary++;return 9;},()=>{secondary++;return 0;}) == 0 && primary == 0 && secondary == 1,"actual nonowner routing invokes only client branch, zero catalog/session/ledger/tray/timer/startup construction");
        owner.Trusted=false; Check(WpfProgram.RouteOwnership(owner,()=>{primary++;return 9;},()=>{secondary++;return 9;}) == 1 && primary == 0 && secondary == 1,"untrusted owner opens neither primary nor client route");
        owner.Trusted=true; owner.Held=true; Check(WpfProgram.RouteOwnership(owner,()=>{primary++;return 0;},()=>{secondary++;return 9;}) == 0 && primary == 1 && secondary == 1,"genuine Held ownership alone enters existing first-start branch");
        var scope = TestScope("catalog-not-created"); Check(!Directory.Exists(Path.Combine(root,"catalog-not-created")),"scope identity creates no data directory");
        Check(ExistingScope.Current(Path.Combine(root,"same-catalog")).CatalogDigest == ExistingScope.Current(Path.Combine(root,"SAME-CATALOG") + Path.DirectorySeparatorChar).CatalogDigest,"scope case and trailing separator normalize consistently");
        Check(ExistingScope.Current(Path.Combine(root,"other-catalog")).Endpoint != ExistingScope.Current(Path.Combine(root,"same-catalog")).Endpoint,"different catalogs have different exact endpoints");
        foreach (string alias in new[] { @"\\server\catalog",Path.Combine(root,"ambiguous."),Path.Combine(root,"ambiguous "),Path.Combine(root,"catalog:stream") })
        { bool rejected=false;try { _=ExistingScope.Current(alias); } catch(Exception ex) when(ex is ArgumentException or IOException){rejected=true;} Check(rejected,"ambiguous catalog spelling refused " + alias); }
        foreach (bool minimized in new[] { false,true }) foreach (bool visible in new[] { false,true }) foreach (bool activated in new[] { false,true })
        {
            var window=new FakeExistingWindow{Minimized=minimized,Visible=visible,Activation=activated}; var status=ExistingWindowDisplay.Show(window,CancellationToken.None);
            Check(window.Shows==1&&window.Activations==1&&window.Restores==(minimized?1:0)&&window.Flashes==(activated?0:1)&&status==(activated?ExistingStatus.Shown:ExistingStatus.ShownWithoutForeground),$"sole fake window restores/shows and truthful foreground result min={minimized} visible={visible} activate={activated}");
        }
        var closingWindow=new FakeExistingWindow{Closing=true,Minimized=true}; Check(ExistingWindowDisplay.Show(closingWindow,CancellationToken.None)==ExistingStatus.Closing&&closingWindow.Shows==0&&closingWindow.Restores==0,"closing window is never reopened/restored");
        using(var canceled=new CancellationTokenSource()){canceled.Cancel();var w=new FakeExistingWindow();Check(ExistingWindowDisplay.Show(w,canceled.Token)==ExistingStatus.Closing&&w.Shows==0,"canceled queued display has zero GUI effects");}
        var timer=Stopwatch.StartNew();int grants=0;var absent=await ExistingInstanceClient.Request(scope,ExistingOperation.ShowExisting,TimeSpan.FromMilliseconds(150),grantForeground:_=>{grants++;return true;});
        Check(!absent.Success&&timer.Elapsed<TimeSpan.FromSeconds(3)&&grants==0,"unavailable owner/listener fails boundedly with zero foreground grant or fallback UI");
        await using(var worker=await ReopenWorker.Start(scope,"ready-worker","ready"))
        {
            bool contention=false;try{await using var duplicate=new ExistingInstanceServer(scope);}catch(IOException){contention=true;} Check(contention,"FirstPipeInstance prevents duplicate primary listener");
            foreach(var wrong in new[]{scope with{Sid=scope.Sid+"-999"},scope with{Session=scope.Session+1},scope with{Executable=Path.Combine(root,"unexpected.exe")}})
            {
                var reply=await ExistingInstanceClient.Request(wrong,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>{grants++;return true;});Check(reply.Status==ExistingStatus.Rejected&&worker.Shows==0&&grants==0,"actual held OS peer proof rejects wrong SID/session/executable");
            }
            var noEvidence=await ExistingInstanceClient.Request(scope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),authenticate:(_,_,_)=>null,grantForeground:_=>{grants++;return true;});
            var expired=await ExistingInstanceClient.Request(scope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),authenticate:(_,_,_)=>new DeadExistingPeer(),grantForeground:_=>{grants++;return true;});
            Check(noEvidence.Status==ExistingStatus.Rejected&&expired.Status==ExistingStatus.Rejected&&grants==0&&worker.Shows==0,"inaccessible/expired evidence cannot grant foreground or show");
            foreach(var operation in new[]{ExistingOperation.Dedupe,ExistingOperation.Dedupe})
            {var reply=await ExistingInstanceClient.Request(scope,operation,TimeSpan.FromSeconds(1),grantForeground:_=>{grants++;return true;});Check(reply.Status==ExistingStatus.Deduplicated&&worker.Shows==0&&grants==0,"tray/login duplicate authenticates without show/grant/login replay: " + reply.Status);}
            uint verifiedPid=0;long verifiedCreation=0;
            IExistingPeer? Capture(PipeStream pipe,bool server,ExistingScope expected){var peer=NativeExistingPeer.Authenticate(pipe,server,expected);if(peer is not null){verifiedPid=peer.Pid;verifiedCreation=peer.Created;}return peer;}
            var manual=await ExistingInstanceClient.Request(scope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),authenticate:Capture,grantForeground:pid=>{Check(pid==(uint)worker.Process.Id,"foreground grant targets only authenticated held server PID");return false;});
            Check(manual.Status==ExistingStatus.Shown&&!manual.ForegroundGranted&&verifiedPid==(uint)worker.Process.Id&&verifiedCreation>0&&worker.Shows==1,"actual OS-authenticated pipe round-trip returns real fake-display terminal result despite denied foreground grant");
            await ExpectBadFrame(scope,h=>{var bytes=Wire(h);bytes[4]=2;return bytes;},"unsupported version");
            await ExpectBadFrame(scope,h=>Wire(h,99),"unknown operation");
            await ExpectBadFrame(scope,h=>Wire(h,nonce:Guid.NewGuid()),"wrong correlation nonce");
            await ExpectBadFrame(scope,h=>Wire(h,digest:new string('B',64)),"wrong catalog binding");
            await ExpectBadFrame(scope,h=>Wire(h,deadline:DateTime.UtcNow.AddSeconds(-1).Ticks),"expired request deadline");
            await ExpectBadFrame(scope,h=>{var b=Wire(h);BitConverter.TryWriteBytes(b.AsSpan(0,4),513);return b;},"oversized frame");
            await ExpectBadFrame(scope,h=>{var b=Wire(h);BitConverter.TryWriteBytes(b.AsSpan(0,4),59);return b;},"trailing body payload");
            using(var stalled=new NamedPipeClientStream(".",scope.Endpoint,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly))
            {using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(3));await stalled.ConnectAsync(bound.Token);_=await ExistingFrame.Read(stalled,bound.Token);bool closed=false;try{_=await ExistingFrame.Read(stalled,bound.Token);}catch(Exception ex)when(ex is IOException or OperationCanceledException){closed=true;}Check(closed,"stalled client cannot hold server beyond per-request deadline");}
            using(var truncated=new NamedPipeClientStream(".",scope.Endpoint,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly))
            {using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(1));await truncated.ConnectAsync(bound.Token);_=await ExistingFrame.Read(truncated,bound.Token);await truncated.WriteAsync(new byte[]{58,0,0,0,1},bound.Token);}
            var afterBad=await ExistingInstanceClient.Request(scope,ExistingOperation.Dedupe,TimeSpan.FromSeconds(1),grantForeground:_=>throw new InvalidOperationException("dedupe grant"));
            Check(afterBad.Status==ExistingStatus.Deduplicated&&worker.Shows==1,"listener survives malformed/truncated/disconnected/stalled requests with zero extra show");
            await using(var client=await ReopenWorker.Start(scope,"genuine-client-worker","client"))
            {var reply=File.ReadAllText(Path.Combine(client.Directory,"reply.txt"));Check(reply.StartsWith("Shown|"+worker.Process.Id+"|True",StringComparison.Ordinal),"two isolated helper processes authenticate actual peer PID/SID/session/image and correlate round-trip");}
            var concurrent=await Task.WhenAll(Enumerable.Range(0,3).Select(_=>ExistingInstanceClient.Request(scope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(2),grantForeground:_=>true)));
            Check(concurrent.All(r=>r.Status==ExistingStatus.Shown)&&worker.Shows==5,"concurrent manual relaunches show only sole primary, no second host or effects");
        }
        var startingScope=TestScope("starting-catalog");
        await using(var worker=await ReopenWorker.Start(startingScope,"starting-worker","starting"))
        {
            var dedupe=await ExistingInstanceClient.Request(startingScope,ExistingOperation.Dedupe,TimeSpan.FromSeconds(1),grantForeground:_=>throw new InvalidOperationException("dedupe grant"));
            Check(dedupe.Status==ExistingStatus.Deduplicated&&worker.Shows==0,"Starting login/tray dedupe acknowledges without waiting or replay");
            var show=ExistingInstanceClient.Request(startingScope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(2),grantForeground:_=>true);await Task.Delay(100);
            Check(worker.Shows==0&&!show.IsCompleted,"Starting Show waits for GUI-safe readiness");
            Check((await show).Status==ExistingStatus.Shown&&worker.Shows==1,"Ready releases exactly existing pending manual show");
        }
        var neverScope=TestScope("never-catalog");
        await using(var worker=await ReopenWorker.Start(neverScope,"never-worker","never"))
        {
            var reply=await ExistingInstanceClient.Request(neverScope,ExistingOperation.ShowExisting,TimeSpan.FromMilliseconds(200),grantForeground:_=>true);
            Check(!reply.Success&&worker.Shows==0,"deadline before Ready refuses terminal show success");File.WriteAllText(Path.Combine(worker.Directory,"make-ready.txt"),"ready now");await Task.Delay(150);
            Check(worker.Shows==0,"timed-out/disconnected Starting request never causes orphan show after Ready");
            Check((await ExistingInstanceClient.Request(neverScope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>true)).Success&&worker.Shows==1,"new valid request succeeds after prior timeout without retained intent");
        }
        var failedScope=TestScope("startup-failure");
        await using(var server=new ExistingInstanceServer(failedScope))
        {
            var pending=ExistingInstanceClient.Request(failedScope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>true);await Task.Delay(50);server.BeginClosing();
            Check(!(await pending).Success&&server.Lifecycle==ExistingLifecycle.Closing,"startup failure/Closing cancels queued Show with no partial primary");
            int shows=0;server.MarkReady(_=>{shows++;return Task.FromResult(ExistingStatus.Shown);});Check(shows==0&&server.Lifecycle==ExistingLifecycle.Closing,"late Ready cannot reopen Closing server");
        }
        var abortScope=TestScope("abrupt-catalog");
        await using(var worker=await ReopenWorker.Start(abortScope,"abrupt-worker","abort"))
        {var reply=await ExistingInstanceClient.Request(abortScope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>true);Check(!reply.Success,"abrupt primary exit cannot falsely acknowledge Show");}
        foreach(bool mismatch in new[]{false,true})
        {
            var badScope=TestScope("bad-response-"+mismatch);using var pipe=new NamedPipeServerStream(badScope.Endpoint,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly|PipeOptions.FirstPipeInstance);
            var serve=Task.Run(async()=>{await pipe.WaitForConnectionAsync();Guid nonce=Guid.NewGuid();await new ExistingFrame(ExistingFrame.Challenge,nonce,badScope.CatalogDigest).Write(pipe,CancellationToken.None);_=await ExistingFrame.Read(pipe,CancellationToken.None);await new ExistingFrame(21,mismatch?Guid.NewGuid():nonce,mismatch?badScope.CatalogDigest:new string('C',64)).Write(pipe,CancellationToken.None);});
            var reply=await ExistingInstanceClient.Request(badScope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>true);await serve;
            Check(reply.Status==ExistingStatus.Rejected,"client refuses mismatched response nonce/catalog on actual transport");
        }
        using(var r=new FunctionalRig("reopen-exit-confirm"))
        {
            var exitScope=TestScope("exit-confirm-catalog");await using var server=new ExistingInstanceServer(exitScope);int shows=0;server.MarkReady(_=>{shows++;return Task.FromResult(ExistingStatus.Shown);});
            var prompt=new TaskCompletionSource<bool>();var exit=new ExitCoordinator(r.Session,()=>prompt.Task,server.BeginClosing,()=>{},TimeSpan.FromMilliseconds(10));var request=exit.Request();
            Check((await ExistingInstanceClient.Request(exitScope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>true)).Success&&server.Lifecycle==ExistingLifecycle.Ready,"unconfirmed exit prompt leaves IPC Ready and allows show without script lane");
            prompt.SetResult(false);Check(!await request&&shows==1&&server.Lifecycle==ExistingLifecycle.Ready&&!r.Session.ShuttingDown,"canceled exit preserves listener and primary session");
            await new ExitCoordinator(r.Session,()=>Task.FromResult(true),server.BeginClosing,()=>{},TimeSpan.FromMilliseconds(10)).Request();
            Check(server.Lifecycle==ExistingLifecycle.Closing&&r.Session.ShuttingDown&&r.Processes.Starts==0&&r.Processes.Stops==0,"approved final exit marks Closing before session shutdown and no child effects");
        }
        using(var r=new FunctionalRig("reopen-busy"))
        {
            r.EmptyProcesses();await r.Session.Refresh();r.Processes.HoldStart=new();var authorized=r.Session.Act(ScriptAction.Start);
            var busyScope=TestScope("busy-catalog");await using var server=new ExistingInstanceServer(busyScope);var fake=new FakeExistingWindow();server.MarkReady(token=>Task.FromResult(ExistingWindowDisplay.Show(fake,token)));
            int starts=r.Processes.Starts,snapshots=r.Processes.SnapshotCalls;var before=File.ReadAllBytes(r.Store.FilePath);
            Check(r.Session.Busy&&(await ExistingInstanceClient.Request(busyScope,ExistingOperation.ShowExisting,TimeSpan.FromSeconds(1),grantForeground:_=>true)).Success&&fake.Shows==1,"Ready busy primary shows existing UI without entering occupied script lane");
            Check(r.Session.Busy&&r.Processes.Starts==starts&&r.Processes.Stops==0&&r.Processes.SnapshotCalls==snapshots&&before.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)),"reopen preserves occupied lane, catalog, snapshot and primary effect counts");
            r.Processes.HoldStart.SetResult();await authorized;
        }
        using(var store=new CatalogStore(Path.Combine(root,"writer-lock")))
        {bool locked=false;try{using var duplicate=new CatalogStore(store.DirectoryPath);}catch(IOException){locked=true;}Check(locked&&File.Exists(Path.Combine(store.DirectoryPath,"writer.lock")),"catalog FileShare.None and writer.lock remain exclusive and untouched");}
    }
}
