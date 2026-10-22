using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;

static partial class Tests
{
    static async Task RunLedgerSchema()
    {
        foreach(int schema in new[]{1,2,3})
        {
            using var r=new ManualRig("ledger-conversion-"+schema);await r.Ready(true);
            var original=r.Store.Load();original.Recovery.Add(new(){ScriptId=original.Scripts[0].Id,LoginKey=r.Owner.LoginKey,AttemptsUtcTicks=[r.Clock.UtcNow.Ticks],Failures=2,NextTryUtcTicks=r.Clock.UtcNow.AddMinutes(2).Ticks,Suspended=true});r.Store.Save(original);
            LegacyPayload(r.Store,original,schema);r.Reopen();
            var converted=r.Session.CurrentCatalog;
            Check(r.Store.LoadedSchema==schema&&r.Session.NeedsSchemaConversion&&converted.Schema==5,"loaded schema retained before explicit conversion:"+schema);
            Check(converted.Scripts.Zip(original.Scripts).All(x=>x.First.Id==x.Second.Id&&x.First.Path==x.Second.Path&&x.First.Interpreter==x.Second.Interpreter&&x.First.Description==x.Second.Description&&x.First.RunAsAdministrator==(schema==3&&x.Second.RunAsAdministrator)),"schema metadata and preference conversion:"+schema);
            Check(schema==1?converted.Scripts.All(e=>e.RequestedEnabled is null&&e.ManagementMode==ManagementMode.Unmanaged):converted.Scripts.Zip(original.Scripts).All(x=>x.First.RequestedEnabled==x.Second.RequestedEnabled&&x.First.ManagementMode==x.Second.ManagementMode),"conversion preserves explicit intent without inference:"+schema);
            if(schema>1)Check(converted.Recovery.Single().Suspended&&converted.Recovery.Single().Failures==2&&converted.Recovery.Single().AttemptsUtcTicks.SequenceEqual(original.Recovery.Single().AttemptsUtcTicks)&&converted.Migration.JournalDigest==original.Migration.JournalDigest,"conversion preserves migration and recovery:"+schema);
            var bytes=Bytes(r.Store.FilePath);var backup=Bytes(r.Store.FilePath+".bak");r.Interaction.Confirmed=false;await r.Session.ConvertSchema();
            Check(bytes.SequenceEqual(Bytes(r.Store.FilePath))&&backup.SequenceEqual(Bytes(r.Store.FilePath+".bak")),"conversion Cancel zero writes:"+schema);
            r.Interaction.Confirmed=true;await r.Session.ConvertSchema();
            Check(r.Store.LoadedSchema==5&&r.Store.VerifyPrimary(r.Session.CurrentCatalog)&&r.Ledger.Verify(r.Session.CurrentCatalog.Authority).Success,"authority bound before durable schema4:"+schema);
            Check(r.Dispatcher.Calls==0&&r.Processes.Starts==0&&r.Processes.Stops==0,"conversion has no process effects:"+schema);
        }
        foreach(string kind in new[]{"missing","duplicate","null","id","sid","mode","version","old-binding"})
        {
            using var r=new ManualRig("bad-binding-"+kind);r.Store.Save(r.Store.Load());var node=JsonNode.Parse(Bytes(r.Store.FilePath))!.AsObject();
            if(kind=="missing")node.Remove("Authority");else if(kind=="null")node["Authority"]=null;else if(kind=="id")node["Authority"]!["LedgerId"]=Guid.Empty.ToString();else if(kind=="sid")node["Authority"]!["Sid"]="";else if(kind=="mode")node["Authority"]!["Fixture"]=false;else if(kind=="version")node["Authority"]!["Version"]=3;else if(kind=="old-binding")node["Schema"]=3;
            string text=node.ToJsonString();if(kind=="duplicate")text=text.Replace("\"Authority\":{","\"Authority\":null,\"Authority\":{",StringComparison.Ordinal);File.WriteAllText(r.Store.FilePath,text);var primary=Bytes(r.Store.FilePath);var backup=Bytes(r.Store.FilePath+".bak");r.Reopen();await r.Session.Reconcile();await r.Session.Act(ScriptAction.Start);
            Check((r.Store.ReadOnly||!r.Ledger.Verify(r.Session.CurrentCatalog.Authority).Success)&&r.Dispatcher.Calls==0&&r.Processes.Starts==0&&r.Processes.Stops==0,"invalid schema binding fails closed:"+kind);
            Check(primary.SequenceEqual(Bytes(r.Store.FilePath))&&backup.SequenceEqual(Bytes(r.Store.FilePath+".bak")),"invalid binding preserves primary and backup:"+kind);
        }
        foreach(string kind in new[]{"missing-ledger","missing-anchor","corrupt-ledger","corrupt-anchor","ledger-id","ledger-sid","ledger-mode","raw-whitespace","partial"})
        {
            using var r=new ManualRig("authority-"+kind);await r.Pending();var ledger=Bytes(r.Ledger.FilePath);var anchor=Bytes(r.Ledger.AnchorPath);
            if(kind=="missing-ledger")File.Delete(r.Ledger.FilePath);else if(kind=="missing-anchor")File.Delete(r.Ledger.AnchorPath);else if(kind=="corrupt-ledger")File.WriteAllText(r.Ledger.FilePath,"{");else if(kind=="corrupt-anchor")File.WriteAllText(r.Ledger.AnchorPath,"{");else if(kind=="partial"){File.Delete(r.Ledger.FilePath);File.WriteAllText(r.Ledger.FilePath+".tmp","partial");}else if(kind=="raw-whitespace"){File.AppendAllText(r.Ledger.FilePath," ");Check(!r.Ledger.Verify(r.Session.CurrentCatalog.Authority).Success,"ledger raw fingerprint catches whitespace mutation");}else{var node=JsonNode.Parse(ledger)!;node["Binding"]![kind=="ledger-id"?"LedgerId":kind=="ledger-sid"?"Sid":"Fixture"]=kind=="ledger-id"?JsonValue.Create(Guid.NewGuid().ToString()):kind=="ledger-sid"?JsonValue.Create("other-user"):JsonValue.Create(false);File.WriteAllText(r.Ledger.FilePath,node.ToJsonString());}
            if(kind=="raw-whitespace")continue;
            var damaged=File.Exists(r.Ledger.FilePath)?Bytes(r.Ledger.FilePath):null;r.Reopen();await r.Session.Act(ScriptAction.Start);await r.Session.Reconcile();
            Check(!r.Ledger.Ready&&r.Ledger.ReadOnly&&r.Dispatcher.Calls==1&&r.Processes.Starts==0&&r.Processes.Stops==0,"authority history failure blocks instead of empty recreation:"+kind);
            Check(damaged is null?!File.Exists(r.Ledger.FilePath):damaged.SequenceEqual(Bytes(r.Ledger.FilePath)),"authority failure preserves bytes:"+kind);
            var newProfile=Path.Combine(r.Directory,"fresh-profile","catalog.json");var next=RequestLedger.OpenFixture(r.Namespace,r.Owner,newProfile,newProfile+".bak");Check(!next.Ready,"new profile cannot reset damaged authority:"+kind);
        }
        using(var r=new ManualRig("conversion-raw-race"))
        {
            r.Store.Save(r.Store.Load());LegacyPayload(r.Store,r.Store.Load(),3);r.Reopen();r.Interaction.HoldConfirmation=new();var conversion=r.Session.ConvertSchema();File.AppendAllText(r.Store.FilePath," ");var primary=Bytes(r.Store.FilePath);var backup=Bytes(r.Store.FilePath+".bak");r.Interaction.HoldConfirmation.SetResult(true);await conversion;
            Check(primary.SequenceEqual(Bytes(r.Store.FilePath))&&backup.SequenceEqual(Bytes(r.Store.FilePath+".bak"))&&r.Store.LoadedSchema==3,"schema3 conversion raw race preserves both files");
        }
        var empty=Path.Combine(root,"fresh-authority");var owner=new FixtureOwner();var created=RequestLedger.OpenFixture(empty,owner);var firstAnchor=Bytes(created.AnchorPath);var reopened=RequestLedger.OpenFixture(empty,owner);
        Check(created.Ready&&reopened.Ready&&created.Binding!.LedgerId==reopened.Binding!.LedgerId&&firstAnchor.SequenceEqual(Bytes(reopened.AnchorPath)),"legitimate first authority initializes durably and immutable anchor survives reopen");
        Check(ThrowsFixture(()=>RequestLedger.OpenFixture(Path.Combine(root,"not-fixture"),new NonFixtureOwner())),"fixture namespace rejects nonfixture owner before real namespace");owner.Dispose();
    }
    static bool ThrowsFixture(Action action){try{action();return false;}catch(ArgumentException){return true;}}
    sealed class NonFixtureOwner:IEffectOwner {public bool Held=>true;public bool Trusted=>true;public bool Fixture=>false;public string Sid=>"fake";public string LoginKey=>"fake";public void Dispose(){} }
}
