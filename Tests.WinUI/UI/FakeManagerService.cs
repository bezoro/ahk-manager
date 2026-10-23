using System.Collections.Immutable;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

internal sealed class FakeManagerService : IManagerService
{
    public ManagerSnapshot Snapshot { get; private set; }
    public event EventHandler<ManagerSnapshot>? Changed;
    public int Effects, RecoveryCalls,Disposals;
    public bool Confirmation = true;
    public List<(Guid? Id,string Name)> SavedCollections {get;}=[];
    public TaskCompletionSource? PerformEntered, ReleasePerform;
    public TaskCompletionSource? LoadEntered,ReleaseLoad;
    public TaskCompletionSource? LoginEntered,ReleaseLogin;
    public TaskCompletionSource? ReleaseRefresh;
    public FakeManagerService(ManagerSnapshot snapshot) => Snapshot = snapshot;
    public void Publish(ManagerSnapshot snapshot) { Snapshot = snapshot; Changed?.Invoke(this,snapshot); }
    public async Task LoadAsync(CancellationToken cancellationToken=default) {LoadEntered?.TrySetResult();if(ReleaseLoad is not null)await ReleaseLoad.Task;}
    public async Task RefreshAsync(CancellationToken cancellationToken=default) {if(ReleaseRefresh is not null)await ReleaseRefresh.Task;}
    static Task<OperationResult> Done() => Task.FromResult(new OperationResult(Outcome.Confirmed));
    public Task<CatalogResult> AddFilesAsync(IReadOnlyList<string> paths,CancellationToken cancellationToken=default)=>Task.FromResult(new CatalogResult(new(Outcome.Confirmed),[],0,[]));
    public Task<CatalogResult> AddFolderAsync(string path,CancellationToken cancellationToken=default)=>AddFilesAsync([],cancellationToken);
    public Task<OperationResult> RemoveAsync(Guid scriptId,CancellationToken cancellationToken=default)=>Done();
    public Task<OperationResult> SaveMetadataAsync(Guid scriptId,ScriptMetadata metadata,bool startupConsent=false,CancellationToken cancellationToken=default)=>Done();
    public Task<OperationResult> SaveCollectionAsync(Guid? collectionId,string name,CancellationToken cancellationToken=default){SavedCollections.Add((collectionId,name));return Done();}
    public Task<OperationResult> RemoveCollectionAsync(Guid collectionId,CancellationToken cancellationToken=default)=>Done();
    public Task<OperationPlan> PrepareAsync(SelectionContext selection,ScriptOperation operation,CancellationToken cancellationToken=default) => Task.FromResult(new OperationPlan(Guid.NewGuid(),selection.ScriptId,operation,Snapshot.Revision,selection.Query.Version,null,true,Confirmation,ReasonCode.None,selection));
    public async Task<OperationResult> PerformAsync(OperationPlan plan,SelectionContext currentSelection,bool confirmed,CancellationToken cancellationToken=default)
    {
        PerformEntered?.TrySetResult();
        if(ReleasePerform is not null)await ReleasePerform.Task;
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.StateRevision!=Snapshot.Revision || plan.QueryVersion!=currentSelection.Query.Version || plan.ScriptId!=currentSelection.ScriptId) return new OperationResult(Outcome.Failed,ReasonCode.StaleSelection);
        Effects++;return await Done();
    }
    public Task<OperationResult> SaveSettingsAsync(ManagerSettings settings,bool startupConsent=false,CancellationToken cancellationToken=default) {Publish(Snapshot with {Settings=settings,Revision=Snapshot.Revision+1});return Done();}
    public async Task<OperationResult> RunLoginAsync(CancellationToken cancellationToken=default){LoginEntered?.TrySetResult();if(ReleaseLogin is not null)await ReleaseLogin.Task;return await Done();}
    public Task<ImmutableArray<ActivitySnapshot>> GetActivityAsync(ActivityQuery query,CancellationToken cancellationToken=default)=>Task.FromResult(Snapshot.Activity);
    public Task<ImportPreview> PreviewImportAsync(string sourcePath,CancellationToken cancellationToken=default)=>Task.FromResult(new ImportPreview(sourcePath,"",0,[],false));
    public Task<OperationResult> ImportAsync(ImportPreview preview,CancellationToken cancellationToken=default)=>Done();
    public Task<OperationResult> RestoreBackupAsync(CancellationToken cancellationToken=default)=>Done();
    public Task<OperationResult> RecoverPendingAsync(CancellationToken cancellationToken=default) {RecoveryCalls++;return Done();}
    public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
}
