using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.WinUI.Resources;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.WinUI.ViewModels;

public sealed partial class ManagerViewModel : ObservableObject
{
    readonly IManagerService service;
    public ObservableCollection<ScriptRow> Rows { get; } = [];
    public ManagerSnapshot Snapshot { get; private set; } = ManagerSnapshot.Empty;
    public ScriptQuery Query { get; private set; } = new();
    public Guid? SelectedId { get; private set; }
    public Guid? EffectiveSelectedId => ScriptViews.Project(Snapshot, Query, SelectedId).EffectiveSelectedId;
    public ScriptSnapshot? Selected => ScriptViews.Project(Snapshot, Query, SelectedId).VisibleIds.Contains(SelectedId ?? Guid.Empty) ? Snapshot.Scripts.FirstOrDefault(s => s.Id == SelectedId) : null;
    public Dictionary<Guid, MetadataDraft> Drafts { get; } = [];
    [ObservableProperty] public partial bool Busy { get; set; }
    public event EventHandler? PresentationChanged;
    public event EventHandler<Exception>? Error;
    public ManagerViewModel(IManagerService service) => this.service = service;
    public void Apply(ManagerSnapshot snapshot)
    {
        Snapshot = snapshot;
        Project();
    }
    public void ChangeQuery(string search, StatusFilter status, ScriptSort sort, Guid? collection)
    {
        Query = new(search, status, sort, collection, Query.Version + 1);
        Project();
    }
    public void Select(Guid? id)
    {
        if (Busy) return;
        SelectedId = id;
        Project();
    }
    public SelectionContext? Selection() => EffectiveSelectedId is Guid id ? new(id, Query, Snapshot.Revision) : null;
    public MetadataDraft? GetDraft() => Selected is {} selected
        ? Drafts.TryGetValue(selected.Id, out var draft) ? draft : Drafts[selected.Id] = new(selected)
        : null;
    void Project()
    {
        var projection = ScriptViews.Project(Snapshot, Query, SelectedId);
        if (!Busy && !projection.VisibleIds.Contains(SelectedId ?? Guid.Empty)) SelectedId = null;
        var ids = projection.VisibleIds;
        for (int i = Rows.Count - 1; i >= 0; --i) if (!ids.Contains(Rows[i].Id)) Rows.RemoveAt(i);
        for (int index = 0; index < ids.Length; ++index)
        {
            var script = Snapshot.Scripts.First(s => s.Id == ids[index]);
            var row = Rows.FirstOrDefault(r => r.Id == script.Id);
            if (row is null) { row = new(script); Rows.Insert(index, row); }
            else { int previous = Rows.IndexOf(row); if (previous != index) Rows.Move(previous, index); row.Apply(script); }
            row.CanEdit = !Snapshot.ReadOnly && !Busy;
        }
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnBusyChanged(bool value) => Project();
    [RelayCommand]
    async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        try { await service.RefreshAsync(); }
        catch (Exception error) { Error?.Invoke(this, error); }
        finally { Busy = false; }
    }
}

public sealed partial class ScriptRow : ObservableObject
{
    public Guid Id { get; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string MainHotkey { get; set; } = "";
    [ObservableProperty] public partial string Privilege { get; set; } = "";
    [ObservableProperty] public partial bool Startup { get; set; }
    [ObservableProperty] public partial bool CanEdit { get; set; }
    public string StartupOn => Text.Get("On");
    public string StartupOff => Text.Get("Off");
    public string StartupLabel => Text.Get("StartupScript") + ": " + Name;
    public ScriptRow(ScriptSnapshot script) { Id = script.Id; Apply(script); }
    public void Apply(ScriptSnapshot script)
    {
        Name = script.FriendlyName; FileName = Path.GetFileName(script.Path); Status = Text.Status(script.Status);
        MainHotkey = script.Hotkeys.FirstOrDefault()?.Chord ?? "—";
        Privilege = script.RunAsAdministrator ? Text.Get("AdministratorRequested") : Text.Get("StandardPrivilege");
        Startup = script.RunAtLogin == true;
    }
}

public sealed class MetadataDraft(ScriptSnapshot script)
{
    public string Name { get; set; } = script.FriendlyName;
    public string Description { get; set; } = script.Description;
    public Guid? CollectionId { get; set; } = script.CollectionId;
    public bool? Startup { get; set; } = script.RunAtLogin;
    public bool Admin { get; set; } = script.RunAsAdministrator;
    public bool Blocked { get; set; } = script.LaunchBlocked;
    public string Hotkeys { get; set; } = string.Join(Environment.NewLine, script.Hotkeys.Where(h => h.Origin == HotkeyOrigin.Manual).Select(h => h.Chord));
    public ScriptMetadata ToMetadata() => new(Name, Description, CollectionId, Startup, Admin, Blocked, [.. Hotkeys.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
}

public sealed record DetailRow(string Title, string Detail);
public sealed record CollectionChoice(Guid? Id, string Name);
