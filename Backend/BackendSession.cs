using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;
using static Bezoro.AhkManager.Backend.BackendRequestPayload;

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
    readonly BackendAuthorityRequests authority;
    readonly BackendRequestDispatcher dispatcher;
    readonly PortableProfileSession? portableProfile;
    bool loaded,
        disposed;
    Task? disposal;
    public event EventHandler<ManagerSnapshot>? Changed
    {
        add => manager.Changed += value;
        remove => manager.Changed -= value;
    }

    public static BackendSession CreateProduction(
        string profileRoot,
        string hostExecutable,
        ParentProof parent,
        string? startupDescriptor = null
    )
    {
        var layout = RuntimeProfileLayout.ForHost(hostExecutable);
        if (!ScriptPaths.Equal(layout.ProfileRoot, profileRoot))
            throw new ProtocolException("ParentMismatch");
        var manager = ManagerService.CreateForTrustedStartupHost(
            profileRoot,
            layout.StartupHostExecutable,
            out var owner
        );
        var session = new BackendSession(
            manager,
            profileRoot,
            layout.StartupHostExecutable,
            () => owner.Held && owner.Trusted,
            new RegistryStartupAdapter(),
            startupDescriptor is null
                ? null
                : new StartupAdoption(StartupAdoptionDescriptor.Parse(startupDescriptor), parent),
            layout
        );
        session.authority.Administration = new(
            parent,
            () =>
                session.loaded
                && !session.authority.AdoptionPending
                && manager.Snapshot.EffectsAvailable
                && !manager.Snapshot.Scripts.Any(s =>
                    s.Issues.Any(i => i.Code == ReasonCode.PendingOperation)
                ),
            manager.CommitStartupContinuationAsync
        );
        return session;
    }

    internal BackendSession(
        IManagerService manager,
        string profileRoot,
        string hostExecutable,
        Func<bool> effectsAllowed,
        IUserStartupAdapter startup,
        StartupAdoption? startupAdoption = null,
        RuntimeProfileLayout? layout = null
    )
    {
        this.manager = manager;
        authority = new(manager, startupAdoption);
        migration = new(profileRoot, effectsAllowed);
        if (layout is not null)
            portableProfile = new(layout, effectsAllowed, () => manager.Snapshot);
        var startupTransfer = new StartupTransferService(
            startup,
            hostExecutable,
            effectsAllowed,
            () => manager.Snapshot
        );
        dispatcher = new(manager, startupTransfer, authority.AdoptionConfigured);
    }

    public async Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement payload,
        CancellationToken ct
    )
    {
        if (disposed)
            throw new ProtocolException("ShuttingDown");
        dispatcher.Prune();
        if (
            authority.AdoptionPending
            && action
                is not (
                    "load"
                    or "initializeProfile"
                    or "snapshot"
                    or "activity"
                    or "shutdown"
                    or "adoptStartupContinuation"
                    or "startupAdoptionStatus"
                )
        )
            throw new ProtocolException("PendingOperation");
        if (BackendAuthorityRequests.Handles(action))
            return await authority.ExecuteAsync(action, payload, loaded, ct).ConfigureAwait(false);
        if (
            authority.Frozen
            && action is not ("snapshot" or "activity")
            && !(action == "shutdown" && authority.ShutdownAllowed)
        )
            throw new ProtocolException("PendingOperation");
        if (action == "previewMigration")
        {
            var request = Read(payload, Protocol.Json.MigrationPathPayload);
            var preview = await migration
                .PreviewAsync(request.SourceRoot, ct)
                .ConfigureAwait(false);
            if (loaded)
                preview = preview with
                {
                    CanMigrate = false,
                    Issues = preview.Issues.Add(
                        new(ReasonCode.ImportInvalid, "Migration must precede profile load.")
                    ),
                };
            return Pack(preview, Protocol.Json.MigrationPreview);
        }

        if (action == "initializeProfile")
        {
            Empty(payload);
            return Pack(
                portableProfile is null
                    ? new OperationResult(Outcome.Confirmed)
                    : await portableProfile.InitializeAsync(ct).ConfigureAwait(false),
                Protocol.Json.OperationResult
            );
        }

        if (action == "migrate")
        {
            var request = Read(payload, Protocol.Json.ConfirmPreviewPayload);
            return Pack(
                loaded
                    ? new OperationResult(Outcome.Failed, ReasonCode.ImportInvalid)
                    : await migration
                        .CommitAsync(request.PreviewId, request.Confirmed, ct)
                        .ConfigureAwait(false),
                Protocol.Json.OperationResult
            );
        }

        if (action == "load")
        {
            Empty(payload);
            if (portableProfile is not null)
            {
                var initialization = await portableProfile
                    .InitializeAsync(ct)
                    .ConfigureAwait(false);
                if (!initialization.Succeeded)
                    throw new ProtocolException("PortableProfileImportFailed");
            }
            await manager.LoadAsync(ct).ConfigureAwait(false);
            loaded = true;
            return Pack(manager.Snapshot, Protocol.Json.ManagerSnapshot);
        }

        if (action == "snapshot")
        {
            Empty(payload);
            return Pack(manager.Snapshot, Protocol.Json.ManagerSnapshot);
        }

        if (action == "shutdown")
        {
            Empty(payload);
            await DisposeAsync().ConfigureAwait(false);
            return Pack(new OperationResult(Outcome.Confirmed), Protocol.Json.OperationResult);
        }

        if (!loaded)
            throw new ProtocolException("InvalidRequest");
        if (action == "preparePortableDataDirectory")
        {
            var request = Read(payload, Protocol.Json.PathPayload);
            return Pack(
                portableProfile?.PrepareDataDirectory(request.Path)
                    ?? new OperationResult(Outcome.Failed, ReasonCode.InvalidPath),
                Protocol.Json.OperationResult
            );
        }
        return await dispatcher.ExecuteAsync(action, payload, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        authority.Dispose();
        return new(disposal ??= manager.DisposeAsync().AsTask());
    }
}
