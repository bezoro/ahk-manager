using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    sealed class FakeBrushTask : IBrushTask
    {
        public BrushProbe State = WindowsBrushTask.Validate(WindowsBrushTask.BaselineXml, WindowsBrushTask.CapturedSecurity, true);
        public int Runs, Registers;
        public int Session;
        public Action? BeforeRun { get; set; } public Action? BeforeRegister { get; set; }
        public BrushProbe Probe() => State;
        public Result Run(int session) { BeforeRun?.Invoke(); Runs++; Session=session; return Result.Ok("fake Run request"); }
        public Result SetLogon(bool enabled) { BeforeRegister?.Invoke(); Registers++; State=State with { LogonEnabled=enabled }; return Result.Ok("fake registration"); }
    }
    sealed class FakeBrushDispatch(FakeBrushTask task, FixtureProcesses processes) : IBrushDispatcher
    {
        public bool Fixture => true;
        public int Calls;
        public bool Lost, Alive=true;
        public Func<string,Result>? StageHook;
        public Action<BrushReply>? AfterReply { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public async Task<BrushReply> Dispatch(BrushJournal j, Func<string,Result> authorize, CancellationToken token)
        {
            Calls++; if(Hold is not null) await Hold.Task.WaitAsync(token);
            async Task<Result> Step(string stage) { await Task.CompletedTask; var hook=StageHook?.Invoke(stage); return hook is { Success:false } ? hook : authorize(stage); }
            var reply=await BrushTaskControls.RunHelper(j,task,processes,Step,()=>Alive,token);
            if(Lost) throw new IOException("fake lost reply"); AfterReply?.Invoke(reply); return reply;
        }
    }
    sealed class IdleBrushTask(FakeBrushTask inner) : IBrushTask, IBrushTaskIdleWitness
    {
        public bool Fixture { get; set; } = true;
        public int? State = 3; public int Reads; public Action<int>? Reading { get; set; }
        public BrushProbe Probe() => inner.Probe();
        public Result Run(int session) => inner.Run(session);
        public Result SetLogon(bool enabled) => inner.SetLogon(enabled);
        public BrushTaskIdle ReadIdle() { Reading?.Invoke(++Reads); return new(inner.Probe(),State); }
    }
    sealed class IdleBrushDispatch(FakeBrushDispatch inner) : IBrushDispatcher, IBrushHelperQuiescence
    {
        public bool Fixture { get; set; } = true;
        public int Reads; public Action<int>? Reading; public bool Quiet = true;
        public Task<BrushReply> Dispatch(BrushJournal j,Func<string,Result> authorize,CancellationToken token) => inner.Dispatch(j,authorize,token);
        public Result CheckQuiescence(BrushJournal j) { Reading?.Invoke(++Reads); return Quiet ? Result.Ok("scripted quiescent helper") : Result.Fail("scripted live/unknown helper"); }
    }
    sealed class HelperCandidate : IBrushHelperCandidate
    {
        public uint Pid { get; set; } = 7100; public string Image { get; set; } = Environment.ProcessPath!;
        public string Sid { get; set; } = "S-1-5-21-fixture"; public int Session { get; set; } = 1;
        public string Login { get; set; } = "S-1-5-21-fixture|login1"; public bool? Elevation { get; set; } = false;
        public bool Living = true, Disposed; public int Reads; public int? DieAt;
        public bool Alive => Living && (++Reads < (DieAt ?? int.MaxValue));
        public void Dispose() => Disposed = true;
    }
    static string BrushTyped(BrushJournal j) => JsonSerializer.Serialize(j,RegisterRecoveryJson.Default.BrushJournal);
    static async Task RunBrushStoppedRecovery(bool barriersOnly = false)
    {
        if (!barriersOnly)
        {
        var slotCalls=new List<string>();
        var state=WindowsBrushTask.ReadIdleWitness(()=>{slotCalls.Add("State");return 3;},()=>{slotCalls.Add("Fresh fields");return new FakeBrushTask().State;});
        Check(state.State==3 && state.Probe.Ready && slotCalls.SequenceEqual(new[]{"State","Fresh fields"}),"typed production TASK_STATE integer seam reads READY then fresh task fields");
        foreach(string kind in new[]{"normal","live-helper","unknown-elevation","inaccessible","replaced","other-normal","current-login","current-session","current-image","missing-current","foreign-sid","local-dispatch"})
        {
            var j=BrushIntent(BrushOperation.Stop);var current=new HelperCandidate();var helper=new HelperCandidate{Pid=7102,Elevation=true};
            if(kind=="unknown-elevation")helper.Elevation=null;
            if(kind=="replaced")current.DieAt=2;
            if(kind=="other-normal")helper.Elevation=false;
            if(kind=="current-login")current.Login="other login";
            if(kind=="current-session")current.Session=2;
            if(kind=="current-image")current.Image+=".other";
            if(kind=="foreign-sid")helper.Sid="other verified SID";
            IEnumerable<IBrushHelperCandidate?> candidates=kind switch {"inaccessible"=>[current,null],"missing-current"=>[],"live-helper" or "unknown-elevation" or "other-normal" or "foreign-sid"=>[current,helper],_=>[current]};
            var result=BrushTaskHelper.ProveQuiescence(j,kind=="local-dispatch",7100,()=>candidates);
            Check(result.Success==(kind is "normal" or "foreign-sid"),"held query-only helper classification "+kind);
            Check(kind=="local-dispatch" || kind=="missing-current" || current.Disposed,"helper handles retained and disposed on "+kind);
        }
        foreach(var operation in new[]{BrushOperation.Stop,BrushOperation.Restart})
        {
            string dir=Path.Combine(root,"production-flow-"+operation);Directory.CreateDirectory(dir);
            var task=new FakeBrushTask();var idle=new IdleBrushTask(task);var processes=new FixtureProcesses{Current=new([BrushProcess()],true,"fake live")};
            var transport=new FakeBrushDispatch(task,processes);var dispatch=new IdleBrushDispatch(transport);var store=new BrushOperationStore(dir);var controls=new BrushTaskControls(idle,dispatch,store);
            if(operation==BrushOperation.Stop)transport.Lost=true;
            else transport.StageHook=stage=>stage=="Run" ? Result.Fail("request failed after Stop acknowledgement") : Result.Ok("Stop admitted");
            var failed=await controls.Execute(BrushIntent(operation),()=>Result.Ok("fixture exact owner graph"),CancellationToken.None);
            var pending=store.Current!;string before=BrushTyped(pending);int effects=transport.Calls;
            Check(!failed.Confirmed && store.Pending && pending.Stage=="Stop" && processes.Stops==1 && task.Runs==0,"production "+operation+" stop/lost-or-partial enters durable pending Stop with Brush absent");
            var settled=await controls.Settle(_=>Result.Ok("same owner/login/profile graph"),processes,CancellationToken.None,new ConfirmationClock());
            var completed=store.Current!;var expected=JsonSerializer.Deserialize(before,RegisterRecoveryJson.Default.BrushJournal)!;expected.Completed=true;expected.Stage="Completed";expected.Outcome=completed.Outcome;
            Check(!store.Pending && !new BrushOperationStore(dir).Pending && BrushTyped(completed)==BrushTyped(expected),"readonly "+operation+" conclusion preserves login deadline owner all Run fields and original intent");
            Check(transport.Calls==effects && task.Runs==0 && processes.Stops==1 && processes.Starts==0 && !settled.ProcessStopped && !settled.RequestAccepted,"readonly "+operation+" settlement dispatches no effect and invents no stop/Run proof");
            Check(settled.Confirmed==(operation==BrushOperation.Stop) && UiSession.BrushInspectionPresentation(settled).Error==(operation==BrushOperation.Restart),"truthful Stop versus failed Restart existing UI presentation");
            Check(completed.Outcome.Contains("somente leitura") && (operation!=BrushOperation.Stop || completed.Outcome.Contains("anterior nao comprovado")),"absence outcome makes no historical kill claim "+operation);
            transport.Lost=false;transport.StageHook=null;task.BeforeRun=()=>processes.Current=new([BrushProcess(DateTime.UtcNow.AddSeconds(1).ToFileTimeUtc())],true,"new explicit fake process");
            var next=BrushIntent(BrushOperation.Start);var restored=await controls.Execute(next,()=>Result.Ok("NEW current owner graph and effect authorization"),CancellationToken.None);
            Check(restored.Confirmed && !store.Pending && next.Nonce!=pending.Nonce && task.Runs==1 && transport.Calls==effects+1 && store.Current!.RunAuthorizedFileTime is >0,"separate explicit Start has fresh nonce/new authorization and one Run after "+operation+" reconciliation");
            Check(store.Current!.AllowedLogon==pending.AllowedLogon && processes.Stops==1 && processes.Starts==0,"restoration preserves login and other effects "+operation);
        }
        }
        bool remaining = !barriersOnly;
        foreach(string kind in new[]{"stop","restart-stop","restart-run","start-run","start-not-accepted","present","unknown","duplicate","replaced","helper-live","helper-unknown","helper-inaccessible","dispatch-live","queued","running","state-unknown","disabled","demand-disabled","definition","security","login","authority","login-key","hash","typed","cancel","timeout","late-present","late-helper","late-task","final-helper","late-owner","late-journal","save-failure","fixture-mode","unavailable-witness"})
        {
            if (kind == "typed") remaining = true; if (!remaining) continue;
            string dir=Path.Combine(root,"barrier-"+kind);Directory.CreateDirectory(dir);var task=new FakeBrushTask();var idle=new IdleBrushTask(task);var processes=new FixtureProcesses();var transport=new FakeBrushDispatch(task,processes);var dispatch=new IdleBrushDispatch(transport);var store=new BrushOperationStore(dir);var controls=new BrushTaskControls(idle,dispatch,store);var clock=new ConfirmationClock();using var cancel=new CancellationTokenSource();bool owner=true;
            var j=BrushIntent(kind is "restart-stop" or "restart-run" ? BrushOperation.Restart : kind is "start-run" or "start-not-accepted" ? BrushOperation.Start : BrushOperation.Stop);
            j.Stage=kind is "restart-run" or "start-run" or "start-not-accepted" ? "Run" : "Stop";j.Definition=task.State.Definition;j.Security=task.State.Security;
            j.DeadlineUtcTicks=DateTime.UtcNow.AddSeconds(-10).Ticks;
            if(kind=="start-not-accepted"){j.Version=2;j.RunAuthorizedFileTime=DateTime.UtcNow.AddSeconds(-30).ToFileTimeUtc();j.RequestAccepted=false;}
            store.Save(j);byte[] before=File.ReadAllBytes(store.FilePath);byte[]? late=null;
            if(kind is "present" or "replaced")processes.Current=new([BrushProcess(kind=="replaced"?200:100)],true,"known present");
            if(kind=="duplicate")processes.Current=new([BrushProcess(),BrushProcess() with{Pid=7110}],true,"duplicates");
            if(kind=="unknown")processes.Current=new([],false,"unknown");
            if(kind.StartsWith("helper-") || kind=="dispatch-live")dispatch.Quiet=false;
            if(kind=="queued")idle.State=2;if(kind=="running")idle.State=4;if(kind=="state-unknown")idle.State=null;
            if(kind=="disabled")task.State=task.State with{TaskEnabled=false};if(kind=="demand-disabled")task.State=task.State with{DemandStart=false};
            if(kind=="definition")task.State=task.State with{Definition=task.State.Definition+"x"};if(kind=="security")task.State=task.State with{Security=task.State.Security+"x"};if(kind=="login")task.State=task.State with{LogonEnabled=false};
            if(kind is "authority" or "login-key")owner=false;
            if(kind=="hash"){File.AppendAllText(store.FilePath," ");late=File.ReadAllBytes(store.FilePath);}
            if(kind=="typed"){File.WriteAllText(store.FilePath,JsonSerializer.Serialize(j,new RegisterRecoveryJson(new System.Text.Json.JsonSerializerOptions{WriteIndented=true}).BrushJournal));late=File.ReadAllBytes(store.FilePath);store=new BrushOperationStore(dir);controls=new BrushTaskControls(idle,dispatch,store);}
            if(kind=="cancel")cancel.Cancel();
            if(kind=="fixture-mode")idle.Fixture=false;
            if(kind=="late-helper")dispatch.Reading=n=>{if(n==2)dispatch.Quiet=false;};
            if(kind=="final-helper")dispatch.Reading=n=>{if(n==4)dispatch.Quiet=false;};
            if(kind=="late-task")dispatch.Reading=n=>{if(n==2)task.State=task.State with{LogonEnabled=false};};
            processes.BeforeSnapshot=()=>{if(kind=="timeout")clock.Millis=2100;if(processes.SnapshotCalls==2){if(kind=="late-present")processes.Current=new([BrushProcess()],true,"late process");if(kind=="late-owner")owner=false;if(kind=="late-journal"){File.AppendAllText(store.FilePath," ");late=File.ReadAllBytes(store.FilePath);}if(kind=="save-failure")File.WriteAllText(store.FilePath+".tmp","external pending temp");}};
            if(kind=="unavailable-witness")controls=new BrushTaskControls(task,transport,store);
            var result=await controls.Settle(_=>owner?Result.Ok("scripted full current owner/hash graph"):Result.Fail("current graph/login changed"),processes,cancel.Token,clock);
            bool success=kind is "stop" or "restart-stop" or "restart-run" or "start-run" or "start-not-accepted";
            Check(store.Pending!=success && result.Confirmed==(success && kind=="stop"),"absence conclusion truth and durable pending barrier "+kind);
            Check(transport.Calls==0 && task.Runs==0 && task.Registers==0 && processes.Stops==0 && processes.Starts==0,"zero effects during explicit absence proof "+kind);
            if(!success)Check((late??before).SequenceEqual(File.ReadAllBytes(store.FilePath)),"refusal preserves original or external journal bytes "+kind);
            else{var now=store.Current!;var expected=JsonSerializer.Deserialize(before,RegisterRecoveryJson.Default.BrushJournal)!;expected.Completed=true;expected.Stage="Completed";expected.Outcome=now.Outcome;Check(BrushTyped(now)==BrushTyped(expected),"only completion stage/outcome changed "+kind);}
        }
        var accepted=ConfirmationIntent(new ConfirmationClock(),new FakeBrushTask());
        Check(!BrushTaskControls.CanConcludeAbsent(accepted),"accepted Run retains existing settlement path without absence shortcut");
        foreach(var pair in new[]{(BrushOperation.SetLogonEnabled,"Register"),(BrushOperation.Stop,"Prepared"),(BrushOperation.Start,"Stop"),(BrushOperation.Restart,"Register")})
        {var j=BrushIntent(pair.Item1);j.Stage=pair.Item2;Check(!BrushTaskControls.CanConcludeAbsent(j),"unrelated stage/operation not eligible "+pair);}
    }
    static ScriptEntry FixedBrush() => new() { Id=BrushTaskControls.RecordId, Path=BrushTaskControls.Script, Interpreter=BrushTaskControls.Interpreter, Description="Ajusta o pincel pela roda do mouse.", ManagementMode=ManagementMode.ExternalPending, StartWithWindows=false, RunAsAdministrator=true, LaunchBlocked=false };
    static BrushJournal BrushIntent(BrushOperation operation) => new() { Fixture=true, Nonce=Guid.NewGuid(), Operation=operation, RequestedLogon=operation==BrushOperation.SetLogonEnabled ? false : null, Sid="S-1-5-21-fixture", LoginKey="S-1-5-21-fixture|login1", ManagerPid=7100, ManagerCreated=100, Session=1, Executable=Environment.ProcessPath!, ExecutableHash=new('A',64), AssemblyHash=new('B',64), CatalogHash=new('C',64), LedgerHash=new('D',64), AnchorHash=new('E',64), AuthorityId=Guid.NewGuid(), DeadlineUtcTicks=DateTime.UtcNow.AddSeconds(90).Ticks };
    static ProcessIdentity BrushProcess(long created=100) => new(7101,created,BrushTaskControls.Interpreter,BrushTaskControls.Script,true);

    sealed class ConfirmationClock : IBrushObservationClock
    {
        public DateTime Origin = DateTime.UtcNow.AddSeconds(1);
        public int Millis;
        public Action? Waiting;
        public DateTime UtcNow => Origin.AddMilliseconds(Millis);
        public long Timestamp => Millis;
        public TimeSpan Elapsed(long start) => TimeSpan.FromMilliseconds(Millis-start);
        public Task Delay(CancellationToken token) { token.ThrowIfCancellationRequested(); Millis+=50; Waiting?.Invoke(); return Task.CompletedTask; }
    }
    sealed class ConfirmationProcesses(ConfirmationClock clock) : IProcessAccess
    {
        public bool Armed, Unknown, Duplicate, FailedProof, Reuse;
        public int Appears=43, Proofs, Stops, Starts;
        public bool? Elevated=true;
        public string Image=BrushTaskControls.Interpreter;
        public ProcessIdentity? Pinned { get; set; }
        public Action? Proving;
        public Task<Snapshot> Snapshot(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!Armed || clock.Millis<Appears)return Task.FromResult(new Snapshot([],true,"fake absent"));
            var p=Pinned ?? BrushProcess(clock.Origin.AddMilliseconds(Appears).ToFileTimeUtc());
            p=p with { Executable=Image, Elevated=Elevated };
            if(Reuse && Proofs>0)p=p with { Pid=p.Pid+1 };
            return Task.FromResult(new Snapshot(Duplicate?[p,p with { Pid=p.Pid+2 }]:[p],!Unknown,"fake observation"));
        }
        public Task<Result> Prove(ProcessIdentity p,CancellationToken token){token.ThrowIfCancellationRequested();Proofs++;Proving?.Invoke();return Task.FromResult(FailedProof?Result.Fail("fake held proof failed"):Result.Ok("same held fake object"));}
        public Result Preflight(ScriptEntry e)=>Result.Ok("unused");
        public Task<Result> Start(ScriptEntry e,CancellationToken token){Starts++;throw new InvalidOperationException("No direct launch");}
        public Task<Result> Stop(ProcessIdentity p,CancellationToken token){Stops++;throw new InvalidOperationException("No stop");}
    }
    static BrushJournal ConfirmationIntent(ConfirmationClock clock,FakeBrushTask task)
    {
        var j=BrushIntent(BrushOperation.Start);j.Version=2;j.Stage="Run";j.RunAuthorizedFileTime=clock.Origin.ToFileTimeUtc();j.RunBeganFileTime=clock.Origin.ToFileTimeUtc();j.RequestAccepted=true;j.Definition=task.State.Definition;j.Security=task.State.Security;return j;
    }
    static async Task RunBrushConfirmation()
    {
        foreach(string kind in new[]{"43ms","1900ms","2001ms","expired","cancel","exit","logon","unknown","duplicate","normal","wrong-image","reused","failed-proof"})
        {
            var clock=new ConfirmationClock();var task=new FakeBrushTask();var p=new ConfirmationProcesses(clock);var j=ConfirmationIntent(clock,task);int acks=0;bool alive=true;using var cancel=new CancellationTokenSource();
            if(kind=="1900ms")p.Appears=1900;if(kind=="2001ms")p.Appears=2001;if(kind=="expired")j.DeadlineUtcTicks=clock.Origin.AddMilliseconds(-1).Ticks;
            if(kind=="unknown")p.Unknown=true;if(kind=="duplicate")p.Duplicate=true;if(kind=="normal")p.Elevated=false;if(kind=="wrong-image")p.Image="wrong.exe";if(kind=="reused")p.Reuse=true;if(kind=="failed-proof")p.FailedProof=true;
            clock.Waiting=()=>{if(kind=="cancel")cancel.Cancel();if(kind=="exit")alive=false;if(kind=="logon")task.State=task.State with { LogonEnabled=false };};task.BeforeRun=()=>p.Armed=true;
            BrushReply? reply=null;try{reply=await BrushTaskControls.RunHelper(j,task,p,_=>{acks++;return Task.FromResult(Result.Ok("one Run authority"));},()=>alive,cancel.Token,clock);}catch(OperationCanceledException){}
            bool expected=kind is "43ms" or "1900ms";
            Check((reply?.Confirmed==true)==expected && task.Runs==(kind=="expired"?0:1) && acks==1 && p.Starts==0 && p.Stops==0 && task.Registers==0,"bounded "+kind+" confirms only safe proof after exactly one Run/ack and no other effects");
            Check(clock.Millis<=2000,"bounded monotonic duration "+kind);
            if(expected)Check(reply!.RunBeganFileTime==clock.Origin.ToFileTimeUtc() && reply.ConfirmedCreated==clock.Origin.AddMilliseconds(p.Appears).ToFileTimeUtc(),"typed exact Run/proof boundary "+kind);
        }
        foreach(string kind in new[]{"positive","missing-accepted","wrong-login","wrong-authority","task-drift","absent","failed-save"})
        {
            string dir=Path.Combine(root,"settle-"+kind);Directory.CreateDirectory(dir);var clock=new ConfirmationClock { Millis=50 };var task=new FakeBrushTask();var p=new ConfirmationProcesses(clock){Armed=true};var j=ConfirmationIntent(clock,task);if(kind=="missing-accepted")j.RequestAccepted=null;
            var operationStore=new BrushOperationStore(dir);operationStore.Save(j);var dispatcher=new FakeBrushDispatch(task,new());var controls=new BrushTaskControls(task,dispatcher,operationStore);byte[] before=File.ReadAllBytes(operationStore.FilePath);byte[]? late=null;
            if(kind=="task-drift")task.State=task.State with { LogonEnabled=false };if(kind=="absent")p.Armed=false;if(kind=="failed-save")p.Proving=()=>{File.AppendAllText(operationStore.FilePath," ");late=File.ReadAllBytes(operationStore.FilePath);};
            Result Verify(BrushJournal current)=>kind is "wrong-login" or "wrong-authority"?Result.Fail("changed binding"):Result.Ok("trusted current owner/profile fixture");
            BrushReply? reply=null;try{reply=await controls.Settle(Verify,p,CancellationToken.None,clock);}catch(IOException){}
            Check((reply?.Confirmed==true)==(kind=="positive") && dispatcher.Calls==0 && task.Runs==0 && task.Registers==0 && p.Starts==0 && p.Stops==0,"explicit read-only settlement "+kind+" never dispatches or changes task/process");
            Check(operationStore.Pending==(kind!="positive"),"durable pending truth "+kind);
            if(kind!="positive")Check((late??before).SequenceEqual(File.ReadAllBytes(operationStore.FilePath)),"refused settlement preserves bytes "+kind);
        }
        var legacyPath=Path.Combine(root,"legacy-original.json");File.WriteAllBytes(legacyPath,Convert.FromBase64String("eyJWZXJzaW9uIjoxLCJGaXh0dXJlIjpmYWxzZSwiTm9uY2UiOiJhNjdhMjJkMC05MzI2LTRkZGMtYmY2NS1lYWNlNzVmNDNmNDEiLCJPcGVyYXRpb24iOjEsIlJlcXVlc3RlZExvZ29uIjpudWxsLCJTaWQiOiJTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxIiwiTG9naW5LZXkiOiJTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxfDAwMDAwMDAwMDAwNUMyREMiLCJNYW5hZ2VyUGlkIjoyOTI4NCwiTWFuYWdlckNyZWF0ZWQiOjEzNDM1ODU2ODg3OTU0NjYzNSwiU2Vzc2lvbiI6MSwiRXhlY3V0YWJsZSI6IlY6XFxSZXBvc1xcQmV6b3JvLkFoa01hbmFnZXJcXHB1Ymxpc2hcXEJlem9yby5BaGtNYW5hZ2VyLmV4ZSIsIkV4ZWN1dGFibGVIYXNoIjoiREI3NEM0NzJFNzI2N0U5OTg4M0YyRUY5QjUyREU2QzBDMUNBQkVFQzQ5QkE2REFEN0FEQjA5OTI4MDkxRkU5MiIsIkFzc2VtYmx5SGFzaCI6IjEzNjY2RUI1NTY0OEZEM0RFRDA0ODUyRUM1RTQ4N0ZFNkIxQUM3MkY3MzgxM0JBRTRBMDE4NzBDQ0E5MzdEMjciLCJBbGxvd2VkTG9nb24iOnRydWUsIkNhdGFsb2dIYXNoIjoiRTREQ0FCRTIzN0E3RTVDODUyRTlFQkE2NEY1MDM4MkYxQ0Q3RTlBQ0E1QkY1NzQ2OUJEQUZDNkU0NjhCODEzMSIsIkxlZGdlckhhc2giOiI0QTUxRDhGNzlCN0VFN0ZDREY2NjNBOEQ1RDcwRjg2NUZFQTcxNkYxQUZBOUYzQzg0MUI4RDg1RDYyNzVFNjE0IiwiQW5jaG9ySGFzaCI6IjQ3RjdCMDZBMjYwNjhGMjJBMzA4MkYxNkFBRUYyODk4RjM4RjAyNzU4OEM1OTNDMjlGMTM4N0VFQUE2MTcwMkMiLCJBdXRob3JpdHlJZCI6IjFiYzdmN2Q4LWFmNGEtNDkzNi1iZGQ3LWE5NWMxNGZlZGIxMiIsIkRlZmluaXRpb24iOiJcclxuXHUwMDNDVGFzayB2ZXJzaW9uPVx1MDAyMjEuM1x1MDAyMiB4bWxucz1cdTAwMjJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFza1x1MDAyMlx1MDAzRVxyXG4gIFx1MDAzQ1JlZ2lzdHJhdGlvbkluZm9cdTAwM0VcclxuICAgIFx1MDAzQ0Rlc2NyaXB0aW9uXHUwMDNFUnVuIHRoZSBBdXRvSG90a2V5IHZvbHVtZS10by1icnVzaC1zaXplIHNjcmlwdCBlbGV2YXRlZCBhdCB1c2VyIGxvZ29uLlx1MDAzQy9EZXNjcmlwdGlvblx1MDAzRVxyXG4gICAgXHUwMDNDVVJJXHUwMDNFXFxBdXRvSG90a2V5XFxWb2x1bWUgdG8gQnJ1c2ggU2l6ZVx1MDAzQy9VUklcdTAwM0VcclxuICBcdTAwM0MvUmVnaXN0cmF0aW9uSW5mb1x1MDAzRVxyXG4gIFx1MDAzQ1ByaW5jaXBhbHNcdTAwM0VcclxuICAgIFx1MDAzQ1ByaW5jaXBhbCBpZD1cdTAwMjJBdXRob3JcdTAwMjJcdTAwM0VcclxuICAgICAgXHUwMDNDVXNlcklkXHUwMDNFUy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMVx1MDAzQy9Vc2VySWRcdTAwM0VcclxuICAgICAgXHUwMDNDTG9nb25UeXBlXHUwMDNFSW50ZXJhY3RpdmVUb2tlblx1MDAzQy9Mb2dvblR5cGVcdTAwM0VcclxuICAgICAgXHUwMDNDUnVuTGV2ZWxcdTAwM0VIaWdoZXN0QXZhaWxhYmxlXHUwMDNDL1J1bkxldmVsXHUwMDNFXHJcbiAgICBcdTAwM0MvUHJpbmNpcGFsXHUwMDNFXHJcbiAgXHUwMDNDL1ByaW5jaXBhbHNcdTAwM0VcclxuICBcdTAwM0NTZXR0aW5nc1x1MDAzRVxyXG4gICAgXHUwMDNDRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXNcdTAwM0VmYWxzZVx1MDAzQy9EaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllc1x1MDAzRVxyXG4gICAgXHUwMDNDU3RvcElmR29pbmdPbkJhdHRlcmllc1x1MDAzRWZhbHNlXHUwMDNDL1N0b3BJZkdvaW5nT25CYXR0ZXJpZXNcdTAwM0VcclxuICAgIFx1MDAzQ0V4ZWN1dGlvblRpbWVMaW1pdFx1MDAzRVBUMFNcdTAwM0MvRXhlY3V0aW9uVGltZUxpbWl0XHUwMDNFXHJcbiAgICBcdTAwM0NNdWx0aXBsZUluc3RhbmNlc1BvbGljeVx1MDAzRUlnbm9yZU5ld1x1MDAzQy9NdWx0aXBsZUluc3RhbmNlc1BvbGljeVx1MDAzRVxyXG4gICAgXHUwMDNDSWRsZVNldHRpbmdzXHUwMDNFXHJcbiAgICAgIFx1MDAzQ0R1cmF0aW9uXHUwMDNFUFQxME1cdTAwM0MvRHVyYXRpb25cdTAwM0VcclxuICAgICAgXHUwMDNDV2FpdFRpbWVvdXRcdTAwM0VQVDFIXHUwMDNDL1dhaXRUaW1lb3V0XHUwMDNFXHJcbiAgICAgIFx1MDAzQ1N0b3BPbklkbGVFbmRcdTAwM0V0cnVlXHUwMDNDL1N0b3BPbklkbGVFbmRcdTAwM0VcclxuICAgICAgXHUwMDNDUmVzdGFydE9uSWRsZVx1MDAzRWZhbHNlXHUwMDNDL1Jlc3RhcnRPbklkbGVcdTAwM0VcclxuICAgIFx1MDAzQy9JZGxlU2V0dGluZ3NcdTAwM0VcclxuICAgIFx1MDAzQ1VzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lXHUwMDNFdHJ1ZVx1MDAzQy9Vc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZVx1MDAzRVxyXG4gIFx1MDAzQy9TZXR0aW5nc1x1MDAzRVxyXG4gIFx1MDAzQ1RyaWdnZXJzXHUwMDNFXHJcbiAgICBcdTAwM0NMb2dvblRyaWdnZXJcdTAwM0VcclxuICAgICAgXHUwMDNDVXNlcklkXHUwMDNFQkVaT1JPXFxCZXpvclx1MDAzQy9Vc2VySWRcdTAwM0VcclxuICAgIFx1MDAzQy9Mb2dvblRyaWdnZXJcdTAwM0VcclxuICBcdTAwM0MvVHJpZ2dlcnNcdTAwM0VcclxuICBcdTAwM0NBY3Rpb25zIENvbnRleHQ9XHUwMDIyQXV0aG9yXHUwMDIyXHUwMDNFXHJcbiAgICBcdTAwM0NFeGVjXHUwMDNFXHJcbiAgICAgIFx1MDAzQ0NvbW1hbmRcdTAwM0VDOlxcUHJvZ3JhbSBGaWxlc1xcQXV0b0hvdGtleVxcdjJcXEF1dG9Ib3RrZXk2NC5leGVcdTAwM0MvQ29tbWFuZFx1MDAzRVxyXG4gICAgICBcdTAwM0NBcmd1bWVudHNcdTAwM0VcdTAwMjJDOlxcVXNlcnNcXEJlem9yXFxEb2N1bWVudHNcXEF1dG9Ib3RrZXlcXHNjcmlwdHNcXGVsZXZhdGVkXFxhaGtfdm9sdW1lLXRvLWJydXNoLXNpemUuYWhrXHUwMDIyXHUwMDNDL0FyZ3VtZW50c1x1MDAzRVxyXG4gICAgICBcdTAwM0NXb3JraW5nRGlyZWN0b3J5XHUwMDNFQzpcXFVzZXJzXFxCZXpvclxcRG9jdW1lbnRzXFxBdXRvSG90a2V5XFxzY3JpcHRzXFxlbGV2YXRlZFx1MDAzQy9Xb3JraW5nRGlyZWN0b3J5XHUwMDNFXHJcbiAgICBcdTAwM0MvRXhlY1x1MDAzRVxyXG4gIFx1MDAzQy9BY3Rpb25zXHUwMDNFXHJcblx1MDAzQy9UYXNrXHUwMDNFIiwiU2VjdXJpdHkiOiJPOkJBRzpTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxRDooQTtJRDsweDFmMDE5Zjs7O0JBKShBO0lEOzB4MWYwMTlmOzs7U1kpKEE7SUQ7RkE7OztCQSkoQTs7RlI7OztTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxKSIsIlN0YWdlIjoiUnVuIiwiQ29tcGxldGVkIjpmYWxzZSwiT3V0Y29tZSI6IlBlZGlkbyBhY2VpdG8gcGVsYSB0YXJlZmE7IGV4ZWN1Y2FvIG5hbyBjb25maXJtYWRhLiBOYW8gcmVwZXRpci4iLCJEZWFkbGluZVV0Y1RpY2tzIjo2MzkyNjk4MDM1OTcxODk3MDB9"));var legacy=BrushOperationStore.Read(legacyPath);
        Check(BrushTaskControls.LegacyEligible(legacy,BrushTaskControls.LegacyIncidentHash),"only pinned original v1 writer/bytes incident recognized");
        foreach(string kind in new[]{"nonce","graph","digest","text"})
        {var changed=BrushOperationStore.Read(legacyPath);string digest=BrushTaskControls.LegacyIncidentHash;if(kind=="nonce")changed.Nonce=Guid.NewGuid();if(kind=="graph")changed.AssemblyHash=new('A',64);if(kind=="digest")digest=new('B',64);if(kind=="text")changed.Outcome="accepted";Check(!BrushTaskControls.LegacyEligible(changed,digest),"legacy insufficient "+kind+" refuses generic clearing");}
        var compact = new BrushReply(true,false,false,true,"Processo Brush elevado confirmado; gatilho de login preservado.",134358570717049438,50148,134358570717549438);
        Check(JsonSerializer.SerializeToUtf8Bytes(compact,ConfirmationJson.Default.BrushReply).Length<=490,"typed reply remains within unchanged helper frame payload");
        await ConfirmationUi();
    }
    sealed class ReadOnlySettlementDispatch : IBrushDispatcher
    {
        public bool Fixture => false;
        public int Calls;
        public Task<BrushReply> Dispatch(BrushJournal j,Func<string,Result> authorize,CancellationToken token) { Calls++;throw new InvalidOperationException("No legacy dispatch"); }
    }
    static async Task RunBrushConfirmationLegacy()
    {
        string original=Path.Combine(Path.GetDirectoryName(root)!,"PENDING-ORIGINAL.json");
        foreach(string kind in new[]{"positive","identity","elevation","creation","task-drift","authority","late-save"})
        {
            string dir=Path.Combine(root,"legacy-"+kind);Directory.CreateDirectory(dir);File.Copy(original,Path.Combine(dir,"brush-operation.json"));var operationStore=new BrushOperationStore(dir);var j=operationStore.Current!;var task=new FakeBrushTask();task.State=task.State with { Definition=j.Definition,Security=j.Security,LogonEnabled=j.AllowedLogon };var clock=new ConfirmationClock { Millis=50 };var target=new ProcessIdentity(50148,134358570717049438,BrushTaskControls.Interpreter,BrushTaskControls.Script,true);if(kind=="identity")target=target with { Pid=50149 };if(kind=="creation")target=target with { Created=target.Created+1 };var p=new ConfirmationProcesses(clock) { Armed=true,Pinned=target,Elevated=kind=="elevation"?false:true };var dispatch=new ReadOnlySettlementDispatch();var controls=new BrushTaskControls(task,dispatch,operationStore);byte[] before=File.ReadAllBytes(operationStore.FilePath);byte[]? late=null;
            if(kind=="task-drift")task.State=task.State with { LogonEnabled=false };if(kind=="late-save")p.Proving=()=>{File.AppendAllText(operationStore.FilePath," ");late=File.ReadAllBytes(operationStore.FilePath);};
            BrushReply? reply=null;try{reply=await controls.Settle(_=>kind=="authority"?Result.Fail("changed current graph"):Result.Ok("same legitimate current owner/profile graph"),p,CancellationToken.None,clock);}catch(IOException){}
            Check((reply?.Confirmed==true)==(kind=="positive") && operationStore.Pending==(kind!="positive"),"exact original legacy incident settlement "+kind);
            Check(dispatch.Calls==0 && task.Runs==0 && task.Registers==0 && p.Starts==0 && p.Stops==0,"legacy "+kind+" has zero dispatch/task/process effects");
            if(kind!="positive")Check((late??before).SequenceEqual(File.ReadAllBytes(operationStore.FilePath)),"legacy "+kind+" preserves refused journal bytes");
            else Check(operationStore.Current is { Completed:true,Version:1,ManagerPid:29284 } && operationStore.Current.AssemblyHash==j.AssemblyHash,"legacy completion preserves original effect bindings");
        }
        foreach(string kind in new[]{"accepted-no-run","before-authorization","after-deadline","proof-without-acceptance","invalid-version"})
        {
            var clock=new ConfirmationClock();var j=ConfirmationIntent(clock,new FakeBrushTask());if(kind=="accepted-no-run")j.RunBeganFileTime=null;if(kind=="before-authorization")j.RunBeganFileTime=j.RunAuthorizedFileTime-1;if(kind=="after-deadline")j.RunBeganFileTime=new DateTime(j.DeadlineUtcTicks,DateTimeKind.Utc).ToFileTimeUtc()+1;if(kind=="proof-without-acceptance"){j.RequestAccepted=false;j.ConfirmedPid=7101;j.ConfirmedCreated=j.RunBeganFileTime;}if(kind=="invalid-version")j.Version=1;bool refused=false;try{BrushOperationStore.ValidateRunEvidence(j);}catch(InvalidDataException){refused=true;}Check(refused,"strict typed Run evidence refuses "+kind);
        }
    }
    static async Task RunBrushConfirmationFinalRead()
    {
        foreach(string kind in new[]{"operation","settlement"})
        {
            string dir=Path.Combine(root,kind);Directory.CreateDirectory(dir);using var store=new CatalogStore(dir);var task=new FakeBrushTask();var p=new FixtureProcesses();var dispatch=new FakeBrushDispatch(task,p);var operationStore=new BrushOperationStore(dir);var controls=new BrushTaskControls(task,dispatch,operationStore);var owner=new FixtureOwner();var interaction=new FixtureInteraction { Confirmed=true };var authority=RequestLedger.OpenFixture(Path.Combine(dir,"authority"),owner,store.FilePath);var entry=FixedBrush();store.Save(new Catalog { Authority=authority.Binding,Scripts=[entry] });var session=new UiSession(store,p,new FixtureRecycler(),interaction,()=>Task.FromResult(new DiscoveryResult([],[],[])),owner,new FixtureClock(),authority,brush:controls);byte[] catalog=File.ReadAllBytes(store.FilePath),ledger=File.ReadAllBytes(authority.FilePath);
            p.BeforeSnapshot=()=>{if(operationStore.Current is { Completed:true })p.ThrowSnapshot=true;};
            if(kind=="operation")p.Current=new([BrushProcess()],true,"old verified instance");await session.Refresh();
            if(kind=="operation")await session.Act(ScriptAction.Stop);
            else { await session.Act(ScriptAction.Start);var pending=operationStore.Current!;p.Current=new([BrushProcess(pending.RunBeganFileTime!.Value+10000)],true,"new proved Brush");await session.InspectBrush(); }
            var committed=operationStore.Current!;Check(committed.Completed && !controls.Pending && !new BrushOperationStore(dir).Pending,"throwing final read preserves durable "+kind+" completion truth");
            Check(session.ObservationStale && !session.Snapshot.Complete && session.Error && session.Status.Contains(committed.Outcome) && session.Status.Contains("observação anterior"),"throwing final read marks retained "+kind+" observation stale and preserves result status");
            Check(dispatch.Calls==1 && task.Runs==(kind=="operation"?0:1) && p.Stops==(kind=="operation"?1:0) && p.Starts==0 && task.Registers==0,"throwing final "+kind+" read never redispatches or performs additional effects");
            Check(catalog.SequenceEqual(File.ReadAllBytes(store.FilePath)) && ledger.SequenceEqual(File.ReadAllBytes(authority.FilePath)),"throwing final "+kind+" read preserves catalog and launch ledger");
        }
    }
    static async Task ConfirmationUi()
    {
        string dir=Path.Combine(root,"verify-ui");Directory.CreateDirectory(dir);using var store=new CatalogStore(dir);var task=new FakeBrushTask();var process=new FixtureProcesses();var dispatch=new FakeBrushDispatch(task,process);var operationStore=new BrushOperationStore(dir);var controls=new BrushTaskControls(task,dispatch,operationStore);var owner=new FixtureOwner();var interaction=new FixtureInteraction { Confirmed=true };var authority=RequestLedger.OpenFixture(Path.Combine(dir,"authority"),owner,store.FilePath);var entry=FixedBrush();store.Save(new Catalog { Authority=authority.Binding,Scripts=[entry] });
        var session=new UiSession(store,process,new FixtureRecycler(),interaction,()=>Task.FromResult(new DiscoveryResult([],[],[])),owner,new FixtureClock(),authority,brush:controls);await session.Refresh();var window=new MainWindow(session);await session.Act(ScriptAction.Start);
        Check(controls.Pending && session.Error && !session.ObservationStale && session.Snapshot.Complete,"fresh complete UI snapshot remains fresh while accepted request pending");
        Check(window.VerifyRequestControl.IsEnabled && ((FrameworkElement)window.FindName("RequestPanel")).Visibility==Visibility.Visible && !((Button)window.FindName("RecoverRequestButton")).IsEnabled,"existing Verify panel reachable for Brush; recovery stays launch-only");
        var j=operationStore.Current!;process.Current=new([BrushProcess(j.RunBeganFileTime!.Value+10000)],true,"fresh same Brush");int calls=dispatch.Calls;var catalog=File.ReadAllBytes(store.FilePath);Click(window.VerifyRequestControl);await session.WhenIdle;
        Check(!controls.Pending && dispatch.Calls==calls && process.Starts==0 && process.Stops==0 && task.Registers==0 && !session.Error && !session.ObservationStale,"actual existing Verify click settles once without another UAC/Run/Stop/Register");
        Check(catalog.SequenceEqual(File.ReadAllBytes(store.FilePath)) && ((FrameworkElement)window.FindName("RequestPanel")).Visibility==Visibility.Collapsed,"explicit settlement preserves catalog and hides completed pending panel");window.Close();
    }

    sealed class RegisterXmlFixture(string xml) : IBrushTask
    {
        public WindowsBrushTask.RegisteredXml Current=new(xml,WindowsBrushTask.CapturedSecurity,true);
        public WindowsBrushTask.XmlRegistration? Last;
        public int Reads, Registers, Runs;
        public bool ThrowRegister;
        public Action<int>? Reading;
        public Action? Registering;
        public Func<Result> Gate=()=>Result.Ok("fixture owner authenticated");
        public BrushProbe Probe()=>WindowsBrushTask.Validate(Current.Xml,Current.Security,Current.Enabled);
        WindowsBrushTask.RegisteredXml Read(){Reading?.Invoke(++Reads);return Current;}
        public Result Run(int session){Runs++;throw new InvalidOperationException("No Run in registration fixtures");}
        public Result SetLogon(bool enabled)=>WindowsBrushTask.UpdateRegisteredLogon(enabled,Read,Gate,input=>
        {
            Registers++;Last=input;if(ThrowRegister)throw new System.Runtime.InteropServices.COMException("fake registration refusal");
            Current=Current with { Xml=input.Xml };Registering?.Invoke();return Current;
        });
    }
    sealed class RegisterXmlDispatch(RegisterXmlFixture task) : IBrushDispatcher
    {
        public bool Fixture=>true;
        public int Calls;
        public Task<BrushReply> Dispatch(BrushJournal j,Func<string,Result> authorize,CancellationToken token)
        {Calls++;return BrushTaskControls.RunHelper(j,task,new FixtureProcesses(),stage=>Task.FromResult(authorize(stage)),()=>true,token);}
    }
    sealed class RegisterRecoveryWitness(string raw, string expanded) : IBrushTask, IBrushRegisterRefusalWitness
    {
        public string Raw=raw, Expanded=expanded, Security=WindowsBrushTask.CapturedSecurity;
        public bool Enabled=true;
        public int Calls,Effects;
        public Action? Witnessing;
        public BrushProbe Probe()=>WindowsBrushTask.Validate(Raw,Security,Enabled);
        public bool ProvesRegisterRefusal(BrushJournal j){Calls++;Witnessing?.Invoke();return WindowsBrushTask.MatchRegisterRefusal(j,new(Raw,Security,Enabled),Expanded);}
        public Result Run(int session){Effects++;throw new InvalidOperationException("No Run");}
        public Result SetLogon(bool enabled){Effects++;throw new InvalidOperationException("No Register");}
    }
    sealed class SchedulerSerializedBrush(string originalOn,string capturedOff) : IBrushTask
    {
        readonly string onXml=originalOn;
        public string Xml=originalOn,Security=WindowsBrushTask.CapturedSecurity;
        public string? OnResponse;
        public bool Enabled=true,FailGate;
        public int Registers,Runs;
        public WindowsBrushTask.XmlRegistration? Last;
        public Action? Registered;
        public BrushProbe Probe()=>WindowsBrushTask.Validate(Xml,Security,Enabled);
        public Result Run(int session){Runs++;throw new InvalidOperationException("No Run");}
        public Result SetLogon(bool enabled)=>WindowsBrushTask.UpdateRegisteredLogon(enabled,()=>new(Xml,Security,Enabled),()=>FailGate?Result.Fail("fake owner refused"):Result.Ok("fixture owner"),input=>
        { Registers++;Last=input;Xml=enabled?(OnResponse??onXml):capturedOff;Registered?.Invoke();return new(Xml,Security,Enabled); });
    }
    sealed class SchedulerSerializedDispatch(SchedulerSerializedBrush task) : IBrushDispatcher
    {
        public bool Fixture=>true;
        public int Calls;
        public Task<BrushReply> Dispatch(BrushJournal j,Func<string,Result> authorize,CancellationToken token)
        {Calls++;return BrushTaskControls.RunHelper(j,task,new FixtureProcesses(),stage=>Task.FromResult(authorize(stage)),()=>true,token);}
    }
    static async Task RunBrushRegisterFormatting(bool settlementOnly = false)
    {
        string on=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPE11bHRpcGxlSW5zdGFuY2VzUG9saWN5Pklnbm9yZU5ldzwvTXVsdGlwbGVJbnN0YW5jZXNQb2xpY3k+DQogICAgPElkbGVTZXR0aW5ncz4NCiAgICAgIDxEdXJhdGlvbj5QVDEwTTwvRHVyYXRpb24+DQogICAgICA8V2FpdFRpbWVvdXQ+UFQxSDwvV2FpdFRpbWVvdXQ+DQogICAgICA8U3RvcE9uSWRsZUVuZD50cnVlPC9TdG9wT25JZGxlRW5kPg0KICAgICAgPFJlc3RhcnRPbklkbGU+ZmFsc2U8L1Jlc3RhcnRPbklkbGU+DQogICAgPC9JZGxlU2V0dGluZ3M+DQogICAgPFVzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPnRydWU8L1VzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPg0KICA8L1NldHRpbmdzPg0KICA8VHJpZ2dlcnM+DQogICAgPExvZ29uVHJpZ2dlcj4NCiAgICAgIDxVc2VySWQ+QkVaT1JPXEJlem9yPC9Vc2VySWQ+DQogICAgPC9Mb2dvblRyaWdnZXI+DQogIDwvVHJpZ2dlcnM+DQogIDxBY3Rpb25zIENvbnRleHQ9IkF1dGhvciI+DQogICAgPEV4ZWM+DQogICAgICA8Q29tbWFuZD5DOlxQcm9ncmFtIEZpbGVzXEF1dG9Ib3RrZXlcdjJcQXV0b0hvdGtleTY0LmV4ZTwvQ29tbWFuZD4NCiAgICAgIDxBcmd1bWVudHM+IkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWRcYWhrX3ZvbHVtZS10by1icnVzaC1zaXplLmFoayI8L0FyZ3VtZW50cz4NCiAgICAgIDxXb3JraW5nRGlyZWN0b3J5PkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWQ8L1dvcmtpbmdEaXJlY3Rvcnk+DQogICAgPC9FeGVjPg0KICA8L0FjdGlvbnM+DQo8L1Rhc2s+")),off=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPE11bHRpcGxlSW5zdGFuY2VzUG9saWN5Pklnbm9yZU5ldzwvTXVsdGlwbGVJbnN0YW5jZXNQb2xpY3k+DQogICAgPElkbGVTZXR0aW5ncz4NCiAgICAgIDxEdXJhdGlvbj5QVDEwTTwvRHVyYXRpb24+DQogICAgICA8V2FpdFRpbWVvdXQ+UFQxSDwvV2FpdFRpbWVvdXQ+DQogICAgICA8U3RvcE9uSWRsZUVuZD50cnVlPC9TdG9wT25JZGxlRW5kPg0KICAgICAgPFJlc3RhcnRPbklkbGU+ZmFsc2U8L1Jlc3RhcnRPbklkbGU+DQogICAgPC9JZGxlU2V0dGluZ3M+DQogICAgPFVzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPnRydWU8L1VzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPg0KICA8L1NldHRpbmdzPg0KICA8VHJpZ2dlcnM+DQogICAgPExvZ29uVHJpZ2dlcj4NCiAgICAgIDxFbmFibGVkPmZhbHNlPC9FbmFibGVkPg0KICAgICAgPFVzZXJJZD5CRVpPUk9cQmV6b3I8L1VzZXJJZD4NCiAgICA8L0xvZ29uVHJpZ2dlcj4NCiAgPC9UcmlnZ2Vycz4NCiAgPEFjdGlvbnMgQ29udGV4dD0iQXV0aG9yIj4NCiAgICA8RXhlYz4NCiAgICAgIDxDb21tYW5kPkM6XFByb2dyYW0gRmlsZXNcQXV0b0hvdGtleVx2MlxBdXRvSG90a2V5NjQuZXhlPC9Db21tYW5kPg0KICAgICAgPEFyZ3VtZW50cz4iQzpcVXNlcnNcQmV6b3JcRG9jdW1lbnRzXEF1dG9Ib3RrZXlcc2NyaXB0c1xlbGV2YXRlZFxhaGtfdm9sdW1lLXRvLWJydXNoLXNpemUuYWhrIjwvQXJndW1lbnRzPg0KICAgICAgPFdvcmtpbmdEaXJlY3Rvcnk+QzpcVXNlcnNcQmV6b3JcRG9jdW1lbnRzXEF1dG9Ib3RrZXlcc2NyaXB0c1xlbGV2YXRlZDwvV29ya2luZ0RpcmVjdG9yeT4NCiAgICA8L0V4ZWM+DQogIDwvQWN0aW9ucz4NCjwvVGFzaz4=")),expanded=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFRyaWdnZXJzPg0KICAgIDxMb2dvblRyaWdnZXI+DQogICAgICA8RW5hYmxlZD50cnVlPC9FbmFibGVkPg0KICAgICAgPFVzZXJJZD5CRVpPUk9cQmV6b3I8L1VzZXJJZD4NCiAgICA8L0xvZ29uVHJpZ2dlcj4NCiAgPC9UcmlnZ2Vycz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxNdWx0aXBsZUluc3RhbmNlc1BvbGljeT5JZ25vcmVOZXc8L011bHRpcGxlSW5zdGFuY2VzUG9saWN5Pg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEFsbG93SGFyZFRlcm1pbmF0ZT50cnVlPC9BbGxvd0hhcmRUZXJtaW5hdGU+DQogICAgPFN0YXJ0V2hlbkF2YWlsYWJsZT5mYWxzZTwvU3RhcnRXaGVuQXZhaWxhYmxlPg0KICAgIDxSdW5Pbmx5SWZOZXR3b3JrQXZhaWxhYmxlPmZhbHNlPC9SdW5Pbmx5SWZOZXR3b3JrQXZhaWxhYmxlPg0KICAgIDxJZGxlU2V0dGluZ3M+DQogICAgICA8RHVyYXRpb24+UFQxME08L0R1cmF0aW9uPg0KICAgICAgPFdhaXRUaW1lb3V0PlBUMUg8L1dhaXRUaW1lb3V0Pg0KICAgICAgPFN0b3BPbklkbGVFbmQ+dHJ1ZTwvU3RvcE9uSWRsZUVuZD4NCiAgICAgIDxSZXN0YXJ0T25JZGxlPmZhbHNlPC9SZXN0YXJ0T25JZGxlPg0KICAgIDwvSWRsZVNldHRpbmdzPg0KICAgIDxBbGxvd1N0YXJ0T25EZW1hbmQ+dHJ1ZTwvQWxsb3dTdGFydE9uRGVtYW5kPg0KICAgIDxFbmFibGVkPnRydWU8L0VuYWJsZWQ+DQogICAgPEhpZGRlbj5mYWxzZTwvSGlkZGVuPg0KICAgIDxSdW5Pbmx5SWZJZGxlPmZhbHNlPC9SdW5Pbmx5SWZJZGxlPg0KICAgIDxEaXNhbGxvd1N0YXJ0T25SZW1vdGVBcHBTZXNzaW9uPmZhbHNlPC9EaXNhbGxvd1N0YXJ0T25SZW1vdGVBcHBTZXNzaW9uPg0KICAgIDxVc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZT50cnVlPC9Vc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZT4NCiAgICA8V2FrZVRvUnVuPmZhbHNlPC9XYWtlVG9SdW4+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPFByaW9yaXR5Pjc8L1ByaW9yaXR5Pg0KICA8L1NldHRpbmdzPg0KICA8QWN0aW9ucyBDb250ZXh0PSJBdXRob3IiPg0KICAgIDxFeGVjPg0KICAgICAgPENvbW1hbmQ+QzpcUHJvZ3JhbSBGaWxlc1xBdXRvSG90a2V5XHYyXEF1dG9Ib3RrZXk2NC5leGU8L0NvbW1hbmQ+DQogICAgICA8QXJndW1lbnRzPiJDOlxVc2Vyc1xCZXpvclxEb2N1bWVudHNcQXV0b0hvdGtleVxzY3JpcHRzXGVsZXZhdGVkXGFoa192b2x1bWUtdG8tYnJ1c2gtc2l6ZS5haGsiPC9Bcmd1bWVudHM+DQogICAgICA8V29ya2luZ0RpcmVjdG9yeT5DOlxVc2Vyc1xCZXpvclxEb2N1bWVudHNcQXV0b0hvdGtleVxzY3JpcHRzXGVsZXZhdGVkPC9Xb3JraW5nRGlyZWN0b3J5Pg0KICAgIDwvRXhlYz4NCiAgPC9BY3Rpb25zPg0KPC9UYXNrPg=="));
        if (!settlementOnly)
        {
        Check(WindowsBrushTask.LegacyRegisterRefusalCanonical(off)!=WindowsBrushTask.LegacyRegisterRefusalCanonical(on) && WindowsBrushTask.Canonical(off)==WindowsBrushTask.Canonical(on),"actual captured serializer OFF/ON reproduces old formatting refusal and corrected canonical equality");
        Check(WindowsBrushTask.Canonical(WindowsBrushTask.WithLogonEnabled(on,false))==WindowsBrushTask.Canonical(on),"inline prepared OFF retains its only indentation; corrected pregate succeeds");
        var derivedDoc=WindowsBrushTask.Parse(off);System.Xml.Linq.XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";derivedDoc.Root!.Element(ns+"Triggers")!.Elements().Single().Element(ns+"Enabled")!.Value="true";string derived=derivedDoc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        foreach(var pair in new[]{(Name:"captured original ON absent/default",Xml:on,Enabled:true),(Name:"captured actual OFF",Xml:off,Enabled:false),(Name:"DERIVED explicit true from captured OFF",Xml:derived,Enabled:true)})
        {var probe=WindowsBrushTask.Validate(pair.Xml,WindowsBrushTask.CapturedSecurity,true);Check(probe.Ready && probe.LogonEnabled==pair.Enabled && probe.Definition==WindowsBrushTask.Canonical(on),"strict serializer shape "+pair.Name);}
        foreach(string kind in new[]{"comment","nonblank-before","nonblank-after","unknown","default","setting","reorder","security"})
        {
            string xml=kind switch{"comment"=>off.Replace("<Enabled>false</Enabled>","<!--keep--><Enabled>false</Enabled>"),"nonblank-before"=>off.Replace("<Enabled>false</Enabled>","marker<Enabled>false</Enabled>"),"nonblank-after"=>off.Replace("</Enabled>","</Enabled>marker"),"unknown"=>off.Replace("<Enabled>false</Enabled>","<Other/><Enabled>false</Enabled>"),"default"=>off.Replace("<Settings>","<Settings><Hidden>false</Hidden>"),"setting"=>off.Replace("IgnoreNew","Parallel"),_=>off};
            if(kind=="reorder"){var d=WindowsBrushTask.Parse(xml);var trigger=d.Root!.Element(ns+"Triggers")!;trigger.Remove();d.Root.Element(ns+"Principals")!.AddBeforeSelf(trigger);xml=d.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);}
            Check(!WindowsBrushTask.Validate(xml,WindowsBrushTask.CapturedSecurity+(kind=="security"?"x":""),true).Known,"canonical keeps semantic/other-structure refusal "+kind);
            if(kind=="comment")Check(WindowsBrushTask.Canonical(xml).Contains("<!--keep-->"),"canonical retains comments");if(kind.StartsWith("nonblank"))Check(WindowsBrushTask.Canonical(xml).Contains("marker"),"canonical retains nonblank text "+kind);
        }
        var c7Path=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!,"brush-register-recovery","ORIGINAL-REGISTER-PENDING.json");var c7=BrushOperationStore.Read(c7Path);
        Check(WindowsBrushTask.MatchRegisterRefusal(c7,new(on,WindowsBrushTask.CapturedSecurity,true),expanded),"historical exact C7 witness still matches its old canonical digest/pregate semantics");
        string roundDir=Path.Combine(root,"captured-roundtrip");Directory.CreateDirectory(roundDir);var task=new SchedulerSerializedBrush(on,off);var dispatch=new SchedulerSerializedDispatch(task);var store=new BrushOperationStore(roundDir);var controls=new BrushTaskControls(task,dispatch,store);
        var down=await controls.Execute(BrushIntent(BrushOperation.SetLogonEnabled),()=>Result.Ok("fake owner/profile"),CancellationToken.None);
        Check(down.Confirmed && down.StartupChanged && task.Xml==off && store.Current is { Completed:true,AllowedLogon:false } && !controls.Pending && task.Registers==1,"E2E production/coordinator OFF returns ACTUAL captured scheduler OFF and confirms durable false");
        var args=task.Last!;Check(args.Path=="Volume to Brush Size" && args.Flags==0x34 && args.User is null && args.Password is null && args.Logon==3 && args.Sddl is null,"roundtrip registration arguments remain exact fixed typed update/null-security contract");
        var upIntent=BrushIntent(BrushOperation.SetLogonEnabled);upIntent.RequestedLogon=true;var up=await controls.Execute(upIntent,()=>Result.Ok("fresh fake owner/profile"),CancellationToken.None);
        Check(up.Confirmed && up.StartupChanged && task.Xml==on && store.Current is { Completed:true,AllowedLogon:true } && !controls.Pending && task.Registers==2 && dispatch.Calls==2 && task.Runs==0,"E2E production/coordinator restore ON returns ACTUAL original absent/defaulttrue and confirms durable true");
        var derivedTask=new SchedulerSerializedBrush(on,off){Xml=off,OnResponse=derived};Check(derivedTask.SetLogon(true).Success && derivedTask.Xml==derived && derivedTask.Registers==1,"DERIVED explicit true roundtrip is accepted and clearly separate from actual original ON");
        foreach(string kind in new[]{"owner","post-security","post-setting","post-login"})
        {var f=new SchedulerSerializedBrush(on,off){FailGate=kind=="owner"};f.Registered=()=>{if(kind=="post-security")f.Security+="x";if(kind=="post-setting")f.Xml=f.Xml.Replace("IgnoreNew","Parallel");if(kind=="post-login")f.Xml=on;};Check(!f.SetLogon(false).Success && f.Registers==(kind=="owner"?0:1),"formatting fix preserves effect/postguard refusal "+kind);}
        }
        string original=Path.Combine(Path.GetDirectoryName(root)!,"ORIGINAL-POSTREGISTER-PENDING.json");var exact=BrushOperationStore.Read(original);
        Check(BrushTaskControls.PostRegisterIncidentEligible(exact,BrushTaskControls.PostRegisterIncidentHash),"exact original raw+typed490 postRegister digest reproducible unchanged");
        foreach(string kind in new[]{"positive","nonce","hash","typed","task-on","security","definition","owner","login-key","unknown","duplicate","replacement","unproven","elevation","cancel","timeout","late-owner","late-task","late-journal","save-failure"})
        {
            string dir=Path.Combine(root,"settle-"+kind);Directory.CreateDirectory(dir);File.Copy(original,Path.Combine(dir,"brush-operation.json"));var operationStore=new BrushOperationStore(dir);var j=operationStore.Current!;var currentTask=new SchedulerSerializedBrush(on,off){Xml=off};var clock=new ConfirmationClock{Millis=50};var p=new ConfirmationProcesses(clock){Armed=true,Pinned=new ProcessIdentity(50148,134358570717049438,BrushTaskControls.Interpreter,BrushTaskControls.Script,true)};var noEffects=new ReadOnlySettlementDispatch();var recovery=new BrushTaskControls(currentTask,noEffects,operationStore);using var cancel=new CancellationTokenSource();bool current=true;byte[] before=File.ReadAllBytes(operationStore.FilePath);byte[]? late=null;
            if(kind is "nonce" or "hash" or "typed"){var altered=BrushOperationStore.Read(original);string digest=BrushTaskControls.PostRegisterIncidentHash;if(kind=="nonce")altered.Nonce=Guid.NewGuid();if(kind=="hash")digest=new('A',64);if(kind=="typed")altered.Outcome+=" ";Check(!BrushTaskControls.PostRegisterIncidentEligible(altered,digest),"postRegister exact pin rejects "+kind);current=false;}
            if(kind=="task-on")currentTask.Xml=on;if(kind=="security")currentTask.Security+="x";if(kind=="definition")currentTask.Xml=off.Replace("IgnoreNew","Parallel");if(kind is "owner" or "login-key")current=false;if(kind=="unknown")p.Unknown=true;if(kind=="duplicate")p.Duplicate=true;if(kind=="replacement")p.Reuse=true;if(kind=="unproven")p.FailedProof=true;if(kind=="elevation")p.Elevated=false;if(kind=="cancel")cancel.Cancel();if(kind=="timeout")p.Proving=()=>clock.Millis=2100;if(kind=="late-owner")p.Proving=()=>current=false;if(kind=="late-task")p.Proving=()=>currentTask.Xml=on;if(kind=="late-journal")p.Proving=()=>{File.AppendAllText(operationStore.FilePath," ");late=File.ReadAllBytes(operationStore.FilePath);};if(kind=="save-failure")File.WriteAllText(operationStore.FilePath+".tmp","unrelated temp");
            BrushReply? reply=null;try{reply=await recovery.Settle(_=>current?Result.Ok("current legitimate owner/profile graph"):Result.Fail("binding changed"),p,cancel.Token,clock);}catch(IOException){}catch(OperationCanceledException){}
            Check((reply?.Confirmed==true)==(kind=="positive") && operationStore.Pending==(kind!="positive"),"exact OFF readonly settlement "+kind+" durable truth");Check(noEffects.Calls==0 && currentTask.Registers==0 && currentTask.Runs==0 && p.Starts==0 && p.Stops==0,"OFF settlement "+kind+" has zero new effect dispatch");
            if(kind=="positive")
            {var finished=operationStore.Current!;var expected=BrushOperationStore.Read(original);expected.Completed=true;expected.Stage=finished.Stage;expected.Outcome=finished.Outcome;expected.AllowedLogon=false;Check(JsonSerializer.Serialize(finished,RegisterRecoveryJson.Default.BrushJournal)==JsonSerializer.Serialize(expected,RegisterRecoveryJson.Default.BrushJournal) && reply!.StartupChanged && finished.RequestedLogon==false,"OFF settlement only Completed/Stage/Outcome/Allowedfalse change and existing result reports startup confirmation");var display=UiSession.BrushInspectionPresentation(reply!);Check(!display.Error && display.Status.Contains("OFF original confirmado") && recovery.Available(FixedBrush(),BrushOperation.SetLogonEnabled,new([p.Pinned!],true,"fake known")).Success,"existing Inspect presentation truth confirms OFF and permits independently authorized restore action");}
            else Check((late??before).SequenceEqual(File.ReadAllBytes(operationStore.FilePath)),"OFF refusal "+kind+" preserves exact pending bytes");
        }
    }
    static async Task RunBrushRegisterRecovery()
    {
        string original=Path.Combine(Path.GetDirectoryName(root)!,"ORIGINAL-REGISTER-PENDING.json");string raw=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPE11bHRpcGxlSW5zdGFuY2VzUG9saWN5Pklnbm9yZU5ldzwvTXVsdGlwbGVJbnN0YW5jZXNQb2xpY3k+DQogICAgPElkbGVTZXR0aW5ncz4NCiAgICAgIDxEdXJhdGlvbj5QVDEwTTwvRHVyYXRpb24+DQogICAgICA8V2FpdFRpbWVvdXQ+UFQxSDwvV2FpdFRpbWVvdXQ+DQogICAgICA8U3RvcE9uSWRsZUVuZD50cnVlPC9TdG9wT25JZGxlRW5kPg0KICAgICAgPFJlc3RhcnRPbklkbGU+ZmFsc2U8L1Jlc3RhcnRPbklkbGU+DQogICAgPC9JZGxlU2V0dGluZ3M+DQogICAgPFVzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPnRydWU8L1VzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPg0KICA8L1NldHRpbmdzPg0KICA8VHJpZ2dlcnM+DQogICAgPExvZ29uVHJpZ2dlcj4NCiAgICAgIDxVc2VySWQ+QkVaT1JPXEJlem9yPC9Vc2VySWQ+DQogICAgPC9Mb2dvblRyaWdnZXI+DQogIDwvVHJpZ2dlcnM+DQogIDxBY3Rpb25zIENvbnRleHQ9IkF1dGhvciI+DQogICAgPEV4ZWM+DQogICAgICA8Q29tbWFuZD5DOlxQcm9ncmFtIEZpbGVzXEF1dG9Ib3RrZXlcdjJcQXV0b0hvdGtleTY0LmV4ZTwvQ29tbWFuZD4NCiAgICAgIDxBcmd1bWVudHM+IkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWRcYWhrX3ZvbHVtZS10by1icnVzaC1zaXplLmFoayI8L0FyZ3VtZW50cz4NCiAgICAgIDxXb3JraW5nRGlyZWN0b3J5PkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWQ8L1dvcmtpbmdEaXJlY3Rvcnk+DQogICAgPC9FeGVjPg0KICA8L0FjdGlvbnM+DQo8L1Rhc2s+")),expanded=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFRyaWdnZXJzPg0KICAgIDxMb2dvblRyaWdnZXI+DQogICAgICA8RW5hYmxlZD50cnVlPC9FbmFibGVkPg0KICAgICAgPFVzZXJJZD5CRVpPUk9cQmV6b3I8L1VzZXJJZD4NCiAgICA8L0xvZ29uVHJpZ2dlcj4NCiAgPC9UcmlnZ2Vycz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxNdWx0aXBsZUluc3RhbmNlc1BvbGljeT5JZ25vcmVOZXc8L011bHRpcGxlSW5zdGFuY2VzUG9saWN5Pg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEFsbG93SGFyZFRlcm1pbmF0ZT50cnVlPC9BbGxvd0hhcmRUZXJtaW5hdGU+DQogICAgPFN0YXJ0V2hlbkF2YWlsYWJsZT5mYWxzZTwvU3RhcnRXaGVuQXZhaWxhYmxlPg0KICAgIDxSdW5Pbmx5SWZOZXR3b3JrQXZhaWxhYmxlPmZhbHNlPC9SdW5Pbmx5SWZOZXR3b3JrQXZhaWxhYmxlPg0KICAgIDxJZGxlU2V0dGluZ3M+DQogICAgICA8RHVyYXRpb24+UFQxME08L0R1cmF0aW9uPg0KICAgICAgPFdhaXRUaW1lb3V0PlBUMUg8L1dhaXRUaW1lb3V0Pg0KICAgICAgPFN0b3BPbklkbGVFbmQ+dHJ1ZTwvU3RvcE9uSWRsZUVuZD4NCiAgICAgIDxSZXN0YXJ0T25JZGxlPmZhbHNlPC9SZXN0YXJ0T25JZGxlPg0KICAgIDwvSWRsZVNldHRpbmdzPg0KICAgIDxBbGxvd1N0YXJ0T25EZW1hbmQ+dHJ1ZTwvQWxsb3dTdGFydE9uRGVtYW5kPg0KICAgIDxFbmFibGVkPnRydWU8L0VuYWJsZWQ+DQogICAgPEhpZGRlbj5mYWxzZTwvSGlkZGVuPg0KICAgIDxSdW5Pbmx5SWZJZGxlPmZhbHNlPC9SdW5Pbmx5SWZJZGxlPg0KICAgIDxEaXNhbGxvd1N0YXJ0T25SZW1vdGVBcHBTZXNzaW9uPmZhbHNlPC9EaXNhbGxvd1N0YXJ0T25SZW1vdGVBcHBTZXNzaW9uPg0KICAgIDxVc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZT50cnVlPC9Vc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZT4NCiAgICA8V2FrZVRvUnVuPmZhbHNlPC9XYWtlVG9SdW4+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPFByaW9yaXR5Pjc8L1ByaW9yaXR5Pg0KICA8L1NldHRpbmdzPg0KICA8QWN0aW9ucyBDb250ZXh0PSJBdXRob3IiPg0KICAgIDxFeGVjPg0KICAgICAgPENvbW1hbmQ+QzpcUHJvZ3JhbSBGaWxlc1xBdXRvSG90a2V5XHYyXEF1dG9Ib3RrZXk2NC5leGU8L0NvbW1hbmQ+DQogICAgICA8QXJndW1lbnRzPiJDOlxVc2Vyc1xCZXpvclxEb2N1bWVudHNcQXV0b0hvdGtleVxzY3JpcHRzXGVsZXZhdGVkXGFoa192b2x1bWUtdG8tYnJ1c2gtc2l6ZS5haGsiPC9Bcmd1bWVudHM+DQogICAgICA8V29ya2luZ0RpcmVjdG9yeT5DOlxVc2Vyc1xCZXpvclxEb2N1bWVudHNcQXV0b0hvdGtleVxzY3JpcHRzXGVsZXZhdGVkPC9Xb3JraW5nRGlyZWN0b3J5Pg0KICAgIDwvRXhlYz4NCiAgPC9BY3Rpb25zPg0KPC9UYXNrPg=="));var exact=BrushOperationStore.Read(original);
        Check(BrushTaskControls.RegisterIncidentEligible(exact,BrushTaskControls.RegisterIncidentHash),"exact original raw plus source-generated typed C7 digest is reproducible without normalization");
        Check(WindowsBrushTask.MatchRegisterRefusal(exact,new(raw,WindowsBrushTask.CapturedSecurity,true),expanded),"exact raw/expanded/canonical witness hashes and old pregate refusal match supplied pins");
        foreach(string kind in new[]{"nonce","stage","operation","requested","manager","assembly","sid","login","version","typed","hash"})
        {
            var changed=BrushOperationStore.Read(original);string hash=BrushTaskControls.RegisterIncidentHash;switch(kind){case "nonce":changed.Nonce=Guid.NewGuid();break;case "stage":changed.Stage="Run";break;case "operation":changed.Operation=BrushOperation.Start;break;case "requested":changed.RequestedLogon=true;break;case "manager":changed.ManagerCreated++;break;case "assembly":changed.AssemblyHash=new('A',64);break;case "sid":changed.Sid+="x";break;case "login":changed.LoginKey+="x";break;case "version":changed.Version=2;break;case "typed":changed.Outcome+=" ";break;case "hash":hash=new('B',64);break;}
            Check(!BrushTaskControls.RegisterIncidentEligible(changed,hash),"exact incident guard rejects divergent "+kind);
        }
        foreach(string kind in new[]{"positive","missing-expanded","wrong-expanded","task","security","login-off","owner","current-login","profile","ledger","anchor","unknown","duplicate","replacement","elevation","unproven","timeout","cancel","late-owner","late-task","late-primary","save-failure"})
        {
            string dir=Path.Combine(root,kind);Directory.CreateDirectory(dir);File.Copy(original,Path.Combine(dir,"brush-operation.json"));var operationStore=new BrushOperationStore(dir);var witness=new RegisterRecoveryWitness(raw,expanded);var clock=new ConfirmationClock{Millis=50};var p=new ConfirmationProcesses(clock){Armed=true,Pinned=new ProcessIdentity(50148,134358570717049438,BrushTaskControls.Interpreter,BrushTaskControls.Script,true)};var dispatch=new ReadOnlySettlementDispatch();var controls=new BrushTaskControls(witness,dispatch,operationStore);using var cancel=new CancellationTokenSource();bool current=true;byte[] before=File.ReadAllBytes(operationStore.FilePath);byte[]? late=null;
            if(kind=="missing-expanded")witness.Expanded="";if(kind=="wrong-expanded")witness.Expanded=expanded+" ";if(kind=="task")witness.Enabled=false;if(kind=="security")witness.Security+="x";if(kind=="login-off")witness.Raw=WindowsBrushTask.WithLogonEnabled(raw,false);
            if(kind is "owner" or "current-login" or "profile" or "ledger" or "anchor")current=false;if(kind=="unknown")p.Unknown=true;if(kind=="duplicate")p.Duplicate=true;if(kind=="replacement")p.Reuse=true;if(kind=="elevation")p.Elevated=false;if(kind=="unproven")p.FailedProof=true;
            if(kind=="timeout")witness.Witnessing=()=>clock.Millis=2050;if(kind=="cancel")cancel.Cancel();if(kind=="late-owner")p.Proving=()=>current=false;if(kind=="late-task")p.Proving=()=>witness.Enabled=false;if(kind=="late-primary")p.Proving=()=>{File.AppendAllText(operationStore.FilePath," ");late=File.ReadAllBytes(operationStore.FilePath);};if(kind=="save-failure")File.WriteAllText(operationStore.FilePath+".tmp","unrelated private temp");
            BrushReply? reply=null;try{reply=await controls.Settle(_=>current?Result.Ok("legitimate current owner/login/profile graph"):Result.Fail("current binding changed"),p,cancel.Token,clock);}catch(IOException){}catch(OperationCanceledException){}
            Check((operationStore.Current?.Completed==true)==(kind=="positive") && operationStore.Pending==(kind!="positive"),"read-only Register refusal settlement "+kind+" preserves durable truth");
            Check(dispatch.Calls==0 && witness.Effects==0 && p.Starts==0 && p.Stops==0,"incident "+kind+" never dispatches/UAC/Register/Run/Stop");
            if(kind=="positive")
            {
                var finished=operationStore.Current!;var originalEntry=BrushOperationStore.Read(original);originalEntry.Completed=finished.Completed;originalEntry.Stage=finished.Stage;originalEntry.Outcome=finished.Outcome;
                Check(System.Text.Json.JsonSerializer.Serialize(finished,RegisterRecoveryJson.Default.BrushJournal)==System.Text.Json.JsonSerializer.Serialize(originalEntry,RegisterRecoveryJson.Default.BrushJournal) && finished.AllowedLogon && finished.RequestedLogon==false && reply is { Confirmed:false,StartupChanged:false } && reply.Message.Contains("Recusado antes de Register"),"terminal result is failure/login unchanged and only Completed/Stage/Outcome differ");
                var presentation=UiSession.BrushInspectionPresentation(reply!);var label=new TextBlock{Text=presentation.Status,TextWrapping=TextWrapping.Wrap};label.Measure(new Size(800,200));label.Arrange(new Rect(0,0,800,200));
                Check(presentation.Error && label.Text.Contains("login nao alterado") && label.DesiredSize.Height>0,"shared actual InspectBrush presentation keeps Error and shows terminal refusal offscreen");
                int calls=witness.Calls;await controls.Settle(_=>Result.Ok("fixture"),p,CancellationToken.None,clock);Check(witness.Calls==calls && dispatch.Calls==0,"completed incident never auto-retries inspection or effects");
            }
            else Check((late??before).SequenceEqual(File.ReadAllBytes(operationStore.FilePath)),"incident "+kind+" refusal preserves exact journal bytes");
            if(kind=="save-failure")Check(File.ReadAllText(operationStore.FilePath+".tmp")=="unrelated private temp" && operationStore.Blocked,"atomic Save refusal preserves existing temp and remains blocked");
        }
    }
    static async Task RunBrushRegisterXml()
    {
        string raw=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPE11bHRpcGxlSW5zdGFuY2VzUG9saWN5Pklnbm9yZU5ldzwvTXVsdGlwbGVJbnN0YW5jZXNQb2xpY3k+DQogICAgPElkbGVTZXR0aW5ncz4NCiAgICAgIDxEdXJhdGlvbj5QVDEwTTwvRHVyYXRpb24+DQogICAgICA8V2FpdFRpbWVvdXQ+UFQxSDwvV2FpdFRpbWVvdXQ+DQogICAgICA8U3RvcE9uSWRsZUVuZD50cnVlPC9TdG9wT25JZGxlRW5kPg0KICAgICAgPFJlc3RhcnRPbklkbGU+ZmFsc2U8L1Jlc3RhcnRPbklkbGU+DQogICAgPC9JZGxlU2V0dGluZ3M+DQogICAgPFVzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPnRydWU8L1VzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lPg0KICA8L1NldHRpbmdzPg0KICA8VHJpZ2dlcnM+DQogICAgPExvZ29uVHJpZ2dlcj4NCiAgICAgIDxVc2VySWQ+QkVaT1JPXEJlem9yPC9Vc2VySWQ+DQogICAgPC9Mb2dvblRyaWdnZXI+DQogIDwvVHJpZ2dlcnM+DQogIDxBY3Rpb25zIENvbnRleHQ9IkF1dGhvciI+DQogICAgPEV4ZWM+DQogICAgICA8Q29tbWFuZD5DOlxQcm9ncmFtIEZpbGVzXEF1dG9Ib3RrZXlcdjJcQXV0b0hvdGtleTY0LmV4ZTwvQ29tbWFuZD4NCiAgICAgIDxBcmd1bWVudHM+IkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWRcYWhrX3ZvbHVtZS10by1icnVzaC1zaXplLmFoayI8L0FyZ3VtZW50cz4NCiAgICAgIDxXb3JraW5nRGlyZWN0b3J5PkM6XFVzZXJzXEJlem9yXERvY3VtZW50c1xBdXRvSG90a2V5XHNjcmlwdHNcZWxldmF0ZWQ8L1dvcmtpbmdEaXJlY3Rvcnk+DQogICAgPC9FeGVjPg0KICA8L0FjdGlvbnM+DQo8L1Rhc2s+"));
        string expanded=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0iVVRGLTE2Ij8+DQo8VGFzayB2ZXJzaW9uPSIxLjMiIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFzayI+DQogIDxSZWdpc3RyYXRpb25JbmZvPg0KICAgIDxEZXNjcmlwdGlvbj5SdW4gdGhlIEF1dG9Ib3RrZXkgdm9sdW1lLXRvLWJydXNoLXNpemUgc2NyaXB0IGVsZXZhdGVkIGF0IHVzZXIgbG9nb24uPC9EZXNjcmlwdGlvbj4NCiAgICA8VVJJPlxBdXRvSG90a2V5XFZvbHVtZSB0byBCcnVzaCBTaXplPC9VUkk+DQogIDwvUmVnaXN0cmF0aW9uSW5mbz4NCiAgPFRyaWdnZXJzPg0KICAgIDxMb2dvblRyaWdnZXI+DQogICAgICA8RW5hYmxlZD50cnVlPC9FbmFibGVkPg0KICAgICAgPFVzZXJJZD5CRVpPUk9cQmV6b3I8L1VzZXJJZD4NCiAgICA8L0xvZ29uVHJpZ2dlcj4NCiAgPC9UcmlnZ2Vycz4NCiAgPFByaW5jaXBhbHM+DQogICAgPFByaW5jaXBhbCBpZD0iQXV0aG9yIj4NCiAgICAgIDxVc2VySWQ+Uy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMTwvVXNlcklkPg0KICAgICAgPExvZ29uVHlwZT5JbnRlcmFjdGl2ZVRva2VuPC9Mb2dvblR5cGU+DQogICAgICA8UnVuTGV2ZWw+SGlnaGVzdEF2YWlsYWJsZTwvUnVuTGV2ZWw+DQogICAgPC9QcmluY2lwYWw+DQogIDwvUHJpbmNpcGFscz4NCiAgPFNldHRpbmdzPg0KICAgIDxNdWx0aXBsZUluc3RhbmNlc1BvbGljeT5JZ25vcmVOZXc8L011bHRpcGxlSW5zdGFuY2VzUG9saWN5Pg0KICAgIDxEaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllcz5mYWxzZTwvRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXM+DQogICAgPFN0b3BJZkdvaW5nT25CYXR0ZXJpZXM+ZmFsc2U8L1N0b3BJZkdvaW5nT25CYXR0ZXJpZXM+DQogICAgPEFsbG93SGFyZFRlcm1pbmF0ZT50cnVlPC9BbGxvd0hhcmRUZXJtaW5hdGU+DQogICAgPFN0YXJ0V2hlbkF2YWlsYWJsZT5mYWxzZTwvU3RhcnRXaGVuQXZhaWxhYmxlPg0KICAgIDxSdW5Pbmx5SWZOZXR3b3JrQXZhaWxhYmxlPmZhbHNlPC9SdW5Pbmx5SWZOZXR3b3JrQXZhaWxhYmxlPg0KICAgIDxJZGxlU2V0dGluZ3M+DQogICAgICA8RHVyYXRpb24+UFQxME08L0R1cmF0aW9uPg0KICAgICAgPFdhaXRUaW1lb3V0PlBUMUg8L1dhaXRUaW1lb3V0Pg0KICAgICAgPFN0b3BPbklkbGVFbmQ+dHJ1ZTwvU3RvcE9uSWRsZUVuZD4NCiAgICAgIDxSZXN0YXJ0T25JZGxlPmZhbHNlPC9SZXN0YXJ0T25JZGxlPg0KICAgIDwvSWRsZVNldHRpbmdzPg0KICAgIDxBbGxvd1N0YXJ0T25EZW1hbmQ+dHJ1ZTwvQWxsb3dTdGFydE9uRGVtYW5kPg0KICAgIDxFbmFibGVkPnRydWU8L0VuYWJsZWQ+DQogICAgPEhpZGRlbj5mYWxzZTwvSGlkZGVuPg0KICAgIDxSdW5Pbmx5SWZJZGxlPmZhbHNlPC9SdW5Pbmx5SWZJZGxlPg0KICAgIDxEaXNhbGxvd1N0YXJ0T25SZW1vdGVBcHBTZXNzaW9uPmZhbHNlPC9EaXNhbGxvd1N0YXJ0T25SZW1vdGVBcHBTZXNzaW9uPg0KICAgIDxVc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZT50cnVlPC9Vc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZT4NCiAgICA8V2FrZVRvUnVuPmZhbHNlPC9XYWtlVG9SdW4+DQogICAgPEV4ZWN1dGlvblRpbWVMaW1pdD5QVDBTPC9FeGVjdXRpb25UaW1lTGltaXQ+DQogICAgPFByaW9yaXR5Pjc8L1ByaW9yaXR5Pg0KICA8L1NldHRpbmdzPg0KICA8QWN0aW9ucyBDb250ZXh0PSJBdXRob3IiPg0KICAgIDxFeGVjPg0KICAgICAgPENvbW1hbmQ+QzpcUHJvZ3JhbSBGaWxlc1xBdXRvSG90a2V5XHYyXEF1dG9Ib3RrZXk2NC5leGU8L0NvbW1hbmQ+DQogICAgICA8QXJndW1lbnRzPiJDOlxVc2Vyc1xCZXpvclxEb2N1bWVudHNcQXV0b0hvdGtleVxzY3JpcHRzXGVsZXZhdGVkXGFoa192b2x1bWUtdG8tYnJ1c2gtc2l6ZS5haGsiPC9Bcmd1bWVudHM+DQogICAgICA8V29ya2luZ0RpcmVjdG9yeT5DOlxVc2Vyc1xCZXpvclxEb2N1bWVudHNcQXV0b0hvdGtleVxzY3JpcHRzXGVsZXZhdGVkPC9Xb3JraW5nRGlyZWN0b3J5Pg0KICAgIDwvRXhlYz4NCiAgPC9BY3Rpb25zPg0KPC9UYXNrPg=="));
        string before=WindowsBrushTask.Canonical(raw);bool oldGate=WindowsBrushTask.Canonical(WindowsBrushTask.WithLogonEnabled(expanded,false))==before;
        Check(!oldGate && WindowsBrushTask.Canonical(WindowsBrushTask.WithLogonEnabled(raw,false))==before,"captured expanded Definition old pregate refuses; registered raw mutation passes unchanged strict guard");
        var valid=new RegisterXmlFixture(raw);var disabled=valid.SetLogon(false);var off=valid.Last!;
        Check(disabled.Success && valid.Registers==1 && valid.Probe().LogonEnabled==false,"production raw registration sequence disables sole login once");
        Check(off.Path=="Volume to Brush Size" && off.Flags==0x34 && off.User is null && off.Password is null && off.Logon==3 && off.Sddl is null,"exact XML registration uses fixed task/update/security/trigger-suppression flags and null credentials/SDDL");
        var doc=WindowsBrushTask.Parse(off.Xml);System.Xml.Linq.XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        Check(doc.Root!.Element(ns+"Triggers")!.Elements().Single().Elements().Select(n=>n.Name.LocalName).SequenceEqual(new[]{"Enabled","UserId"}) && WindowsBrushTask.Canonical(off.Xml)==before,"absent Enabled inserted in trigger schema position; every other node/order/value preserved");
        var restored=valid.SetLogon(true);Check(restored.Success && valid.Registers==2 && valid.Probe().LogonEnabled==true && WindowsBrushTask.Canonical(valid.Current.Xml)==before,"restore changes existing Enabled value only with strict canonical preservation");
        var expected=WindowsBrushTask.Parse(off.Xml);expected.Root!.Element(ns+"Triggers")!.Elements().Single().Element(ns+"Enabled")!.Value="true";
        Check(expected.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)==valid.Current.Xml && valid.Runs==0,"existing flag update preserves full serialized task and never invokes Run");
        foreach(string kind in new[]{"owner","task","login","definition","security","after-owner-gate","register-failure","post-login","post-definition","post-security","post-disabled"})
        {
            var f=new RegisterXmlFixture(raw);
            if(kind=="owner")f.Gate=()=>Result.Fail("fixture owner changed");
            if(kind is "task" or "login" or "definition" or "security")f.Reading=n=>{if(n==2)f.Current=kind switch{"task"=>f.Current with{Enabled=false},"login"=>f.Current with{Xml=WindowsBrushTask.WithLogonEnabled(raw,false)},"definition"=>f.Current with{Xml=raw.Replace("IgnoreNew","Parallel")},_=>f.Current with{Security=f.Current.Security+"x"}};};
            if(kind=="after-owner-gate")f.Gate=()=>{f.Current=f.Current with{Xml=WindowsBrushTask.WithLogonEnabled(raw,false)};return Result.Ok("gate returned before task drift");};
            f.ThrowRegister=kind=="register-failure";
            f.Registering=()=>{if(kind=="post-login")f.Current=f.Current with{Xml=WindowsBrushTask.WithLogonEnabled(f.Current.Xml,true)};if(kind=="post-definition")f.Current=f.Current with{Xml=f.Current.Xml.Replace("IgnoreNew","Parallel")};if(kind=="post-security")f.Current=f.Current with{Security=f.Current.Security+"x"};if(kind=="post-disabled")f.Current=f.Current with{Enabled=false};};
            var result=f.SetLogon(false);bool attempted=kind.StartsWith("post-") || kind=="register-failure";
            Check(!result.Success && f.Registers==(attempted?1:0) && f.Runs==0,"exact registration sequence refuses "+kind+" without retry or process effect");
        }
        foreach(string kind in new[]{"action","principal","default","reorder"})
        {
            string xml=kind switch{"action"=>raw.Replace("AutoHotkey64.exe","other.exe"),"principal"=>raw.Replace("HighestAvailable","LeastPrivilege"),"default"=>raw.Replace("<Settings>","<Settings><Hidden>false</Hidden>"),_=>expanded};var f=new RegisterXmlFixture(xml);Check(!f.SetLogon(false).Success && f.Registers==0,"strict captured shape still rejects changed "+kind);
        }
        Check(!new WindowsBrushTask().SetLogon(false).Success,"ordinary read-only adapter has no registration authority and does not open COM");
        foreach(string kind in new[]{"success","failure","postcheck"})
        {
            string dir=Path.Combine(root,"journal-"+kind);Directory.CreateDirectory(dir);var f=new RegisterXmlFixture(raw){ThrowRegister=kind=="failure"};if(kind=="postcheck")f.Registering=()=>f.Current=f.Current with{Security=f.Current.Security+"x"};var dispatch=new RegisterXmlDispatch(f);var store=new BrushOperationStore(dir);var controls=new BrushTaskControls(f,dispatch,store);var reply=await controls.Execute(BrushIntent(BrushOperation.SetLogonEnabled),()=>Result.Ok("fixture current profile authority"),CancellationToken.None);bool success=kind=="success";
            Check(reply.Confirmed==success && store.Pending!=success && new BrushOperationStore(dir).Pending!=success && f.Registers==1 && dispatch.Calls==1,"registration "+kind+" preserves durable confirmed/pending truth");
            if(!success){byte[] saved=File.ReadAllBytes(store.FilePath);await controls.Execute(BrushIntent(BrushOperation.SetLogonEnabled),()=>Result.Ok("fixture"),CancellationToken.None);Check(f.Registers==1 && dispatch.Calls==1 && saved.SequenceEqual(File.ReadAllBytes(store.FilePath)),"unconfirmed "+kind+" journal blocks redispatch and preserves bytes");}
        }
    }
    static async Task RunBrushTasks()
    {
        var exact=FixedBrush();
        Check(BrushTaskControls.Matches(exact),"fixed Brush exact identity accepted");
        foreach(var mutate in new Action<ScriptEntry>[] { e=>e.Id=Guid.NewGuid(), e=>e.Path+=".other",e=>e.Interpreter="wrong.exe",e=>e.ManagementMode=ManagementMode.Normal,e=>e.ManagementMode=ManagementMode.Retired,e=>e.RunAsAdministrator=false,e=>e.LaunchBlocked=true,e=>e.StartWithWindows=true })
        { var e=exact.Copy();mutate(e); Check(!BrushTaskControls.Matches(e),"scope variation cannot obtain task controls"); }
        var baseline=WindowsBrushTask.BaselineXml;
        Check(WindowsBrushTask.Validate(baseline,WindowsBrushTask.CapturedSecurity,true).Ready,"captured exact task definition probe ready");
        Check(!WindowsBrushTask.Validate(baseline.Replace("IgnoreNew","Parallel"),WindowsBrushTask.CapturedSecurity,true).Known,"setting drift rejected");
        Check(!WindowsBrushTask.Validate(baseline,WindowsBrushTask.CapturedSecurity+"x",true).Known,"task security drift rejected");
        Check(!WindowsBrushTask.Validate(baseline,WindowsBrushTask.CapturedSecurity,false).Ready,"Task.Enabled false blocked, never silently enabled");
        var changed=baseline.Replace("<LogonTrigger>","<LogonTrigger><Enabled>false</Enabled>");
        Check(WindowsBrushTask.Canonical(changed)==WindowsBrushTask.Canonical(baseline) && WindowsBrushTask.Validate(changed,WindowsBrushTask.CapturedSecurity,true).LogonEnabled==false,"only logon boolean difference canonicalized");
        foreach(var op in Enum.GetValues<BrushOperation>())
        {
            string dir=Path.Combine(root,"op-"+op);Directory.CreateDirectory(dir);
            var task=new FakeBrushTask(); var p=new FixtureProcesses { Current=new(op is BrushOperation.Stop or BrushOperation.Restart ? [BrushProcess()] : [],true,"fake") };
            task.BeforeRun=()=>p.Current=new([BrushProcess(DateTime.UtcNow.AddSeconds(1).ToFileTimeUtc())],true,"fake new instance");
            var dispatch=new FakeBrushDispatch(task,p); var journal=new BrushOperationStore(dir);var controls=new BrushTaskControls(task,dispatch,journal);controls.Refresh();
            var j=BrushIntent(op); var reply=await controls.Execute(j,()=>Result.Ok("fixture authority"),CancellationToken.None);
            Check(reply.Confirmed && !journal.Pending,"confirmed "+op+" completes durable operation");
            Check(p.Starts==0 && task.Runs==(op is BrushOperation.Start or BrushOperation.Restart ? 1 : 0) && p.Stops==(op is BrushOperation.Stop or BrushOperation.Restart ? 1 : 0) && task.Registers==(op==BrushOperation.SetLogonEnabled ? 1 : 0),"exact effects and zero direct AHK for "+op);
            Check(task.Runs==0 || task.Session==1,"Run uses positively verified requester session for "+op);
            Check(task.State.TaskEnabled && (op==BrushOperation.SetLogonEnabled ? task.State.LogonEnabled==false : task.State.LogonEnabled==true),"startup independent from process action "+op);
            Check(!new BrushOperationStore(dir).Pending,"confirmed journal survives reopen "+op);
        }
        foreach(var kind in new[]{"unknown","duplicate","absent-stop","reused","stop-fail","cancel-run","task-drift-run","requester-exit","lost-reply","accepted-unconfirmed"})
        {
            string dir=Path.Combine(root,kind);Directory.CreateDirectory(dir);var task=new FakeBrushTask();var p=new FixtureProcesses { Current=new([BrushProcess()],true,"fake") };
            var dispatch=new FakeBrushDispatch(task,p);var journal=new BrushOperationStore(dir);var controls=new BrushTaskControls(task,dispatch,journal);controls.Refresh();
            var j=BrushIntent(kind=="accepted-unconfirmed" ? BrushOperation.Start : BrushOperation.Restart);
            if(kind=="unknown")p.Current=new([],false,"unknown"); if(kind=="duplicate")p.Current=new([BrushProcess(),BrushProcess() with { Pid=7102 }],true,"duplicates");
            if(kind is "absent-stop" or "accepted-unconfirmed")p.Current=new([],true,"absence");
            if(kind=="reused")dispatch.StageHook=stage=>{ if(stage=="Stop")p.Current=new([BrushProcess() with { Created=200 }],true,"reused"); return Result.Ok("hook"); };
            if(kind=="stop-fail")p.FailStop=true;
            if(kind is "cancel-run" or "task-drift-run")dispatch.StageHook=stage=>{ if(stage=="Run") { if(kind=="task-drift-run")task.State=task.State with { TaskEnabled=false };return Result.Fail("late stage refused"); }return Result.Ok("hook"); };
            if(kind=="requester-exit")dispatch.Alive=false;if(kind=="lost-reply")dispatch.Lost=true;
            var reply=await controls.Execute(j,()=>Result.Ok("fake"),CancellationToken.None);
            Check(!reply.Confirmed && journal.Pending && new BrushOperationStore(dir).Pending,"uncertainty durable across reopen "+kind);
            Check(task.Runs==(kind is "lost-reply" or "accepted-unconfirmed" ? 1 : 0),"no forbidden followup Run "+kind);
            int calls=dispatch.Calls; await controls.Execute(BrushIntent(BrushOperation.Start),()=>Result.Ok("fake"),CancellationToken.None);
            Check(dispatch.Calls==calls,"pending refuses redispatch "+kind);
            if(kind is "cancel-run" or "task-drift-run")Check(reply.ProcessStopped && p.Stops==1,"restart partial honestly reports stopped "+kind);
        }
        {
            var task=new FakeBrushTask();var p=new FixtureProcesses();var dir=Path.Combine(root,"store-fail");Directory.CreateDirectory(dir);var store=new BrushOperationStore(dir);var dispatch=new FakeBrushDispatch(task,p);var controls=new BrushTaskControls(task,dispatch,store);controls.Refresh();
            File.WriteAllText(store.FilePath+".tmp","unrelated existing temp"); bool failed=false;try { await controls.Execute(BrushIntent(BrushOperation.Start),()=>Result.Ok("fake"),CancellationToken.None); }catch(IOException){failed=true;}
            Check(failed && dispatch.Calls==0 && File.ReadAllText(store.FilePath+".tmp")=="unrelated existing temp","failed journal write refuses dispatch and preserves existing temp");
            File.Delete(store.FilePath+".tmp");File.WriteAllText(store.FilePath,"{malformed}");Check(new BrushOperationStore(dir).Pending,"malformed journal fail closed");
        }
        {
            var task=new FakeBrushTask();task.State=task.State with { LogonEnabled=false };var dir=Path.Combine(root,"external-toggle");Directory.CreateDirectory(dir);var controls=new BrushTaskControls(task,new FakeBrushDispatch(task,new()),new(dir));controls.Refresh();
            Check(!controls.Probe.Known && !controls.Available(exact,BrushOperation.Start,new([],true,"fake")).Success,"external trigger change without completed authorization rejected");
        }
        var steps=new HashSet<string>();Check(BrushTaskControls.Allowed(BrushOperation.Restart,steps,"Stop") && !BrushTaskControls.Allowed(BrushOperation.Restart,steps,"Run"),"restart Run requires Stop stage");steps.Add("Stop");Check(BrushTaskControls.Allowed(BrushOperation.Restart,steps,"Run") && !BrushTaskControls.Allowed(BrushOperation.Restart,steps,"Stop"),"consumed Stop cannot replay");
        Check(!BrushTaskHelper.TryArguments(["--brush-helper","arbitrary",Guid.NewGuid().ToString("N"),"1"],out _,out _,out _) && BrushTaskHelper.Run(["--brush-helper","invalid"])==2,"helper argument scope refuses arbitrary inputs before effects");
        await BrushUiChecks(); await BrushTransportChecks();
    }
    sealed class HeldBrushSnapshot : IProcessAccess
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshots;
        public async Task<Snapshot> Snapshot(CancellationToken token)
        {
            if (++snapshots == 2) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new([], true, "isolated held snapshot");
        }
        public Result Preflight(ScriptEntry e) => Result.Ok("unused fake");
        public Task<Result> Start(ScriptEntry e, CancellationToken token) => throw new InvalidOperationException("No direct launch permitted");
        public Task<Result> Stop(ProcessIdentity p, CancellationToken token) => throw new InvalidOperationException("No stop in Start fixture");
    }
    static async Task RunBrushReviewFixes()
    {
        foreach(var op in new[]{BrushOperation.Stop, BrushOperation.SetLogonEnabled})
        {
            string dir=Path.Combine(root,"completion-"+op);Directory.CreateDirectory(dir);var task=new FakeBrushTask();var p=new FixtureProcesses { Current=new([BrushProcess()],true,"fake") };var store=new BrushOperationStore(dir);var dispatch=new FakeBrushDispatch(task,p);var controls=new BrushTaskControls(task,dispatch,store);controls.Refresh();byte[]? late=null;
            dispatch.AfterReply=reply=>{ Check(reply.Confirmed,"fake "+op+" positively confirmed before final commit");File.AppendAllText(store.FilePath," \n");late=File.ReadAllBytes(store.FilePath); };
            var result=await controls.Execute(BrushIntent(op),()=>Result.Ok("fixture authority"),CancellationToken.None);
            Check(!result.Confirmed && store.Blocked && store.Pending && store.Current is { Completed:false },"confirmed "+op+" completion fingerprint refusal keeps committed pending snapshot");
            Check(late!.SequenceEqual(File.ReadAllBytes(store.FilePath)) && new BrushOperationStore(dir).Pending,"late "+op+" bytes preserved and pending survives reopen");
            var exposed=store.Current!;exposed.Completed=true;Check(store.Current is { Completed:false },"exposed "+op+" snapshot cannot mutate committed state");
            int calls=dispatch.Calls;await controls.Execute(BrushIntent(op),()=>Result.Ok("fixture authority"),CancellationToken.None);Check(dispatch.Calls==calls,"failed final "+op+" save never redispatches");
        }
        foreach(string field in new[]{"Sid","LoginKey","Executable","ExecutableHash","AssemblyHash","CatalogHash","LedgerHash","AnchorHash","Definition","Security","Stage","Outcome"})
        foreach(bool backup in new[]{false,true})
        {
            string dir=Path.Combine(root,"null-"+field+(backup?"-backup":"-primary"));Directory.CreateDirectory(dir);var operationStore=new BrushOperationStore(dir);operationStore.Save(BrushIntent(BrushOperation.Stop));
            string json=File.ReadAllText(operationStore.FilePath);using var document=JsonDocument.Parse(json);using var memory=new MemoryStream();using(var writer=new Utf8JsonWriter(memory)){writer.WriteStartObject();foreach(var property in document.RootElement.EnumerateObject()){if(property.Name==field)writer.WriteNull(field);else property.WriteTo(writer);}writer.WriteEndObject();}string broken=System.Text.Encoding.UTF8.GetString(memory.ToArray());string target=operationStore.FilePath+(backup?".bak":"");File.WriteAllText(target,broken);
            var reopened=new BrushOperationStore(dir);var controls=new BrushTaskControls(new FakeBrushTask(),new FakeBrushDispatch(new FakeBrushTask(),new()),reopened);controls.Refresh();
            Check(reopened.Blocked && reopened.Pending && controls.Notice.Contains("pendente") && File.ReadAllText(target)==broken,"null "+field+" in "+(backup?"backup":"primary")+" keeps store usable blocked without writes");
        }
        {
            var task=new FakeBrushTask();var processes=new HeldBrushSnapshot();var j=BrushIntent(BrushOperation.Start);j.Definition=task.State.Definition;j.Security=task.State.Security;int authorized=0;
            var operation=BrushTaskControls.RunHelper(j,task,processes,_=>{authorized++;return Task.FromResult(Result.Ok("single Run acknowledgement"));},()=>true,CancellationToken.None);
            await processes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));task.State=task.State with { LogonEnabled=false };processes.Release.TrySetResult();var result=await operation;
            Check(authorized==1 && task.Runs==0 && !result.RequestAccepted && !result.Confirmed,"logon toggle while post-authorization Snapshot awaits yields zero Run and no second acknowledgement");
        }
    }
    static async Task RunBrushUiFinal()
    {
        var denied = await BrushTaskControls.RunHelper(BrushIntent(BrushOperation.Stop), new FakeBrushTask(), new WindowsProcesses(), _ => Task.FromResult(Result.Ok("unused")), () => true, CancellationToken.None);
        Check(!denied.Confirmed && denied.Message.Contains("recusa adapters reais"), "direct fixture helper rejects native process adapter before query or effect");
        string dir=Path.Combine(root,"ui-status");Directory.CreateDirectory(dir);using var store=new CatalogStore(dir);var task=new FakeBrushTask();var p=new FixtureProcesses();var dispatch=new FakeBrushDispatch(task,p);var operationStore=new BrushOperationStore(dir);var controls=new BrushTaskControls(task,dispatch,operationStore);var owner=new FixtureOwner();var interaction=new FixtureInteraction { Confirmed=true };var authority=RequestLedger.OpenFixture(Path.Combine(dir,"authority"),owner,store.FilePath);var entry=FixedBrush();store.Save(new Catalog { Authority=authority.Binding,Scripts=[entry] });
        var session=new UiSession(store,p,new FixtureRecycler(),interaction,()=>Task.FromResult(new DiscoveryResult([],[],[])),owner,new FixtureClock(),authority,brush:controls);await session.Refresh();var window=new MainWindow(session);var checkbox=RowPreference(window,entry.Id,"Iniciar com o Windows");
        Check(((TextBlock)window.FindName("IntentText")).Text.Contains("Tarefa: habilitada; login: marcado") && checkbox.IsChecked==true,"actual detail separates task enabled and confirmed login state");
        WpfProgram.Render(window,Path.Combine(root,"brush-confirmed-min.png"),1000,720,1);WpfProgram.Render(window,Path.Combine(root,"brush-confirmed-dpi150.png"),1000,720,1.5);
        await session.Act(ScriptAction.Start);Check(((TextBlock)window.FindName("IntentText")).Text.Contains("Tarefa: habilitada; login: marcado") && session.IntentState(entry).Contains("Pedido Brush pendente") && !checkbox.IsEnabled,"actual pending detail retains task and login truth");
        WpfProgram.Render(window,Path.Combine(root,"brush-pending-min.png"),1000,720,1);WpfProgram.Render(window,Path.Combine(root,"brush-pending-dpi150.png"),1000,720,1.5);
        task.State=BrushProbe.Unknown("Definicao da tarefa mudou; controle bloqueado.");await session.InspectBrush();Check(((TextBlock)window.FindName("IntentText")).Text.Contains("Tarefa e login: nao confirmados") && checkbox.IsChecked is null && session.IntentState(entry).Contains("Definicao da tarefa mudou"),"actual pending error explicitly labels unknown task and login");
        Check(controls.Pending && dispatch.Calls==1 && task.Registers==0 && p.Starts==0 && p.Stops==0,"read-only inspection preserves pending and never redispatches or controls processes");
        WpfProgram.Render(window,Path.Combine(root,"brush-error-min.png"),1000,720,1);WpfProgram.Render(window,Path.Combine(root,"brush-error-dpi150.png"),1000,720,1.5);window.Close();
    }
    static async Task RunBrushFinal()
    {
        Check(!new WindowsBrushTask().Run(1).Success && !new WindowsBrushTask().SetLogon(false).Success,"readonly probe instance has no task effect authority");
        foreach(var op in Enum.GetValues<BrushOperation>())
        {
            string dir=Path.Combine(root,"final-"+op);Directory.CreateDirectory(dir);var task=new FakeBrushTask();var p=new FixtureProcesses { Current=new(op is BrushOperation.Stop or BrushOperation.Restart ? [BrushProcess()] : [],true,"fake") };task.BeforeRun=()=>p.Current=new([BrushProcess(DateTime.UtcNow.AddSeconds(1).ToFileTimeUtc())],true,"new fake");
            var dispatch=new FakeBrushDispatch(task,p);var store=new BrushOperationStore(dir);var controls=new BrushTaskControls(task,dispatch,store);controls.Refresh();var j=BrushIntent(op);var result=await controls.Execute(j,()=>Result.Ok("authority"),CancellationToken.None);
            Check(result.Confirmed && !new BrushOperationStore(dir).Pending,"final guarded "+op+" durable completion");
            Check(task.State.TaskEnabled && p.Starts==0 && task.Runs==(op is BrushOperation.Start or BrushOperation.Restart?1:0) && p.Stops==(op is BrushOperation.Stop or BrushOperation.Restart?1:0),"final guarded "+op+" exact effects without direct launch");
            Check(store.Current!.Stage=="Completed" && store.Current.AllowedLogon==(op!=BrushOperation.SetLogonEnabled),"final guarded "+op+" records authorized trigger state");
            var replay=await controls.Execute(j,()=>Result.Ok("authority"),CancellationToken.None);Check(!replay.Confirmed && dispatch.Calls==1,"completed nonce replay refused "+op);
        }
        foreach(string boundary in new[]{"cancel-before","cancel-after-stop","owner-after-effect","late-final-write","trigger-drift"})
        {
            string dir=Path.Combine(root,boundary);Directory.CreateDirectory(dir);var task=new FakeBrushTask();var p=new FixtureProcesses { Current=new([BrushProcess()],true,"fake") };var store=new BrushOperationStore(dir);var dispatch=new FakeBrushDispatch(task,p);var controls=new BrushTaskControls(task,dispatch,store);controls.Refresh();using var cancel=new CancellationTokenSource();bool owner=true;
            if(boundary=="cancel-before") { dispatch.Hold=new();cancel.Cancel(); }
            if(boundary=="cancel-after-stop")dispatch.StageHook=stage=>{ if(stage=="Run")cancel.Cancel();return Result.Ok("fake"); };
            if(boundary=="owner-after-effect")p.BeforeStop=()=>owner=false;
            if(boundary=="late-final-write")p.BeforeStop=()=>File.WriteAllText(store.FilePath+".tmp","late existing temp");
            if(boundary=="trigger-drift")dispatch.StageHook=stage=>{task.State=task.State with { LogonEnabled=false };return Result.Ok("fake");};
            BrushReply? reply=null;bool cancelled=false;try { reply=await controls.Execute(BrushIntent(BrushOperation.Restart),()=>owner?Result.Ok("fixture"):Result.Fail("owner lost"),cancel.Token); }catch(OperationCanceledException){cancelled=true;}
            if(boundary=="cancel-before")Check(cancelled && dispatch.Calls==0 && !File.Exists(store.FilePath),"cancel before journal gives zero dispatch and effects");
            else Check(store.Pending && new BrushOperationStore(dir).Pending && task.Runs==0,"late boundary stays durable blocked, zero followup Run "+boundary);
            if(boundary=="late-final-write")Check(File.ReadAllText(store.FilePath+".tmp")=="late existing temp" && p.Stops==1,"final write failure preserves existing temp and real stopped outcome uncertainty");
        }
        {
            string dir=Path.Combine(root,"final-ui");Directory.CreateDirectory(dir);using var store=new CatalogStore(dir);var task=new FakeBrushTask();var p=new FixtureProcesses();var dispatcher=new FakeBrushDispatch(task,p);var controls=new BrushTaskControls(task,dispatcher,new(dir));var owner=new FixtureOwner();var interaction=new FixtureInteraction { Confirmed=true };var authority=RequestLedger.OpenFixture(Path.Combine(dir,"authority"),owner,store.FilePath);var entry=FixedBrush();store.Save(new Catalog { Authority=authority.Binding,Scripts=[entry] });
            var session=new UiSession(store,p,new FixtureRecycler(),interaction,()=>Task.FromResult(new DiscoveryResult([],[],[])),owner,new FixtureClock(),authority,brush:controls);await session.Refresh();var bytes=File.ReadAllBytes(store.FilePath);var window=new MainWindow(session);await session.Act(ScriptAction.Start);
            Check(controls.Pending && session.Error && task.Runs==1 && session.Status.Contains("nao confirmada"),"actual UI Run acceptance remains pending rather than claiming running");
            var checkbox=RowPreference(window,entry.Id,"Iniciar com o Windows");Check(checkbox.IsChecked==true && !checkbox.IsEnabled && !window.ActionButton(ScriptAction.Start).IsEnabled,"pending actual checkbox shows confirmed task state and disables repeat");
            Check(bytes.SequenceEqual(File.ReadAllBytes(store.FilePath)) && session.Entries.Single().ManagementMode==ManagementMode.ExternalPending && !session.Entries.Single().StartWithWindows!.Value,"actual UI task flow preserves complete catalog record");
            WpfProgram.Render(window,Path.Combine(root,"brush-pending-min.png"),1000,720,1);WpfProgram.Render(window,Path.Combine(root,"brush-pending-dpi150.png"),1000,720,1.5);
            task.State=BrushProbe.Unknown("Definicao da tarefa mudou; controle bloqueado.");await session.InspectBrush();Check(checkbox.IsChecked is null && !checkbox.IsEnabled,"pending error actual state remains visibly unconfirmed");WpfProgram.Render(window,Path.Combine(root,"brush-error-min.png"),1000,720,1);window.Close();
        }
        using var payload=new MemoryStream();Guid nonce=Guid.NewGuid();await new BrushTaskHelper.Frame(21,nonce).Write(payload,CancellationToken.None);payload.Position=0;var receipt=await BrushTaskHelper.Frame.Read(payload,CancellationToken.None);Check(receipt.Kind==21 && receipt.Bound(nonce) && !receipt.Bound(Guid.NewGuid()),"terminal receipt is bounded and nonce-authenticated");
    }
    static async Task BrushUiChecks()
    {
        string dir=Path.Combine(root,"ui");Directory.CreateDirectory(dir);using var store=new CatalogStore(dir);var task=new FakeBrushTask();var p=new FixtureProcesses();var dispatcher=new FakeBrushDispatch(task,p);var controls=new BrushTaskControls(task,dispatcher,new(dir));var owner=new FixtureOwner();var interaction=new FixtureInteraction();var authority=RequestLedger.OpenFixture(Path.Combine(dir,"authority"),owner,store.FilePath);var entry=FixedBrush();var catalog=new Catalog { Authority=authority.Binding,Scripts=[entry] };store.Save(catalog);
        var session=new UiSession(store,p,new FixtureRecycler(),interaction,()=>Task.FromResult(new DiscoveryResult([],[],[])),owner,new FixtureClock(),authority,brush:controls);await session.Refresh();var bytes=File.ReadAllBytes(store.FilePath);var window=new MainWindow(session);var checkbox=RowPreference(window,entry.Id,"Iniciar com o Windows");
        Check(checkbox.IsChecked==true && entry.StartWithWindows==false && checkbox.IsEnabled,"actual Brush checkbox shows task true while catalog false");
        Check(window.ActionButton(ScriptAction.Start).IsEnabled && !window.ActionButton(ScriptAction.Recycle).IsEnabled,"only exact task-backed actions bypass external refusal");
        checkbox.IsChecked=false;checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await session.WhenIdle;
        Check(dispatcher.Calls==0 && checkbox.IsChecked==true && bytes.SequenceEqual(File.ReadAllBytes(store.FilePath)),"actual task checkbox cancellation restores confirmed state and preserves catalog");
        interaction.Confirmed=true;interaction.HoldConfirmation=new TaskCompletionSource<bool>();var action=session.Act(ScriptAction.Start);await System.Windows.Threading.Dispatcher.Yield();
        Check(session.Busy && dispatcher.Calls==0,"confirmation holds shared UI lane before helper");await session.Act(ScriptAction.Start);session.SetStartWithWindows(entry.Id,false);Check(interaction.Confirmations==2 && dispatcher.Calls==0,"double actions do not prompt or dispatch while lane held");
        interaction.HoldConfirmation.SetResult(false);await action;interaction.HoldConfirmation=null;
        Check(!session.Busy && p.Starts==0 && p.Stops==0 && task.Registers==0,"cancelled current actions have zero process or task effects");
        WpfProgram.Render(window,Path.Combine(root,"brush-confirmed-min.png"),1000,720,1);WpfProgram.Render(window,Path.Combine(root,"brush-confirmed-dpi150.png"),1000,720,1.5);
        task.State=BrushProbe.Unknown("Consulta da tarefa nao confirmada.");controls.Refresh();await session.InspectBrush();Check(checkbox.IsChecked is null && !checkbox.IsEnabled && !window.ActionButton(ScriptAction.Start).IsEnabled,"unknown task startup is unconfirmed, not unchecked");WpfProgram.Render(window,Path.Combine(root,"brush-unknown-min.png"),1000,720,1);window.Close();
        bool mismatch=false;try { _=new BrushTaskControls(new WindowsBrushTask(),dispatcher,new(Path.Combine(dir,"refuse"))); }catch(ArgumentException){mismatch=true;}Check(mismatch,"fixture refuses real task adapter before query");
    }
    static async Task BrushTransportChecks()
    {
        using var identity=WindowsIdentity.GetCurrent();using var current=Process.GetCurrentProcess();string endpoint="Bezoro.Brush."+Guid.NewGuid().ToString("N");Guid nonce=Guid.NewGuid();using var server=BrushTaskHelper.PrivateServer(endpoint,identity.User!.Value);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--brush-worker");start.ArgumentList.Add(endpoint);start.ArgumentList.Add(nonce.ToString("N"));using var worker=Process.Start(start)!;
        await server.WaitForConnectionAsync(timeout.Token);var scope=new ExistingScope(identity.User.Value,current.SessionId,new('A',64),endpoint,Environment.ProcessPath!);using var peer=NativeExistingPeer.Authenticate(server,true,scope);
        Check(peer is not null && peer.Pid==worker.Id && peer.Alive,"isolated pipe authenticates kernel worker PID held SID/session/image");
        using var rejected=NativeExistingPeer.Authenticate(server,true,scope with { Session=scope.Session+1 });Check(rejected is null,"isolated pipe wrong session rejects peer");
        var frame=await BrushTaskHelper.Frame.Read(server,timeout.Token);Check(frame.Kind==1 && frame.Bound(nonce) && !frame.Bound(Guid.NewGuid()),"isolated nonce binding rejects replay/spoof");await new BrushTaskHelper.Frame(2,nonce).Write(server,timeout.Token);await worker.WaitForExitAsync(timeout.Token);Check(worker.ExitCode==0,"isolated worker completed with no profile or effects");
        using var bad=new MemoryStream();bad.Write(BitConverter.GetBytes(508));bad.Position=0;bool refused=false;try{await BrushTaskHelper.Frame.Read(bad,timeout.Token);}catch(IOException){refused=true;}Check(refused,"oversized transport frame rejected before payload");
    }
    static async Task<int> RunBrushWorker(string[] args)
    {
        if(args.Length!=3 || !Guid.TryParseExact(args[2],"N",out var nonce))return 2;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Anonymous);await pipe.ConnectAsync(timeout.Token);await new BrushTaskHelper.Frame(1,nonce).Write(pipe,timeout.Token);var ack=await BrushTaskHelper.Frame.Read(pipe,timeout.Token);return ack.Kind==2 && ack.Bound(nonce)?0:1;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BrushReply))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
partial class ConfirmationJson : System.Text.Json.Serialization.JsonSerializerContext { }

[System.Text.Json.Serialization.JsonSerializable(typeof(BrushJournal))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
partial class RegisterRecoveryJson : System.Text.Json.Serialization.JsonSerializerContext { }
