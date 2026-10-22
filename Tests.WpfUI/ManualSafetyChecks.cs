using System.IO;
using System.Text.Json.Nodes;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
static partial class Tests
{
    static async Task RunManualSafety()
    {
        foreach(string change in new[]{"shutdown","owner","duplicate","unknown"})
        {
            using var r=new ManualRig("prompt-"+change);await r.Ready();r.Interaction.HoldConfirmation=new(TaskCreationOptions.RunContinuationsAsynchronously);var primary=Bytes(r.Store.FilePath);var ledger=Bytes(r.Ledger.FilePath);var task=r.Session.Act(ScriptAction.Start);
            if(change=="shutdown")await r.Session.PrepareExit(TimeSpan.Zero);else if(change=="owner")r.Owner.Held=false;else r.Processes.Current=change=="unknown"?new([new(99,0,"",null)],false,"fake unknown"):new([new(8500,132000000000000000,r.Entry.Interpreter,r.Entry.Path)],true,"fake duplicate");
            r.Interaction.HoldConfirmation.SetResult(true);await task;
            Check(r.Dispatcher.Calls==0&&r.Processes.Starts==0&&r.Processes.Stops==0&&!r.Ledger.HasPending&&!r.Session.Busy,"confirmation race revalidates before marker/OS:"+change);
            Check(primary.SequenceEqual(Bytes(r.Store.FilePath))&&ledger.SequenceEqual(Bytes(r.Ledger.FilePath)),"confirmation race preserves profile/authority:"+change);
        }
        using(var r=new ManualRig("checked-cooldown"))
        {
            await r.Ready(true);var c=r.Store.Load();c.Recovery.Add(new(){ScriptId=r.Entry.Id,LoginKey=r.Owner.LoginKey,Failures=1,NextTryUtcTicks=r.Clock.UtcNow.AddMinutes(1).Ticks});r.Store.Save(c);r.Reopen();await r.Session.Refresh();await r.Session.Act(ScriptAction.Start);await r.Session.Act(ScriptAction.Restart);
            Check(r.Dispatcher.Calls==0&&r.Processes.Stops==0&&!r.Ledger.HasPending&&r.Store.Load().Recovery.Single().AttemptsUtcTicks.Count==0,"checked manual cooldown refuses before stop marker or budget");
        }
        foreach(string change in new[]{"preference","interpreter","sid","anchor"})
        {
            using var r=new ManualRig("permit-"+change);await r.Ready();r.Dispatcher.BeforeConsume=_=>
            {
                if(change=="preference")r.Session.Entries[0].RunAsAdministrator=false;else if(change=="interpreter")r.Session.Entries[0].Interpreter=Path.Combine(r.Directory,"other","AutoHotkey64.exe");else if(change=="sid")r.Owner.Sid="other-owner";else File.AppendAllText(r.Ledger.AnchorPath," ");
            };await r.Session.Act(ScriptAction.Start);
            Check(r.Dispatcher.Calls==0&&r.Processes.Starts==0,"permit live context mismatch zero invocation:"+change);
        }
        using(var r=new ManualRig("all-authority-removed"))
        {
            await r.Pending();File.Delete(r.Ledger.FilePath);File.Delete(r.Ledger.AnchorPath);File.Delete(r.Ledger.FilePath+".bak");r.Reopen();
            Check(!r.Ledger.Ready&&!File.Exists(r.Ledger.FilePath)&&!File.Exists(r.Ledger.AnchorPath),"linked schema4 catalog prevents recreating lost authority and anchor together");
        }
        using(var r=new ManualRig("readonly-authority-effects"))
        {
            await r.Pending();File.WriteAllText(r.Ledger.FilePath,"{");r.Reopen();var other=r.Session.Entries[1];r.Processes.Current=new([new(8600,132000000000000000,other.Interpreter,other.Path)],true,"fake other");await r.Session.Refresh();r.Session.Select(other.Id);var primary=Bytes(r.Store.FilePath);await r.Session.Act(ScriptAction.Stop);await r.Session.SetIntent(false);await r.Session.Reconcile();
            Check(!r.Ledger.Ready&&r.Processes.Stops==0&&r.Processes.Starts==0&&r.Dispatcher.Calls==1,"invalid authority blocks all manual/automatic effects including another path");
            Check(primary.SequenceEqual(Bytes(r.Store.FilePath)),"invalid authority refuses intent persistence before write");
        }
        foreach(string malformed in new[]{"duplicate-candidate","missing-candidate","unknown-request","duplicate-nonce"})
        {
            using var r=new ManualRig("malformed-"+malformed);await r.Pending();var node=JsonNode.Parse(Bytes(r.Ledger.FilePath))!;var request=node["Requests"]![0]!.AsObject();request["Candidate"]=new JsonObject{{"Pid",8700},{"Created",132000000000000000L},{"Executable",r.Entry.Interpreter}};
            if(malformed=="missing-candidate")request["Candidate"]!.AsObject().Remove("Created");if(malformed=="unknown-request")request["Typo"]=true;if(malformed=="duplicate-nonce")node["Requests"]!.AsArray().Add(request.DeepClone());string text=node.ToJsonString();if(malformed=="duplicate-candidate")text=text.Replace("\"Pid\":8700","\"Pid\":8700,\"Pid\":8701",StringComparison.Ordinal);File.WriteAllText(r.Ledger.FilePath,text);var primary=Bytes(r.Ledger.FilePath);r.Reopen();await r.Session.Act(ScriptAction.Start);
            Check(!r.Ledger.Ready&&primary.SequenceEqual(Bytes(r.Ledger.FilePath))&&r.Dispatcher.Calls==1,"malformed request remains consult-only barrier:"+malformed);
        }
        var absent=Path.Combine(root,"no-owner-init");var owner=new FixtureOwner{Held=false};var noOwner=RequestLedger.OpenFixture(absent,owner);Check(!noOwner.Ready&&!File.Exists(noOwner.FilePath)&&!File.Exists(noOwner.AnchorPath),"unowned first init has zero authority writes");owner.Dispose();
    }
}
