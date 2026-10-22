using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;

static partial class Tests
{
    static byte[] RetiredCatalog(UiSession session) => JsonSerializer.SerializeToUtf8Bytes(session.CurrentCatalog, TestJson.Default.Catalog);
    static void Retire(ScriptEntry entry)
    {
        entry.ManagementMode = ManagementMode.Retired; entry.StartWithWindows = false; entry.LaunchBlocked = true;
    }
    static TextBlock RetiredLabel(MainWindow window,string name) => (TextBlock)window.FindName(name);
    static bool NoSelectedActions(MainWindow window) => Enum.GetValues<ScriptAction>().All(action => !window.ActionButton(action).IsEnabled);
    static async Task RunRetiredVisibility()
    {
        using(var r=new FunctionalRig("mixed"))
        {
            var c=r.Store.Load(); var normal=c.Scripts[0];var external=c.Scripts[1];var retired=c.Scripts[2];
            normal.ManagementMode=ManagementMode.Normal;normal.StartWithWindows=true;
            external.ManagementMode=ManagementMode.ExternalPending;external.StartWithWindows=false;
            Retire(retired);retired.Path=Path.Combine(r.Directory,"harmless-scripts","startup.ahk");retired.Description="retired-only-marker";retired.DescriptionSource="Informada";retired.Interpreter=normal.Interpreter;retired.RunAsAdministrator=true;
            c.Runtime=new(){LoginKey=r.Owner.LoginKey,Decided=true};c.Runtime.Set(normal.Id,false);c.Runtime.Set(retired.Id,false);
            c.Recovery.Add(new(){ScriptId=retired.Id,LoginKey=r.Owner.LoginKey,Failures=2,Suspended=true,AttemptsUtcTicks=[r.Clock.UtcNow.Ticks],NextTryUtcTicks=r.Clock.UtcNow.AddMinutes(2).Ticks});
            string archive=Path.Combine(r.Directory,"recoverable-archive","startup.ahk");Directory.CreateDirectory(Path.GetDirectoryName(archive)!);File.WriteAllText(archive,"retired fixture backup; never executed");byte[] archived=File.ReadAllBytes(archive);
            r.Store.Save(c);r.Reload();byte[] before=RetiredCatalog(r.Session),primary=File.ReadAllBytes(r.Store.FilePath),ledger=File.ReadAllBytes(r.Session.Authority.FilePath);
            var window=new MainWindow(r.Session);
            Check(r.Session.Entries.Count==3&&r.Session.Entries.Any(e=>e.Id==retired.Id)&&r.Session.DisplayCount==2,"Entries retain complete catalog while display count excludes only Retired");
            Check(r.Session.Visible.Select(e=>e.Id).SequenceEqual(new[]{normal.Id,external.Id})&&window.ListControl.Items.Cast<ListBoxItem>().Select(row=>(Guid)row.Tag).SequenceEqual(new[]{normal.Id,external.Id}),"mixed normal/external/retired fixture filters actual cached list in original order");
            Check(RetiredLabel(window,"CountText").Text=="2 de 2","mixed list denominator counts non-retired entries only");
            r.Session.Select(retired.Id);
            Check(r.Session.SelectedId is null&&r.Session.Selected is null&&window.ListControl.SelectedItem is null&&NoSelectedActions(window),"explicit retired selection cannot populate details or enable selected-script actions");
            Check(RetiredLabel(window,"SelectedTitle").Text=="Selecione um script"&&window.DescriptionControl.Text=="","retired selection leaves empty detail content");
            window.SearchControl.Text="retired-only-marker";
            Check(r.Session.Visible.Count==0&&r.Session.Selected is null&&window.ListControl.Items.Count==0&&RetiredLabel(window,"CountText").Text=="0 de 2","search matching only retired metadata produces no visible or selected row");
            Check(RetiredLabel(window,"EmptyText").Text=="Nenhum resultado para esta busca"&&((FrameworkElement)window.FindName("EmptyPanel")).Visibility==Visibility.Visible,"eligible catalog with no search matches uses search-empty message");
            window.SearchControl.Text="brush";
            Check(r.Session.Visible.Count==1&&r.Session.Visible[0].Id==external.Id&&RetiredLabel(window,"CountText").Text=="1 de 2","external row retains normal search behavior and active denominator");
            window.SearchControl.Text="";r.Session.Select(normal.Id);
            Check(r.Session.Selected?.Id==normal.Id&&before.SequenceEqual(RetiredCatalog(r.Session))&&primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath))&&ledger.SequenceEqual(File.ReadAllBytes(r.Session.Authority.FilePath)),"filter/search/selection preserve complete record flags, metadata, runtime, recovery and durable bytes");
            await r.Session.Refresh();
            Check(r.Session.Status.Contains("2 scripts.",StringComparison.Ordinal)&&!r.Session.Status.Contains("3 scripts.",StringComparison.Ordinal),"refresh status reports active display count without changing discovery merge");
            Check(before.SequenceEqual(RetiredCatalog(r.Session))&&archived.SequenceEqual(File.ReadAllBytes(archive))&&!File.Exists(retired.Path),"legitimate fixture refresh retains complete retired record and recoverable archive without recreating script");
            Check(r.Processes.Starts==0&&r.Processes.Stops==0&&r.Recycler.Calls==0&&r.Interaction.Confirmations==0,"display correction causes zero process/recycle/confirmation effects");window.Close();
        }
        using(var r=new FunctionalRig("retired-first-registration"))
        {
            var c=r.Store.Load();var first=c.Scripts[0];Retire(first);c.Scripts[1].ManagementMode=ManagementMode.ExternalPending;c.Scripts[1].StartWithWindows=false;
            r.Store.Save(c);r.Reload();var window=new MainWindow(r.Session);var eligible=c.Scripts[1];
            Check(r.Session.SelectedId==eligible.Id&&r.Session.Selected?.Id==eligible.Id&&((ListBoxItem?)window.ListControl.SelectedItem)?.Tag is Guid id&&id==eligible.Id,"retired-first initialization chooses first non-retired catalog entry");
            byte[] before=RetiredCatalog(r.Session),primary=File.ReadAllBytes(r.Store.FilePath);r.Interaction.ScriptChoice=first.Path;r.Session.Register();
            Check(r.Session.SelectedId is null&&r.Session.Selected is null&&window.ListControl.SelectedItem is null&&NoSelectedActions(window),"already-cataloged retired registration cannot restore hidden selection or actions");
            Check(before.SequenceEqual(RetiredCatalog(r.Session))&&primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath))&&r.Session.Entries.Count==3&&r.Processes.Starts==0&&r.Processes.Stops==0,"retired registration neither deletes record nor re-registers/changes preferences or effects");window.Close();
        }
        using(var r=new FunctionalRig("stale-selection"))
        {
            Guid stale=r.Session.SelectedId!.Value;Retire(r.Entry);r.Store.Save(r.Session.CurrentCatalog);var window=new MainWindow(r.Session);
            Check(r.Session.SelectedId==stale&&r.Session.Selected is null&&window.ListControl.SelectedItem is null&&NoSelectedActions(window),"stale retired selected ID is excluded by shared detail predicate");
            r.Session.SetSearch("");Check(window.ListControl.Items.Cast<ListBoxItem>().All(row=>(Guid)row.Tag!=stale)&&r.Session.Selected is null,"repaint never restores cached retired row or stale details");window.Close();
        }
        using(var r=new FunctionalRig("all-retired"))
        {
            var c=r.Store.Load();foreach(var entry in c.Scripts)Retire(entry);r.Store.Save(c);r.Reload();byte[] before=RetiredCatalog(r.Session);var window=new MainWindow(r.Session);
            Check(r.Session.Entries.Count==3&&r.Session.Visible.Count==0&&r.Session.DisplayCount==0&&r.Session.SelectedId is null&&r.Session.Selected is null,"all-retired keeps stored records but starts without selection");
            Check(window.ListControl.Items.Count==0&&window.ListControl.SelectedItem is null&&RetiredLabel(window,"CountText").Text=="0 de 0"&&RetiredLabel(window,"EmptyText").Text=="Nenhum script cadastrado"&&((FrameworkElement)window.FindName("EmptyPanel")).Visibility==Visibility.Visible,"all-retired actual catalog shows true empty state and zero denominator");
            Check(NoSelectedActions(window)&&window.DescriptionControl.Text=="","all-retired has no selected-script action or details");window.SearchControl.Text="desktop";
            Check(RetiredLabel(window,"EmptyText").Text=="Nenhum script cadastrado"&&RetiredLabel(window,"CountText").Text=="0 de 0","all-retired search still reports true empty catalog");await r.Session.Refresh();
            Check(r.Session.Status.Contains("0 scripts.",StringComparison.Ordinal)&&before.SequenceEqual(RetiredCatalog(r.Session))&&r.Processes.Starts==0&&r.Processes.Stops==0,"all-retired refresh count is zero with retained catalog and no effects");window.Close();
        }
    }
}
