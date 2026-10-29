using System.Text.Json;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;
using static Bezoro.AhkManager.Backend.BackendRequestPayload;

namespace Bezoro.AhkManager.Backend;

/// <summary>Maps validated, loaded-session requests to the manager without client-owned plans.</summary>
internal sealed class BackendRequestDispatcher(
    IManagerService manager,
    StartupTransferService startupTransfer,
    bool startupAdoptionConfigured
)
{
    readonly SessionTokenStore<OperationPlan> plans = new();
    readonly SessionTokenStore<ImportPreview> imports = new();

    internal void Prune()
    {
        plans.Prune();
        imports.Prune();
    }

    internal async Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement payload,
        CancellationToken ct
    )
    {
        switch (action)
        {
            case "refresh":
                Empty(payload);
                await manager.RefreshAsync(ct).ConfigureAwait(false);
                return Pack(manager.Snapshot, Protocol.Json.ManagerSnapshot);
            case "addFiles":
                var files = Read(payload, Protocol.Json.PathsPayload);
                if (
                    files.Paths is null
                    || files.Paths.Length is < 1 or > 2000
                    || files.Paths.Any(p => p is null || p.Length > 32760)
                )
                    throw new ProtocolException("InvalidRequest");
                return Pack(
                    await manager.AddFilesAsync(files.Paths, ct).ConfigureAwait(false),
                    Protocol.Json.CatalogResult
                );
            case "addFolder":
                return Pack(
                    await manager
                        .AddFolderAsync(Read(payload, Protocol.Json.PathPayload).Path, ct)
                        .ConfigureAwait(false),
                    Protocol.Json.CatalogResult
                );
            case "remove":
                return Pack(
                    await manager
                        .RemoveAsync(
                            Nonempty(Read(payload, Protocol.Json.ScriptPayload).ScriptId),
                            ct
                        )
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "relink":
                var relink = Read(payload, Protocol.Json.RelinkPayload);
                ValidateSelection(relink.Selection);
                return Pack(
                    await manager
                        .RelinkAsync(
                            relink.Selection,
                            relink.OldPath,
                            relink.ReplacementPath,
                            relink.StartupConsent,
                            ct
                        )
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "prepareRecycle":
                var recyclePrepare = Read(payload, Protocol.Json.RecyclePreparePayload);
                ValidateSelection(recyclePrepare.Selection);
                return Pack(
                    await manager
                        .PrepareRecycleAsync(recyclePrepare.Selection, ct)
                        .ConfigureAwait(false),
                    Protocol.Json.RecyclePlan
                );
            case "authorizeRecycle":
                var recycleAuthorize = Read(payload, Protocol.Json.RecycleAuthorizePayload);
                ValidateSelection(recycleAuthorize.Selection);
                return Pack(
                    await manager
                        .AuthorizeRecycleAsync(
                            Nonempty(recycleAuthorize.OperationId),
                            recycleAuthorize.Selection,
                            recycleAuthorize.Confirmed,
                            ct
                        )
                        .ConfigureAwait(false),
                    Protocol.Json.RecycleAuthorization
                );
            case "completeRecycle":
                var recycleComplete = Read(payload, Protocol.Json.RecycleCompletePayload);
                if (
                    recycleComplete.Receipt
                        is not (Outcome.Confirmed or Outcome.Failed or Outcome.Unknown)
                    || recycleComplete.Detail?.Length > 16000
                )
                    throw new ProtocolException("InvalidRequest");
                return Pack(
                    await manager
                        .CompleteRecycleAsync(
                            Nonempty(recycleComplete.OperationId),
                            recycleComplete.Receipt,
                            recycleComplete.Detail,
                            ct
                        )
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "saveMetadata":
                var metadata = Read(payload, Protocol.Json.MetadataPayload);
                return Pack(
                    await manager
                        .SaveMetadataAsync(
                            Nonempty(metadata.ScriptId),
                            metadata.Metadata ?? throw new ProtocolException("InvalidRequest"),
                            metadata.StartupConsent,
                            ct
                        )
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "saveCollection":
                var collection = Read(payload, Protocol.Json.CollectionPayload);
                return Pack(
                    await manager
                        .SaveCollectionAsync(collection.CollectionId, collection.Name, ct)
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "removeCollection":
                return Pack(
                    await manager
                        .RemoveCollectionAsync(
                            Nonempty(Read(payload, Protocol.Json.CollectionIdPayload).CollectionId),
                            ct
                        )
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "prepare":
                var prepare = Read(payload, Protocol.Json.PreparePayload);
                ValidateSelection(prepare.Selection);
                if (!Enum.IsDefined(prepare.Operation) || plans.Count >= Protocol.MaximumPending)
                    throw new ProtocolException("InvalidRequest");
                var plan = await manager
                    .PrepareAsync(prepare.Selection, prepare.Operation, ct)
                    .ConfigureAwait(false);
                Guid planId = plans.Add(plan);
                return Pack(new PreparedResult(planId, plan), Protocol.Json.PreparedResult);
            case "perform":
                var perform = Read(payload, Protocol.Json.PerformPayload);
                ValidateSelection(perform.CurrentSelection);
                if (!plans.TryTake(perform.PlanId, out var saved))
                    return Pack(
                        new OperationResult(Outcome.Failed, ReasonCode.StaleSelection),
                        Protocol.Json.OperationResult
                    );
                return Pack(
                    await manager
                        .PerformAsync(saved, perform.CurrentSelection, perform.Confirmed, ct)
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "saveSettings":
                var settings = Read(payload, Protocol.Json.SettingsPayload);
                return Pack(
                    await manager
                        .SaveSettingsAsync(
                            settings.Settings ?? throw new ProtocolException("InvalidRequest"),
                            settings.StartupConsent,
                            ct
                        )
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "initializeStartup":
                Empty(payload);
                return Pack(
                    await manager
                        .SaveSettingsAsync(manager.Snapshot.Settings, true, ct)
                        .ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "runLogin":
                Empty(payload);
                return Pack(
                    startupAdoptionConfigured
                        ? new(Outcome.Confirmed, ReasonCode.StartupAlreadyProcessed)
                        : await manager.RunLoginAsync(ct).ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "startupContinuation":
                Empty(payload);
                return Pack(
                    manager is ManagerService concrete
                        ? await concrete.GetStartupContinuationAsync().ConfigureAwait(false)
                        : null,
                    Protocol.Json.StartupContinuationOffer
                );
            case "activity":
                var query = Read(payload, Protocol.Json.ActivityPayload).Query;
                if (
                    query is null
                    || query.Search is null
                    || query.Search.Length > 1000
                    || query.Outcome.HasValue && !Enum.IsDefined(query.Outcome.Value)
                )
                    throw new ProtocolException("InvalidRequest");
                return Pack(
                    await manager.GetActivityAsync(query, ct).ConfigureAwait(false),
                    Protocol.Json.ImmutableArrayActivitySnapshot
                );
            case "previewImport":
                if (imports.Count >= Protocol.MaximumPending)
                    throw new ProtocolException("QueueFull");
                var preview = await manager
                    .PreviewImportAsync(
                        Read(payload, Protocol.Json.ImportPathPayload).SourcePath,
                        ct
                    )
                    .ConfigureAwait(false);
                Guid previewId = imports.Add(preview);
                return Pack(new ImportPrepared(previewId, preview), Protocol.Json.ImportPrepared);
            case "import":
                var importId = Read(payload, Protocol.Json.PreviewPayload).PreviewId;
                return Pack(
                    imports.TryTake(importId, out var import)
                        ? await manager.ImportAsync(import, ct).ConfigureAwait(false)
                        : new(Outcome.Failed, ReasonCode.ImportSourceChanged),
                    Protocol.Json.OperationResult
                );
            case "restoreBackup":
                Empty(payload);
                return Pack(
                    await manager.RestoreBackupAsync(ct).ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "recoverPending":
                Empty(payload);
                return Pack(
                    await manager.RecoverPendingAsync(ct).ConfigureAwait(false),
                    Protocol.Json.OperationResult
                );
            case "previewStartupTransfer":
                Empty(payload);
                return Pack(startupTransfer.Preview(), Protocol.Json.StartupTransferPreview);
            case "transferStartup":
                var transfer = Read(payload, Protocol.Json.ConfirmPreviewPayload);
                return Pack(
                    startupTransfer.Commit(transfer.PreviewId, transfer.Confirmed),
                    Protocol.Json.OperationResult
                );
            default:
                throw new ProtocolException("UnknownAction");
        }
    }
}
