using System.IO;
using System.Text.Json.Nodes;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;

static partial class Tests
{
    sealed class ManualRig : IDisposable
    {
        public string Directory { get; }
        public string Namespace { get; }
        public CatalogStore Store { get; }
        public FixtureOwner Owner { get; } = new();
        public FixtureClock Clock { get; } = new();
        public FixtureProcesses Processes { get; } = new();
        public FixtureRecycler Recycler { get; } = new();
        public FixtureInteraction Interaction { get; } = new();
        public FixtureElevationDispatcher Dispatcher { get; } = new();
        public FixtureUnbiasedCounter Counter { get; } = new();
        public FixtureContextProtector Protector { get; } = new();
        public BootContextService Context => new(true, Counter, Protector);
        public RequestLedger Ledger { get; private set; }
        public UiSession Session { get; private set; }
        public ScriptEntry Entry => Session.Entries[0];
        public ManualRig(string name, string? authorityNamespace = null, string? script = null)
        {
            Directory = Path.Combine(root, name); Namespace = authorityNamespace ?? Path.Combine(Directory, "authority"); Store = new(Directory);
            Ledger = RequestLedger.OpenFixture(Namespace, Owner, Store.FilePath, Store.FilePath + ".bak");
            script ??= Path.Combine(Directory, "owned-script á espaço.ahk"); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(script)!); File.WriteAllText(script, "; harmless fixture never executed\n");
            string other = Path.Combine(Directory, "other.ahk"); File.WriteAllText(other, "; harmless never executed\n");
            var catalog = new Catalog { Authority = Ledger.Binding, Scripts = [new() { Path = script, Interpreter = Path.Combine(Path.GetDirectoryName(script)!, "AutoHotkey64.exe"), Description = "Owned fixture" }, new() { Path = other, Interpreter = Path.Combine(Directory, "AutoHotkey64.exe") }] };
            Store.Save(catalog); Session = Create();
        }
        UiSession Create() => new(Store, Processes, Recycler, Interaction, () => Task.FromResult(new DiscoveryResult([], [], [])), Owner, Clock, Ledger, Dispatcher, Context);
        public void Reopen() { Ledger = RequestLedger.OpenFixture(Namespace, Owner, Store.FilePath, Store.FilePath + ".bak"); Session = Create(); }
        public async Task Ready(bool managed = false)
        {
            if (managed)
            {
                var c = Store.Load(); c.Scripts[0].ManagementMode = ManagementMode.Normal; c.Scripts[0].RequestedEnabled = true;
                var journal = new MigrationJournal { Fixture = true, ManifestHash = new('A',64), OwnerSid = Owner.Sid, State = MigrationState.Completed, AppliedSteps = [.. JournalStore.Steps] };
                c.Migration = new() { State = MigrationState.Completed, ManifestHash = journal.ManifestHash, OwnerSid = Owner.Sid, JournalDigest = JournalStore.Save(Directory,journal), CompletedUtcTicks = Clock.UtcNow.Ticks };
                Store.Save(c); Reopen();
            }
            await Session.Refresh(); Session.SetRunAsAdministrator(true); Interaction.Confirmed = true;
        }
        public async Task Pending() { await Ready(); await Session.Act(ScriptAction.Start); }
        public void Dispose() { Owner.Dispose(); Store.Dispose(); }
    }
    static byte[] Bytes(string path) => File.ReadAllBytes(path);
    static async Task RunManualUac()
    {
        using(var r=new ManualRig("manual-cancel"))
        {
            await r.Ready(); r.Interaction.Confirmed=false; var profile=Bytes(r.Store.FilePath);var ledger=Bytes(r.Ledger.FilePath);var anchor=Bytes(r.Ledger.AnchorPath);
            await r.Session.Act(ScriptAction.Start);
            Check(profile.SequenceEqual(Bytes(r.Store.FilePath))&&ledger.SequenceEqual(Bytes(r.Ledger.FilePath))&&anchor.SequenceEqual(Bytes(r.Ledger.AnchorPath)),"manual confirmation Cancel performs zero writes");
            Check(r.Dispatcher.Calls==0&&r.Processes.Starts==0&&!r.Ledger.HasPending,"manual confirmation Cancel dispatches nothing");
            Check(r.Interaction.LastConfirmation.Contains(r.Entry.Path)&&r.Interaction.LastConfirmation.Contains(r.Entry.Interpreter)&&r.Interaction.LastConfirmation.Contains("UAC"),"manual UAC confirmation includes canonical script and interpreter");
        }
        using(var r=new ManualRig("accepted-null"))
        {
            await r.Ready();r.Dispatcher.BeforeConsume=p=>
            {
                var persisted=JsonNode.Parse(Bytes(r.Ledger.FilePath))!;
                Check(persisted["Requests"]!.AsArray().Count==1&&persisted["Requests"]![0]!["Nonce"]!.GetValue<Guid>()==p.Nonce&&File.Exists(r.Ledger.AnchorPath),"marker and anchor durably present before fake OS dispatch");
                Check(!p.Validate(r.Session.Entries[1],true).Success,"permit path mutation refused before consume");
            };
            await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==1&&r.Ledger.HasPending&&r.Session.State(r.Entry)=="Não confirmado","accepted null preserves pending and never claims Running/admin");
            Check(r.Session.Status.Contains("prova de contexto"),"null result explains persistent limit");
            await r.Session.Act(ScriptAction.Start); await r.Session.Act(ScriptAction.Restart); await r.Session.Reconcile();
            Check(r.Dispatcher.Calls==1&&r.Processes.Starts==0&&r.Processes.Stops==0,"pending globally blocks repeat restart and automatic effects");
            r.Session.SetRunAsAdministrator(false);await r.Session.Act(ScriptAction.Start);
            Check(!r.Session.Selected!.RunAsAdministrator&&r.Dispatcher.Calls==1&&r.Processes.Starts==0&&r.Ledger.HasPending,"uncheck does not bypass durable pending");
            r.Reopen();await r.Session.Refresh(); await r.Session.Act(ScriptAction.Start);
            Check(r.Ledger.HasPending&&r.Dispatcher.Calls==1,"reopening preserves barrier");
            var current=r.Store.Load();current.Scripts[0].Id=Guid.NewGuid();r.Store.Save(current);r.Reopen();await r.Session.Refresh();
            Check(!r.Session.Availability(ScriptAction.Start).Success&&r.Ledger.HasPending,"changed registration Guid cannot bypass canonical path authority");
            current=r.Store.Load();var reimport=current.Scripts[0].Copy();current.Scripts.RemoveAt(0);r.Store.Save(current);r.Reopen();
            Check(r.Ledger.HasPending&&!r.Session.Availability(ScriptAction.Start).Success,"removing record does not erase global pending");
            current=r.Store.Load();reimport.Id=Guid.NewGuid();current.Scripts.Insert(0,reimport);r.Store.Save(current);r.Reopen();await r.Session.Refresh();
            Check(r.Session.State(r.Entry).Contains("Não confirmado")&&!r.Session.Availability(ScriptAction.Start).Success,"reimport restores display without clearing authority");
            r.Clock.UtcNow=r.Clock.UtcNow.AddDays(30);await r.Session.VerifyRequest();
            Check(r.Ledger.HasPending,"time and complete empty snapshot never clear candidate-less request");
            var newProfile=new ManualRig("second-profile",r.Namespace,r.Entry.Path);using(newProfile)
            {
                await newProfile.Session.Refresh();await newProfile.Session.Act(ScriptAction.Start);
                Check(newProfile.Ledger.HasPending&&newProfile.Dispatcher.Calls==0&&newProfile.Processes.Starts==0,"different data-dir/profile shares authoritative barrier");
                Check(newProfile.Session.CurrentCatalog.Authority!.LedgerId==r.Session.CurrentCatalog.Authority!.LedgerId,"profiles bind same fixed fixture authority without mirrored markers");
            }
            var permit=r.Dispatcher.LastPermit!;
            Check(!permit.Validate(r.Entry,true).Success,"single-use consumed permit refuses reuse");
            var native=await new WindowsElevationDispatcher().Dispatch(permit,r.Entry,CancellationToken.None);
            Check(native.Disposition==ElevationDisposition.RefusedBeforeDispatch,"fixture permit never reaches native dispatcher");
        }
        using(var r=new ManualRig("auto-checked"))
        {
            await r.Ready(true);var before=Bytes(r.Store.FilePath);await r.Session.Reconcile();await r.Session.Reconcile();r.Session.RetryRecovery();
            Check(r.Dispatcher.Calls==0&&r.Processes.Starts==0&&r.Session.CurrentCatalog.Recovery.Count==0&&before.SequenceEqual(Bytes(r.Store.FilePath)),"checked login auto and retry never UAC or consume budget");
            await r.Session.Act(ScriptAction.Restart);
            Check(r.Processes.Stops==0&&r.Dispatcher.Calls==0&&r.Session.CurrentCatalog.Recovery.Count==0,"checked restart refused before stop or reserve");
            r.Dispatcher.Outcome=new(ElevationDisposition.Cancelled,"Fake UAC1223");await r.Session.Act(ScriptAction.Start);
            var budget=r.Session.CurrentCatalog.Recovery.Single();
            Check(r.Dispatcher.Calls==1&&!r.Ledger.HasPending&&budget.AttemptsUtcTicks.Count==1&&budget.Failures==0&&budget.NextTryUtcTicks==0,"1223 removes own marker and keeps manual reservation without failure/cooldown");
            var c=r.Store.Load();c.Recovery[0].Suspended=true;r.Store.Save(c);r.Reopen();await r.Session.Refresh();await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==1&&r.Processes.Stops==0,"manual checked does not bypass persisted suspension");
        }
        using(var r=new ManualRig("ledger-save-failure"))
        {
            await r.Ready();using(var locked=new FileStream(r.Ledger.FilePath+".tmp",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==0&&r.Processes.Starts==0&&!r.Ledger.Ready,"ledger save failure authorizes zero invoke and fails closed");
            Check(r.Session.RequestDetails(r.Entry).Contains(r.Ledger.FilePath),"ledger error exposes authority path without deletion advice");
        }
        using(var r=new ManualRig("profile-save-failure"))
        {
            await r.Ready(true);using(var locked=new FileStream(r.Store.FilePath+".tmp",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==0&&!r.Ledger.HasPending,"profile budget save failure authorizes zero marker/dispatch");
        }
        using(var r=new ManualRig("cleanup-failure"))
        {
            await r.Ready();r.Dispatcher.Outcome=new(ElevationDisposition.Cancelled,"Fake UAC1223");
            r.Dispatcher.AfterConsume=_=>File.SetAttributes(r.Ledger.FilePath,FileAttributes.ReadOnly);
            try {await r.Session.Act(ScriptAction.Start);}
            finally {File.SetAttributes(r.Ledger.FilePath,FileAttributes.Normal);}
            r.Reopen();
            Check(r.Ledger.HasPending&&r.Session.RequestDetails(r.Entry).Contains("não confirmada"),"1223 cleanup failure preserves durable barrier on reopen");
        }
        foreach(var fault in new[]{"access-denied","other-error","accepted-null"})
        {
            using var r=new ManualRig("fault-"+fault);await r.Ready();r.Dispatcher.Outcome=new(fault=="accepted-null"?ElevationDisposition.Accepted:ElevationDisposition.Uncertain,"Fake "+fault);
            await r.Session.Act(ScriptAction.Start);
            Check(r.Ledger.HasPending&&r.Dispatcher.Calls==1&&r.Processes.Starts==0,"postdispatch uncertainty preserves marker:"+fault);
        }
        using(var r=new ManualRig("owner-lost-before-dispatch"))
        {
            await r.Ready();r.Dispatcher.BeforeConsume=_=>r.Owner.Held=false;await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==0&&r.Ledger.HasPending,"owner loss blocks consume; cleanup cannot erase another owner barrier");
        }
        using(var r=new ManualRig("primary-race-before-dispatch"))
        {
            await r.Ready();r.Dispatcher.BeforeConsume=_=>File.AppendAllText(r.Store.FilePath," ");await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==0&&!r.Ledger.HasPending,"profile raw mismatch blocks dispatcher and proves own predispatch cleanup");
        }
        using(var r=new ManualRig("ledger-nonce-race"))
        {
            await r.Ready();Guid foreign=Guid.NewGuid();r.Dispatcher.BeforeConsume=_=>
            {var node=JsonNode.Parse(Bytes(r.Ledger.FilePath))!;node["Requests"]![0]!["Nonce"]=foreign;File.WriteAllText(r.Ledger.FilePath,node.ToJsonString());};
            await r.Session.Act(ScriptAction.Start);r.Reopen();
            Check(r.Dispatcher.Calls==0&&r.Ledger.Requests.Single().Nonce==foreign,"nonce/raw change blocks dispatch and cannot erase changed request");
        }
        using(var r=new ManualRig("cancel-before-dispatch"))
        {
            await r.Ready();r.Dispatcher.BeforeConsume=p=>{_=r.Session.PrepareExit(TimeSpan.Zero);};await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==0&&!r.Ledger.HasPending&&!r.Session.Busy,"CT before delivery cleans own proven undelivered request");
        }
        using(var r=new ManualRig("exit-inflight"))
        {
            await r.Ready();r.Dispatcher.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);var start=r.Session.Act(ScriptAction.Start);
            await r.Session.Act(ScriptAction.Start);var exit=await r.Session.PrepareExit(TimeSpan.Zero);
            Check(!exit&&r.Session.Busy&&r.Owner.Held&&r.Dispatcher.Calls==1,"exit and repeated click retain lane/owner while fake UAC in flight");
            r.Dispatcher.Hold.SetResult();await start;
            Check(!r.Session.Busy&&r.Session.WhenIdle.IsCompleted&&r.Ledger.HasPending&&r.Processes.Stops==0,"CT after delivery does not roll back request or terminate child");
        }
        using(var r=new ManualRig("positive-candidate"))
        {
            await r.Ready();var identity=new ProcessIdentity(8100,132000000000000000,r.Entry.Interpreter,r.Entry.Path);
            r.Dispatcher.Outcome=new(ElevationDisposition.Accepted,"Fake accepted candidate",new(identity.Pid,identity.Created,identity.Executable));
            r.Dispatcher.AfterConsume=_=>r.Processes.Current=new([identity],true,"Fake positive object");await r.Session.Act(ScriptAction.Start);
            Check(!r.Ledger.HasPending&&r.Session.State(r.Entry).StartsWith("Rodando")&&!r.Session.Status.Contains("administrador"),"positive script/interpreter/creation/object correlation clears durably without admin claim");
        }
        foreach(var mismatch in new[]{"pid-reused","interpreter","script","multiple","proof-unavailable"})
        {
            using var r=new ManualRig("candidate-"+mismatch);await r.Ready();var candidate=new LaunchCandidate(8200,132000000000000000,r.Entry.Interpreter);
            r.Dispatcher.Outcome=new(ElevationDisposition.Accepted,"Fake candidate",candidate);
            var identity=new ProcessIdentity(candidate.Pid,mismatch=="pid-reused"?candidate.Created+1:candidate.Created,mismatch=="interpreter"?@"C:\Different\AutoHotkey64.exe":candidate.Executable,mismatch=="script"?r.Session.Entries[1].Path:r.Entry.Path);
            r.Dispatcher.AfterConsume=_=>r.Processes.Current=new(mismatch=="multiple"?[identity,identity with {Pid=8201}]:[identity],true,"Fake mismatch");
            if(mismatch=="proof-unavailable")r.Processes.RefuseProof=true;
            await r.Session.Act(ScriptAction.Start);await r.Session.VerifyRequest();
            Check(r.Ledger.HasPending&&r.Session.State(r.Entry).Contains("Não confirmado"),"candidate mismatch never clears barrier:"+mismatch);
        }
        using(var r=new ManualRig("other-manual-stop"))
        {
            await r.Ready(true);await r.Session.Act(ScriptAction.Start);var other=r.Session.Entries[1];
            r.Processes.Current=new([new(8300,132000000000000000,other.Interpreter,other.Path)],true,"Owned other verified");await r.Session.Refresh();r.Session.Select(other.Id);
            await r.Session.Act(ScriptAction.Stop);
            Check(r.Processes.Stops==1&&r.Ledger.HasPending&&r.Dispatcher.Calls==1,"verified manual Stop of another path retains pending without UAC");
            r.Processes.Current=new([new(8301,132000000000000001,other.Interpreter,other.Path)],true,"Owned other verified");await r.Session.SetIntent(false);
            Check(r.Processes.Stops==1&&r.Session.Selected!.StartWithWindows is null&&r.Ledger.HasPending,"unmanaged checkbox cannot change runtime or pending authority");
            await r.Session.Reconcile();
            Check(r.Processes.Stops==1&&r.Processes.Starts==0,"all automatic effects remain globally suspended with pending");
        }
        using(var r=new ManualRig("native-normal-boundary"))
        {
            var plain=r.Entry.Copy();plain.RunAsAdministrator=false;
            var result=await new WindowsProcesses().Start(plain,CancellationToken.None);
            Check(!result.Success&&result.Message.Contains("Autoridade"),"normal native Start without authority refuses before OS query");
            result=await new ActionController(r.Processes,r.Recycler,(_,_)=>Result.Fail("invalid authority")).Execute(plain,ScriptAction.Start,true);
            Check(!result.Success&&r.Processes.Starts==0,"core invalid normal authority refuses without process effect");
        }
        var args=WindowsElevationDispatcher.BuildInfo(@"C:\Fixture\AutoHotkey64.exe",@"C:\Fixture\á script espaço.ahk");
        Check(args.UseShellExecute&&args.Verb=="runas"&&args.ArgumentList.Single()==@"C:\Fixture\á script espaço.ahk"&&args.FileName.EndsWith(".exe")&&args.WorkingDirectory==@"C:\Fixture","runas uses explicit interpreter and runtime ArgumentList Unicode quoting");
    }
}
