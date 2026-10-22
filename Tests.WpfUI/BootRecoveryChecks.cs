using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;

static partial class Tests
{
    static void JsonEdit(string file, Action<JsonNode> edit) { var value = JsonNode.Parse(Bytes(file))!; edit(value); File.WriteAllText(file, value.ToJsonString()); }
    static void MakeLegacy(ManualRig r)
    {
        JsonEdit(r.Ledger.FilePath, n => { n["Schema"] = 1; n["Binding"]!["Version"] = 1; foreach (var v in n["Requests"]!.AsArray()) v!.AsObject().Remove("Context"); });
        JsonEdit(r.Ledger.AnchorPath, n => n["Version"] = 1);
        JsonEdit(r.Store.FilePath, n => n["Authority"]!["Version"] = 1); r.Reopen();
    }
    static bool ThrowsBoot(Action action) { try { action();return false; } catch(ArgumentException) { return true; } }
    static async Task RunBootRecovery()
    {
        using (var r = new ManualRig("P06-new"))
        {
            Check(r.Ledger.Ready && r.Ledger.Binding?.Version == 2 && !r.Ledger.HasPending, "P06 initial empty ledger2 anchor2 binding2");
            var id=r.Ledger.Binding!.LedgerId; r.Reopen(); Check(r.Ledger.Binding!.LedgerId==id,"P06 reopen never invents new identity");
            File.Delete(r.Ledger.FilePath); r.Reopen(); Check(!r.Ledger.Ready && r.Ledger.ReadOnly,"P06 historical anchor prevents empty recreation");
        }
        using (var r = new ManualRig("P01-legacy"))
        {
            await r.Ready(true); await r.Session.Act(ScriptAction.Start); var oldRequest=r.Ledger.Requests.Single(); var oldCatalog=r.Session.CurrentCatalog; var metadata=JsonNode.Parse(Bytes(r.Store.FilePath))!;metadata.AsObject().Remove("Authority"); MakeLegacy(r);
            Check(r.Session.NeedsSchemaConversion && !r.Session.Availability(ScriptAction.Start).Success,"P01 legacy authority blocks effects before explicit conversion");
            var ledger=Bytes(r.Ledger.FilePath);r.Interaction.Confirmed=false;await r.Session.ConvertSchema();Check(ledger.SequenceEqual(Bytes(r.Ledger.FilePath)),"P01 migration Cancel preserves authority");
            r.Interaction.Confirmed=true;await r.Session.ConvertSchema();var request=r.Ledger.Requests.Single();
            Check(r.Ledger.Binding?.Version==2 && !r.Session.NeedsSchemaConversion && r.Store.VerifyPrimary(r.Session.CurrentCatalog),"P01 explicit durable three-file conversion completes");
            Check(request.Nonce==oldRequest.Nonce&&request.Candidate==oldRequest.Candidate&&request.Sid==oldRequest.Sid&&request.ScriptPath==oldRequest.ScriptPath&&request.Context.Kind=="Legacy"&&request.Context.Blob.Length==0,"P01 requests identities preserved only Legacy no retrospective stamp");
            Check(r.Session.CurrentCatalog.Scripts[0].Id==oldCatalog.Scripts[0].Id&&r.Session.CurrentCatalog.Scripts[0].RunAsAdministrator,"P01 script metadata preference preserved");
            var migrated=JsonNode.Parse(Bytes(r.Store.FilePath))!;migrated.AsObject().Remove("Authority");Check(JsonNode.DeepEquals(metadata,migrated),"P01 entire script migration recovery budget metadata unchanged");
            r.Counter.Value=10;await r.Session.RecoverAfterRestart();Check(r.Ledger.HasPending&&r.Dispatcher.Calls==1,"R02 Legacy cannot recover by counter reset");
            var second=new ManualRig("P04-alternate",r.Namespace,r.Entry.Path);using(second){JsonEdit(second.Store.FilePath,n=>n["Authority"]!["Version"]=1);second.Reopen();second.Interaction.Confirmed=true;await second.Session.ConvertSchema();Check(second.Session.CurrentCatalog.Authority?.LedgerId==r.Ledger.Binding!.LedgerId&&second.Session.CurrentCatalog.Authority.Version==2&&second.Ledger.HasPending,"P04 old alternate binding converts only to recognized existing authority");}
        }
        foreach(var stage in new[]{"ledger","anchor","profile"})
        {
            using var r=new ManualRig("P02-fail-"+stage);await r.Pending();MakeLegacy(r);r.Interaction.Confirmed=true;
            string path=stage=="ledger"?r.Ledger.FilePath+".tmp":stage=="anchor"?r.Ledger.AnchorPath+".tmp":r.Store.FilePath+".tmp";
            using(var held=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))await r.Session.ConvertSchema();
            r.Reopen();await r.Session.Reconcile();Check(r.Ledger.HasPending&&r.Dispatcher.Calls==1&&r.Processes.Starts==0&&r.Processes.Stops==0,"P02 "+stage+" write failure/reopen preserves barrier and zero effects");
            if(stage=="anchor")Check(!r.Ledger.Ready,"P02 mismatched primary versions block without backup promotion");
            if(stage=="profile"){r.Interaction.Confirmed=true;await r.Session.ConvertSchema();Check(r.Session.CurrentCatalog.Authority?.Version==2&&r.Ledger.HasPending,"P02 explicit recognized alternate profile resume preserves pending");}
        }
        foreach(var fault in new[]{"capture","zero","protect","unprotect","digest","content","large"})
        {
            using var r=new ManualRig("C02-"+fault);await r.Ready(true);var profile=Bytes(r.Store.FilePath);var ledger=Bytes(r.Ledger.FilePath);
            if(fault=="capture")r.Counter.Fail=true;if(fault=="zero")r.Counter.Value=0;if(fault=="protect")r.Protector.FailProtect=true;if(fault=="unprotect")r.Protector.FailUnprotect=true;
            if(fault=="digest")r.Protector.Alter=b=>{b[^1]^=1;return b;};if(fault=="content")r.Protector.Alter=b=>{b[10]^=1;return b;};if(fault=="large")r.Protector.Alter=_=>new byte[BootContextService.PlainLimit+1];
            await r.Session.Act(ScriptAction.Start);Check(r.Dispatcher.Calls==0&&!r.Ledger.HasPending&&r.Session.CurrentCatalog.Recovery.Count==0&&profile.SequenceEqual(Bytes(r.Store.FilePath))&&ledger.SequenceEqual(Bytes(r.Ledger.FilePath)),"C02 "+fault+" refuses before budget marker dispatch writes");
        }
        using(var r=new ManualRig("C01-order"))
        {
            await r.Ready(true);r.Dispatcher.BeforeConsume=_=>Check(r.Protector.ProtectCalls==1&&r.Counter.Calls>=1&&r.Session.CurrentCatalog.Recovery.Single().AttemptsUtcTicks.Count==1&&r.Ledger.Requests.Single().Context.Origin==1000,"C01 roundtrip precedes budget and durable marker precedes fake OS");
            await r.Session.Act(ScriptAction.Start);Check(r.Dispatcher.Calls==1&&r.Counter.Calls>=2,"C01 immediate counter revalidation before fake dispatch");
            var request=r.Ledger.Requests.Single();var binding=r.Ledger.Binding!;
            foreach(var field in new[]{"sid","mode","ledger","nonce","script","exe","origin","manager","login","session","utc"})
            {
                var copy=request.Copy();var b=binding.Copy();switch(field){case"sid":copy.Sid="other";break;case"mode":b.Fixture=false;break;case"ledger":b.LedgerId=Guid.NewGuid();break;case"nonce":copy.Nonce=Guid.NewGuid();break;case"script":copy.ScriptPath+= "x";break;case"exe":copy.InterpreterPath+="x";break;case"origin":copy.Context.Origin++;break;case"manager":copy.ManagerCreated++;break;case"login":copy.LoginKey+="x";break;case"session":copy.SessionId++;break;case"utc":copy.RequestedUtcTicks++;break;}
                Check(!r.Context.Verify(copy,b).Success,"C03 sealed "+field+" mutation rejected");
            }
            r.Protector.Machine="machine-B";Check(!r.Context.Verify(request,binding).Success,"C03 machine A blob rejected on fake B");r.Protector.Machine="machine-A";
            foreach(var alter in new Func<byte[],byte[]>[]{b=>b[..^1],b=>b.Concat(new byte[]{0}).ToArray(),b=>{b[0]=0;return b;},b=>{b[5]^=1;return b;}}){r.Protector.Alter=alter;Check(!r.Context.Verify(request,binding).Success,"C04 truncated unknown missing or altered grammar rejected");}r.Protector.Alter=null;
            Check(!WindowsContextProtector.ValidBuffer(IntPtr.Zero,10,100)&&!WindowsContextProtector.ValidBuffer(new IntPtr(1),0,100)&&!WindowsContextProtector.ValidBuffer(new IntPtr(1),-1,100)&&!WindowsContextProtector.ValidBuffer(new IntPtr(1),101,100)&&WindowsContextProtector.ValidBuffer(new IntPtr(1),100,100),"C04 native pointer and size limits checked without native DPAPI");
            var c=request.Copy();c.Context.Blob=new byte[BootContextService.BlobLimit+1];Check(!r.Context.Verify(c,binding).Success,"C04 blob over 512KiB rejected before protector");
        }
        using(var r=new ManualRig("C05-pre-dispatch-reset"))
        {
            await r.Ready(true);r.Dispatcher.BeforeConsume=_=>r.Counter.Value=10;await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==0&&!r.Ledger.HasPending&&r.Session.CurrentCatalog.Recovery.Single().AttemptsUtcTicks.Count==1,"C05 counter reset before OS refuses dispatch preserves deliberate budget and proven local cleanup");
        }
        using(var r=new ManualRig("R01-success"))
        {
            await r.Pending();r.Counter.Value=10;r.Counter.Samples.Enqueue(10);r.Counter.Samples.Enqueue(20);r.Interaction.Confirmed=true;var profile=Bytes(r.Store.FilePath);
            await r.Session.RecoverAfterRestart();Check(!r.Ledger.HasPending&&r.Dispatcher.Calls==1&&r.Processes.Starts==0&&r.Processes.Stops==0&&profile.SequenceEqual(Bytes(r.Store.FilePath)),"R01 1000 origin 10/20 empty fresh snapshot clears nonce only no budget or effects");
            Check(r.Interaction.LastConfirmation.Contains(r.Entry.Path)&&r.Interaction.LastConfirmation.Contains("próximo ciclo ordinário")&&r.Interaction.LastConfirmation.Contains("não inicia"),"R01 confirmation describes path checks and possible next ordinary supervision");
            r.Reopen();Check(!r.Ledger.HasPending,"R01 durable clear survives reopen");
        }
        foreach(var samples in new[]{new ulong[]{1000,1000},new ulong[]{1001,1002},new ulong[]{20,10}})
        {
            using var r=new ManualRig("R02-samples-"+samples[0]+"-"+samples[1]);await r.Pending();foreach(var v in samples)r.Counter.Samples.Enqueue(v);r.Interaction.Confirmed=true;await r.Session.RecoverAfterRestart();Check(r.Ledger.HasPending&&r.Dispatcher.Calls==1,"R02 equal greater decreasing samples keep marker");
        }
        foreach(var fault in new[]{"counter","unprotect","incomplete","unknown","same-script","other-exe","query"})
        {
            using var r=new ManualRig("R03-"+fault);await r.Pending();r.Counter.Value=10;r.Interaction.Confirmed=true;
            if(fault=="counter")r.Counter.Fail=true;if(fault=="unprotect")r.Protector.FailUnprotect=true;if(fault=="incomplete")r.Processes.Current=new([],false,"partial");if(fault=="unknown")r.Processes.Current=new([new(9000,1,"",null)],true,"unknown");if(fault is "same-script" or "other-exe")r.Processes.Current=new([new(9000,1,fault=="other-exe"?Path.Combine(r.Directory,"AutoHotkey32.exe"):r.Entry.Interpreter,r.Entry.Path)],true,"same script");if(fault=="query")r.Processes.ThrowSnapshot=true;
            await r.Session.RecoverAfterRestart();Check(r.Ledger.HasPending&&r.Processes.Starts==0&&r.Processes.Stops==0,"R03 "+fault+" preserves absence barrier");
        }
        using(var r=new ManualRig("R04-verify-cancel"))
        {
            await r.Pending();r.Counter.Value=10;var ledger=Bytes(r.Ledger.FilePath);var profile=Bytes(r.Store.FilePath);await r.Session.VerifyRequest();Check(r.Ledger.HasPending&&ledger.SequenceEqual(Bytes(r.Ledger.FilePath))&&r.Session.Status.Contains("elegível"),"R04 Verify reports eligible but zero absence clear");
            r.Interaction.Confirmed=false;await r.Session.RecoverAfterRestart();Check(ledger.SequenceEqual(Bytes(r.Ledger.FilePath))&&profile.SequenceEqual(Bytes(r.Store.FilePath))&&r.Dispatcher.Calls==1,"R04 Recovery Cancel zero changes effects budgets");
            r.Clock.UtcNow=r.Clock.UtcNow.AddYears(10);r.Counter.Value=1001;r.Interaction.Confirmed=true;await r.Session.RecoverAfterRestart();Check(r.Ledger.HasPending,"R02 civil-clock change and no counter reset never recover");
        }
        foreach(var change in new[]{"selection","ledger","profile","shutdown"})
        {
            using var r=new ManualRig("R04-race-"+change);await r.Pending();r.Counter.Value=10;r.Interaction.HoldConfirmation=new();var recovery=r.Session.RecoverAfterRestart();
            if(change=="selection"){ // Busy intentionally refuses user selection; external catalog edit is covered separately.
                r.Session.Select(r.Session.Entries[1].Id);Check(r.Session.SelectedId==r.Entry.Id,"R04 busy selection remains captured identity");
            }
            if(change=="ledger")File.AppendAllText(r.Ledger.FilePath," ");if(change=="profile")File.AppendAllText(r.Store.FilePath," ");if(change=="shutdown")_ = r.Session.PrepareExit(TimeSpan.FromMilliseconds(1));
            r.Interaction.HoldConfirmation.SetResult(change!="selection");await recovery;Check(r.Ledger.HasPending&&r.Dispatcher.Calls==1&&r.Processes.Stops==0,"R04 "+change+" confirmation race refuses recovery");
        }
        using(var r=new ManualRig("R05-inflight"))
        {
            await r.Ready();r.Dispatcher.Hold=new();var launch=r.Session.Act(ScriptAction.Start);r.Counter.Value=10;await r.Session.RecoverAfterRestart();Check(r.Session.Busy&&r.Ledger.HasPending,"R05 in-flight fake UAC lane refuses Recovery");r.Dispatcher.Hold.SetResult();await launch;
        }
        using(var r=new ManualRig("R06-commit-fail"))
        {
            await r.Pending();r.Counter.Value=10;r.Interaction.Confirmed=true;using(var held=new FileStream(r.Ledger.FilePath+".tmp",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))await r.Session.RecoverAfterRestart();r.Reopen();Check(r.Ledger.HasPending,"R06 failed clear commit preserves durable barrier after reopen");
        }
        using(var r=new ManualRig("R07-next-tick"))
        {
            await r.Ready(true);await r.Session.Act(ScriptAction.Start);r.Counter.Value=10;r.Interaction.Confirmed=true;await r.Session.RecoverAfterRestart();int snapshots=r.Processes.SnapshotCalls;await r.Session.Refresh();Check(r.Processes.Starts==0&&r.Dispatcher.Calls==1&&r.Session.CurrentCatalog.Recovery.Single().AttemptsUtcTicks.Count==1,"R07 recovery callback refresh never launches reserves or changes budget");await r.Session.Reconcile();Check(r.Processes.SnapshotCalls>snapshots&&r.Dispatcher.Calls==1&&r.Processes.Starts==0,"R07 next ordinary tick fresh snapshot all gates checked preference still zero UAC");
        }
        foreach(var malformed in new[]{"missing","duplicate","unknown","zero-origin"})
        {
            using var r=new ManualRig("P03-context-"+malformed);await r.Pending();JsonEdit(r.Ledger.FilePath,n=>{var c=n["Requests"]![0]!["Context"]!;if(malformed=="missing")c.AsObject().Remove("Origin");if(malformed=="unknown")c["Unexpected"]=1;if(malformed=="zero-origin")c["Origin"]=0;});if(malformed=="duplicate"){var text=File.ReadAllText(r.Ledger.FilePath);File.WriteAllText(r.Ledger.FilePath,text.Replace("\"Kind\":", "\"Kind\":\"Legacy\",\"Kind\":"));}r.Reopen();await r.Session.Reconcile();Check(!r.Ledger.Ready&&r.Ledger.ReadOnly&&r.Processes.Starts==0&&r.Processes.Stops==0,"P03 "+malformed+" context fails closed backup consult only");
        }
        using(var r=new ManualRig("R07-multiple"))
        {
            await r.Pending();JsonEdit(r.Ledger.FilePath,n=>{var second=n["Requests"]![0]!.DeepClone();second["Nonce"]=Guid.NewGuid();second["ScriptPath"]=r.Session.Entries[1].Path;second["InterpreterPath"]=r.Session.Entries[1].Interpreter;second["Context"]=new JsonObject{["Kind"]="Legacy",["Origin"]=0,["Blob"]=""};n["Requests"]!.AsArray().Add(second);});r.Reopen();r.Counter.Value=10;r.Interaction.Confirmed=true;await r.Session.RecoverAfterRestart();await r.Session.Reconcile();
            Check(r.Ledger.Requests.Count==1&&r.Ledger.Find(r.Session.Entries[1].Path) is not null&&r.Processes.Starts==0&&r.Processes.Stops==0,"R07 exact nonce clear preserves other pending global automatic veto");
        }
        foreach(var fault in new[]{"anchor","binding","ledger-version","context-null"})
        {
            using var r=new ManualRig("P03-"+fault);await r.Pending();if(fault=="anchor")File.Delete(r.Ledger.AnchorPath);if(fault=="binding")JsonEdit(r.Ledger.AnchorPath,n=>n["LedgerId"]=Guid.NewGuid());if(fault=="ledger-version")JsonEdit(r.Ledger.FilePath,n=>n["Schema"]=3);if(fault=="context-null")JsonEdit(r.Ledger.FilePath,n=>n["Requests"]![0]!["Context"]=null);r.Reopen();await r.Session.Reconcile();
            Check(!r.Ledger.Ready&&r.Ledger.ReadOnly&&r.Processes.Starts==0&&r.Processes.Stops==0,"P03 "+fault+" remains blocked no empty recreation");
        }
        Check(ThrowsBoot(()=>new BootContextService(true,new WindowsUnbiasedCounter(),new FixtureContextProtector())),"D02 fixture rejects native counter before call");
        Check(ThrowsBoot(()=>new BootContextService(true,new FixtureUnbiasedCounter(),new WindowsContextProtector())),"D02 fixture rejects native protector before call");
        using(var r=new ManualRig("D-render"))
        {
            await r.Pending();r.Counter.Value=10;await r.Session.VerifyRequest();var window=new MainWindow(r.Session);window.ShowRequestDetails();
            Check(window.RecoverRequestControl.Content.ToString()=="Recuperar após reinicialização"&&window.RecoverRequestControl.IsEnabled,"D01 separate accessible recovery command");
            Bezoro.AhkManager.Presentation.Program.Render(window,Path.Combine(root,"recovery-min-100.png"),1000,720,1);window.ShowRequestDetails();Bezoro.AhkManager.Presentation.Program.Render(window,Path.Combine(root,"recovery-150.png"),1220,800,1.5);window.ShowRequestDetails();Bezoro.AhkManager.Presentation.Program.Render(window,Path.Combine(root,"recovery-200.png"),1000,720,2);window.Close();
        }
    }
}
