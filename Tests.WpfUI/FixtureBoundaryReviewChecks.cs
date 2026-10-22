using System.IO;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;
static partial class Tests
{
    static async Task RunFixtureBoundaryReview()
    {
        using var r=new ManualRig("fixture-native-boundary");var profile=Bytes(r.Store.FilePath);var ledger=Bytes(r.Ledger.FilePath);var native=new WindowsProcesses();bool refused=false;
        try{_=new UiSession(r.Store,native,r.Recycler,r.Interaction,()=>throw new InvalidOperationException("Must not discover"),r.Owner,r.Clock,r.Ledger,r.Dispatcher);}catch(ArgumentException e){refused=e.Message.Contains("antes de qualquer consulta");}
        Check(refused,"fixture/native process composition refused at constructor before query");
        Check(profile.SequenceEqual(Bytes(r.Store.FilePath))&&ledger.SequenceEqual(Bytes(r.Ledger.FilePath))&&r.Dispatcher.Calls==0&&r.Recycler.Calls==0,"mixed constructor refusal has zero writes or effects");
        foreach(string adapter in new[]{"recycler","interaction"})
        {
            refused=false;try{_=new UiSession(r.Store,r.Processes,adapter=="recycler"?new WindowsRecycler():r.Recycler,adapter=="interaction"?new DesktopInteraction():r.Interaction,()=>throw new InvalidOperationException("Must not discover"),r.Owner,r.Clock,r.Ledger,r.Dispatcher);}catch(ArgumentException e){refused=e.Message.Contains("antes de qualquer consulta");}
            Check(refused&&profile.SequenceEqual(Bytes(r.Store.FilePath))&&ledger.SequenceEqual(Bytes(r.Ledger.FilePath))&&r.Recycler.Calls==0,"fixture/native adapter composition refuses before effects:"+adapter);
        }
        var plain=r.Entry.Copy();plain.RunAsAdministrator=false;var result=await native.Start(plain,CancellationToken.None);
        Check(!result.Success&&result.Message.Contains("Autoridade"),"rejected fixture graph never installs native Start callback");
        refused=false;try{_=new UiSession(r.Store,r.Processes,r.Recycler,r.Interaction,()=>throw new InvalidOperationException("Must not discover"),r.Owner,r.Clock,r.Ledger,new WindowsElevationDispatcher());}catch(ArgumentException){refused=true;}
        Check(refused,"fixture native elevation dispatcher refused at composition before any OS query");
        var nonFixture=new NonFixtureOwner();refused=false;try{_=new UiSession(r.Store,native,r.Recycler,r.Interaction,()=>throw new InvalidOperationException("Must not discover"),nonFixture,r.Clock,r.Ledger);}catch(ArgumentException){refused=true;}
        Check(refused,"nonfixture owner cannot attach fixture authority before native callback installation");
        refused=false;try{_=new UiSession(r.Store,r.Processes,r.Recycler,r.Interaction,()=>throw new InvalidOperationException("Must not discover"),nonFixture,r.Clock,null,r.Dispatcher);}catch(ArgumentException){refused=true;}
        Check(refused,"owner and dispatcher mode mismatch refuses before authority resolution");
        result=await native.Start(plain,CancellationToken.None);
        Check(!result.Success&&result.Message.Contains("Autoridade"),"all rejected graphs leave native callback absent and refuse before OS");
        Check(profile.SequenceEqual(Bytes(r.Store.FilePath))&&ledger.SequenceEqual(Bytes(r.Ledger.FilePath))&&r.Dispatcher.Calls==0,"composition refusal preserves profile/ledger without UAC");
        await r.Ready();r.Dispatcher.Outcome=new(ElevationDisposition.Cancelled,"Fake UAC1223");await r.Session.Act(ScriptAction.Start);
        Check(r.Dispatcher.Calls==1&&!r.Ledger.HasPending&&r.Processes.Starts==0,"fully fake manual path still works after boundary guard");
        using(var rollback=new ManualRig("refresh-external-rollback"))
        {
            await rollback.Ready();var beforeMarker=Bytes(rollback.Ledger.FilePath);await rollback.Session.Act(ScriptAction.Start);var nonce=rollback.Ledger.Requests.Single().Nonce;File.WriteAllBytes(rollback.Ledger.FilePath,beforeMarker);
            var refreshed=rollback.Ledger.Refresh(rollback.Session.CurrentCatalog.Authority);await rollback.Session.Act(ScriptAction.Start);
            Check(!refreshed.Success&&rollback.Ledger.HasPending&&rollback.Ledger.Requests.Single().Nonce==nonce,"Refresh refuses earlier valid same-binding ledger and preserves known pending memory");
            Check(rollback.Dispatcher.Calls==1&&rollback.Processes.Starts==0&&beforeMarker.SequenceEqual(Bytes(rollback.Ledger.FilePath)),"Refresh rollback authorizes zero new dispatch and no overwrite");
        }
    }
}
