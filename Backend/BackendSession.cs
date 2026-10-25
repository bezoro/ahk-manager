using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.Backend;

internal interface IBackendSession : IAsyncDisposable
{
    event EventHandler<ManagerSnapshot>? Changed;
    Task<JsonElement> ExecuteAsync(string action, JsonElement payload, CancellationToken ct);
}

internal sealed class BackendSession : IBackendSession
{
    readonly IManagerService manager;
    readonly ProfileMigrationService migration;
    ManagerElevation? administration;
    readonly StartupTransferService startupTransfer;
    readonly Dictionary<Guid, (OperationPlan Plan, DateTimeOffset Time)> plans = [];
    readonly Dictionary<Guid, (ImportPreview Preview, DateTimeOffset Time)> imports = [];
    bool loaded, disposed;
    Task? disposal;
    public event EventHandler<ManagerSnapshot>? Changed { add => manager.Changed += value; remove => manager.Changed -= value; }
    public static BackendSession CreateProduction(string profileRoot, string hostExecutable, ParentProof parent)
    {
        var manager = ManagerService.CreateForTrustedStartupHost(profileRoot, hostExecutable, out var owner);
        var session = new BackendSession(manager, profileRoot, hostExecutable, () => owner.Held && owner.Trusted, new RegistryStartupAdapter());
        session.administration = new(parent, () => session.loaded && manager.Snapshot.EffectsAvailable && !manager.Snapshot.Scripts.Any(s => s.Issues.Any(i => i.Code == ReasonCode.PendingOperation)));
        return session;
    }
    internal BackendSession(IManagerService manager, string profileRoot, string hostExecutable, Func<bool> effectsAllowed, IUserStartupAdapter startup)
    {
        this.manager = manager;
        migration = new(profileRoot, effectsAllowed);
        startupTransfer = new(startup, hostExecutable, effectsAllowed, () => manager.Snapshot);
    }
    static T Read<T>(JsonElement payload, JsonTypeInfo<T> type) => payload.Deserialize(type) ?? throw new ProtocolException("InvalidRequest");
    static JsonElement Pack<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToElement(value, type);
    static void Empty(JsonElement payload) => _ = Read(payload, Protocol.Json.EmptyPayload);
    public async Task<JsonElement> ExecuteAsync(string action, JsonElement payload, CancellationToken ct)
    {
        if (disposed) throw new ProtocolException("ShuttingDown");
        Prune();
        if (action is "beginAdministration" or "administrationStatus" or "commitAdministration" or "cancelAdministration")
        {
            if (!loaded || administration is null) throw new ProtocolException("InvalidRequest");
            if (action == "administrationStatus") { var request = Read(payload, Protocol.Json.ElevationStatusPayload); return Pack(await administration.Status(request.OperationId, request.AfterRevision, ct).ConfigureAwait(false), Protocol.Json.ElevationStatus); }
            var ticket = Read(payload, Protocol.Json.ElevationOperationPayload);
            var status = action switch
            {
                "beginAdministration" => administration.Begin(ticket.OperationId),
                "commitAdministration" => await administration.Commit(ticket.OperationId, ct).ConfigureAwait(false),
                _ => administration.Cancel(ticket.OperationId)
            };
            return Pack(status, Protocol.Json.ElevationStatus);
        }
        if (administration?.Frozen == true && action is not ("snapshot" or "activity") && !(action == "shutdown" && administration.ShutdownAllowed)) throw new ProtocolException("PendingOperation");
        if (action == "previewMigration")
        {
            var request = Read(payload, Protocol.Json.MigrationPathPayload);
            var preview = await migration.PreviewAsync(request.SourceRoot, ct).ConfigureAwait(false);
            if (loaded) preview = preview with { CanMigrate = false, Issues = preview.Issues.Add(new(ReasonCode.ImportInvalid, "Migration must precede profile load.")) };
            return Pack(preview, Protocol.Json.MigrationPreview);
        }
        if (action == "migrate")
        {
            var request = Read(payload, Protocol.Json.ConfirmPreviewPayload);
            return Pack(loaded ? new OperationResult(Outcome.Failed, ReasonCode.ImportInvalid) : await migration.CommitAsync(request.PreviewId, request.Confirmed, ct).ConfigureAwait(false), Protocol.Json.OperationResult);
        }
        if (action == "load") { Empty(payload); await manager.LoadAsync(ct).ConfigureAwait(false); loaded = true; return Pack(manager.Snapshot, Protocol.Json.ManagerSnapshot); }
        if (action == "snapshot") { Empty(payload); return Pack(manager.Snapshot, Protocol.Json.ManagerSnapshot); }
        if (action == "shutdown") { Empty(payload); await DisposeAsync().ConfigureAwait(false); return Pack(new OperationResult(Outcome.Confirmed), Protocol.Json.OperationResult); }
        if (!loaded) throw new ProtocolException("InvalidRequest");
        switch (action)
        {
            case "refresh": Empty(payload); await manager.RefreshAsync(ct).ConfigureAwait(false); return Pack(manager.Snapshot, Protocol.Json.ManagerSnapshot);
            case "addFiles":
                var files = Read(payload, Protocol.Json.PathsPayload);
                if (files.Paths is null || files.Paths.Length is < 1 or > 2000 || files.Paths.Any(p => p is null || p.Length > 32760)) throw new ProtocolException("InvalidRequest");
                return Pack(await manager.AddFilesAsync(files.Paths, ct).ConfigureAwait(false), Protocol.Json.CatalogResult);
            case "addFolder": return Pack(await manager.AddFolderAsync(Read(payload, Protocol.Json.PathPayload).Path, ct).ConfigureAwait(false), Protocol.Json.CatalogResult);
            case "remove": return Pack(await manager.RemoveAsync(Nonempty(Read(payload, Protocol.Json.ScriptPayload).ScriptId), ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "relink":
                var relink = Read(payload, Protocol.Json.RelinkPayload); ValidateSelection(relink.Selection);
                return Pack(await manager.RelinkAsync(relink.Selection, relink.OldPath, relink.ReplacementPath, relink.StartupConsent, ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "prepareRecycle":
                var recyclePrepare = Read(payload, Protocol.Json.RecyclePreparePayload); ValidateSelection(recyclePrepare.Selection);
                return Pack(await manager.PrepareRecycleAsync(recyclePrepare.Selection, ct).ConfigureAwait(false), Protocol.Json.RecyclePlan);
            case "authorizeRecycle":
                var recycleAuthorize = Read(payload, Protocol.Json.RecycleAuthorizePayload); ValidateSelection(recycleAuthorize.Selection);
                return Pack(await manager.AuthorizeRecycleAsync(Nonempty(recycleAuthorize.OperationId), recycleAuthorize.Selection, recycleAuthorize.Confirmed, ct).ConfigureAwait(false), Protocol.Json.RecycleAuthorization);
            case "completeRecycle":
                var recycleComplete = Read(payload, Protocol.Json.RecycleCompletePayload);
                if (recycleComplete.Receipt is not (Outcome.Confirmed or Outcome.Failed or Outcome.Unknown) || recycleComplete.Detail?.Length > 16000) throw new ProtocolException("InvalidRequest");
                return Pack(await manager.CompleteRecycleAsync(Nonempty(recycleComplete.OperationId), recycleComplete.Receipt, recycleComplete.Detail, ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "saveMetadata":
                var metadata = Read(payload, Protocol.Json.MetadataPayload);
                return Pack(await manager.SaveMetadataAsync(Nonempty(metadata.ScriptId), metadata.Metadata ?? throw new ProtocolException("InvalidRequest"), metadata.StartupConsent, ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "saveCollection":
                var collection = Read(payload, Protocol.Json.CollectionPayload);
                return Pack(await manager.SaveCollectionAsync(collection.CollectionId, collection.Name, ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "removeCollection": return Pack(await manager.RemoveCollectionAsync(Nonempty(Read(payload, Protocol.Json.CollectionIdPayload).CollectionId), ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "prepare":
                var prepare = Read(payload, Protocol.Json.PreparePayload); ValidateSelection(prepare.Selection);
                if (!Enum.IsDefined(prepare.Operation) || plans.Count >= Protocol.MaximumPending) throw new ProtocolException("InvalidRequest");
                var plan = await manager.PrepareAsync(prepare.Selection, prepare.Operation, ct).ConfigureAwait(false); Guid planId = Guid.NewGuid();
                plans.Add(planId, (plan, DateTimeOffset.UtcNow)); return Pack(new PreparedResult(planId, plan), Protocol.Json.PreparedResult);
            case "perform":
                var perform = Read(payload, Protocol.Json.PerformPayload); ValidateSelection(perform.CurrentSelection);
                if (!plans.Remove(perform.PlanId, out var saved)) return Pack(new OperationResult(Outcome.Failed, ReasonCode.StaleSelection), Protocol.Json.OperationResult);
                return Pack(await manager.PerformAsync(saved.Plan, perform.CurrentSelection, perform.Confirmed, ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "saveSettings":
                var settings = Read(payload, Protocol.Json.SettingsPayload);
                return Pack(await manager.SaveSettingsAsync(settings.Settings ?? throw new ProtocolException("InvalidRequest"), settings.StartupConsent, ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "runLogin": Empty(payload); return Pack(await manager.RunLoginAsync(ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "activity":
                var query = Read(payload, Protocol.Json.ActivityPayload).Query;
                if (query is null || query.Search is null || query.Search.Length > 1000 || query.Outcome.HasValue && !Enum.IsDefined(query.Outcome.Value)) throw new ProtocolException("InvalidRequest");
                return Pack(await manager.GetActivityAsync(query, ct).ConfigureAwait(false), Protocol.Json.ImmutableArrayActivitySnapshot);
            case "previewImport":
                if (imports.Count >= Protocol.MaximumPending) throw new ProtocolException("QueueFull");
                var preview = await manager.PreviewImportAsync(Read(payload, Protocol.Json.ImportPathPayload).SourcePath, ct).ConfigureAwait(false); Guid previewId = Guid.NewGuid();
                imports.Add(previewId, (preview, DateTimeOffset.UtcNow)); return Pack(new ImportPrepared(previewId, preview), Protocol.Json.ImportPrepared);
            case "import":
                var importId = Read(payload, Protocol.Json.PreviewPayload).PreviewId;
                return Pack(imports.Remove(importId, out var import) ? await manager.ImportAsync(import.Preview, ct).ConfigureAwait(false) : new(Outcome.Failed, ReasonCode.ImportSourceChanged), Protocol.Json.OperationResult);
            case "restoreBackup": Empty(payload); return Pack(await manager.RestoreBackupAsync(ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "recoverPending": Empty(payload); return Pack(await manager.RecoverPendingAsync(ct).ConfigureAwait(false), Protocol.Json.OperationResult);
            case "previewStartupTransfer": Empty(payload); return Pack(startupTransfer.Preview(), Protocol.Json.StartupTransferPreview);
            case "transferStartup":
                var transfer = Read(payload, Protocol.Json.ConfirmPreviewPayload); return Pack(startupTransfer.Commit(transfer.PreviewId, transfer.Confirmed), Protocol.Json.OperationResult);
            default: throw new ProtocolException("UnknownAction");
        }
    }
    static Guid Nonempty(Guid id) => id != Guid.Empty ? id : throw new ProtocolException("InvalidRequest");
    static void ValidateSelection(SelectionContext selection)
    {
        if (selection is null || selection.ScriptId == Guid.Empty || selection.StateRevision < 0 || selection.Query is null || selection.Query.Version < 0 || selection.Query.Search is null || selection.Query.Search.Length > 1000 || !Enum.IsDefined(selection.Query.Status) || !Enum.IsDefined(selection.Query.Sort)) throw new ProtocolException("InvalidRequest");
    }
    void Prune()
    {
        foreach (var id in plans.Where(p => DateTimeOffset.UtcNow - p.Value.Time > Protocol.TokenLifetime).Select(p => p.Key).ToArray()) plans.Remove(id);
        foreach (var id in imports.Where(p => DateTimeOffset.UtcNow - p.Value.Time > Protocol.TokenLifetime).Select(p => p.Key).ToArray()) imports.Remove(id);
    }
    public ValueTask DisposeAsync() { disposed = true; administration?.Dispose(); return new(disposal ??= manager.DisposeAsync().AsTask()); }
}
