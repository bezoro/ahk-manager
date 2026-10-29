using System.Text.Json;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;
using static Bezoro.AhkManager.Backend.BackendRequestPayload;

namespace Bezoro.AhkManager.Backend;

/// <summary>Authority handshakes stay separate from ordinary catalog and script requests.</summary>
internal sealed class BackendAuthorityRequests(
    IManagerService manager,
    StartupAdoption? startupAdoption
) : IDisposable
{
    internal ManagerElevation? Administration { get; set; }
    internal bool AdoptionPending => startupAdoption?.Pending == true;
    internal bool AdoptionConfigured => startupAdoption is not null;
    internal bool Frozen => Administration?.Frozen == true;
    internal bool ShutdownAllowed => Administration?.ShutdownAllowed == true;

    internal static bool Handles(string action) =>
        action
            is "startupAdoptionStatus"
                or "adoptStartupContinuation"
                or "beginStartupContinuation"
                or "beginAdministration"
                or "administrationStatus"
                or "commitAdministration"
                or "cancelAdministration";

    internal async Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement payload,
        bool loaded,
        CancellationToken ct
    )
    {
        var administration = Administration;
        if (action == "startupAdoptionStatus")
        {
            Empty(payload);
            if (startupAdoption is null)
                throw new ProtocolException("InvalidRequest");
            return Pack(
                await startupAdoption.StatusAsync().WaitAsync(ct).ConfigureAwait(false),
                Protocol.Json.OperationResult
            );
        }

        if (action == "adoptStartupContinuation")
        {
            Empty(payload);
            if (!loaded || startupAdoption is null || manager is not ManagerService adoptionManager)
                throw new ProtocolException("InvalidRequest");
            return Pack(
                await startupAdoption.RunAsync(adoptionManager).ConfigureAwait(false),
                Protocol.Json.OperationResult
            );
        }

        if (action == "beginStartupContinuation")
        {
            var request = Read(payload, Protocol.Json.StartupElevationPayload);
            if (
                !loaded
                || administration is null
                || manager is not ManagerService continuationManager
            )
                throw new ProtocolException("InvalidRequest");
            var ticket = await continuationManager
                .GetPreparedStartupContinuationAsync(request.ContinuationId)
                .ConfigureAwait(false);
            return Pack(
                ticket is null
                    ? new ElevationStatus(
                        request.OperationId,
                        0,
                        ElevationState.Failed,
                        ReasonCode.IdentityChanged,
                        null,
                        true
                    )
                    : administration.BeginStartup(request.OperationId, ticket),
                Protocol.Json.ElevationStatus
            );
        }

        if (
            action
            is "beginAdministration"
                or "administrationStatus"
                or "commitAdministration"
                or "cancelAdministration"
        )
        {
            if (!loaded || administration is null)
                throw new ProtocolException("InvalidRequest");
            if (action == "administrationStatus")
            {
                var request = Read(payload, Protocol.Json.ElevationStatusPayload);
                return Pack(
                    await administration
                        .Status(request.OperationId, request.AfterRevision, ct)
                        .ConfigureAwait(false),
                    Protocol.Json.ElevationStatus
                );
            }

            if (action == "beginAdministration")
            {
                var request = Read(payload, Protocol.Json.AdministrationBeginPayload);
                return Pack(
                    administration.Begin(request.OperationId, request.ResumeLogin),
                    Protocol.Json.ElevationStatus
                );
            }

            var ticket = Read(payload, Protocol.Json.ElevationOperationPayload);
            var status = action switch
            {
                "commitAdministration" => await administration
                    .Commit(ticket.OperationId, ct)
                    .ConfigureAwait(false),
                _ => administration.Cancel(ticket.OperationId),
            };
            return Pack(status, Protocol.Json.ElevationStatus);
        }

        throw new ProtocolException("UnknownAction");
    }

    public void Dispose() => Administration?.Dispose();
}
