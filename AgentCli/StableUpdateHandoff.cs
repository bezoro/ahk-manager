using System.Globalization;
using System.Security.Principal;
using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

internal sealed record StableUpdateIdentity(Guid OperationId, string Sid, int Session);

internal enum StableUpdateInvocation
{
    Other,
    Direct,
    RequestConsent,
    Invalid,
}

internal static class StableUpdateHandoff
{
    internal const string Command = "updates.installLatest";
    internal const string ChildAction = "--install-latest-elevated";
    internal const int ChildNoUpdate = 0;
    internal const int ChildRefused = 3;
    internal const int ChildAccepted = 5;
    internal const int ChildCheckFailed = 7;
    internal const int ChildUnknown = 8;

    internal static StableUpdateInvocation Invocation(string[] args) =>
        args switch
        {
            [Command] => StableUpdateInvocation.Direct,
            [Command, "--elevate"] => StableUpdateInvocation.RequestConsent,
            [Command, ..] => StableUpdateInvocation.Invalid,
            _ => StableUpdateInvocation.Other,
        };

    internal static string ChildParameters(StableUpdateIdentity identity) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ChildAction} {identity.OperationId:D} {CanonicalSid(identity.Sid)} {identity.Session}"
        );

    internal static StableUpdateIdentity? ParseChild(string[] args)
    {
        if (
            args is not [ChildAction, var id, var sid, var session]
            || !Guid.TryParseExact(id, "D", out var operationId)
            || operationId == Guid.Empty
            || id != operationId.ToString("D")
            || !int.TryParse(
                session,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int sessionId
            )
            || session != sessionId.ToString(CultureInfo.InvariantCulture)
        )
            return null;
        try
        {
            return new StableUpdateIdentity(operationId, CanonicalSid(sid), sessionId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static string CanonicalSid(string value)
    {
        if (
            value.Length is < 5 or > 192
            || !value.StartsWith("S-1-", StringComparison.Ordinal)
            || value.AsSpan(2).IndexOfAnyExcept("0123456789-") >= 0
        )
            throw new ArgumentException("A canonical Windows SID is required.");
        string canonical = new SecurityIdentifier(value).Value;
        if (canonical != value)
            throw new ArgumentException("A canonical Windows SID is required.");
        return canonical;
    }

    internal static bool MatchesAccount(
        StableUpdateIdentity expected,
        string sid,
        int session,
        bool admin
    ) => admin && expected.Sid == sid && expected.Session == session;

    internal static int ChildExit(Guid operationId, AgentResponse response)
    {
        if (operationId != response.RequestId)
            return ChildUnknown;
        if (response is { Outcome: "Confirmed", Code: "NoNewerStable" })
            return ChildNoUpdate;
        if (response is { Outcome: "Failed", Code: "UpdateCheckFailed" })
            return ChildCheckFailed;
        if (
            response
                is {
                    Outcome: "Accepted",
                    Code: "OperationAccepted",
                    Result: { ValueKind: JsonValueKind.Object } result
                }
            && result.TryGetProperty("operationId", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.TryGetGuid(out var returnedId)
            && returnedId == operationId
        )
            return ChildAccepted;
        return ChildUnknown;
    }

    internal static AgentResponse FromExit(Guid id, string version, int exit) =>
        exit switch
        {
            ChildNoUpdate => Reply(id, version, "Confirmed", "NoNewerStable"),
            ChildRefused => Reply(id, version, "Failed", "ElevationHandoffRefused"),
            ChildAccepted => Reply(
                id,
                version,
                "Accepted",
                "OperationAccepted",
                withOperation: true
            ),
            ChildCheckFailed => Reply(id, version, "Failed", "UpdateCheckFailed"),
            _ => Reply(id, version, "Unknown", "UpdateDispatchUnconfirmed", withOperation: true),
        };

    internal static AgentResponse BeforeLaunch(Guid id, string version, bool canceled) =>
        Reply(
            id,
            version,
            "Failed",
            canceled ? "CancelledBeforeDispatch" : "ElevationHandoffRefused"
        );

    internal static AgentResponse FromShellError(Guid id, string version, int error) =>
        error switch
        {
            1223 => BeforeLaunch(id, version, canceled: true), // ERROR_CANCELLED
            5 => BeforeLaunch(id, version, canceled: false), // ERROR_ACCESS_DENIED
            _ => FromExit(id, version, ChildUnknown),
        };

    static AgentResponse Reply(
        Guid id,
        string version,
        string outcome,
        string code,
        bool withOperation = false
    ) =>
        new(
            AgentProtocol.Version,
            id,
            version,
            null,
            outcome,
            code,
            withOperation
                ? JsonSerializer.SerializeToElement(
                    new AgentOperationId(id),
                    AgentJson.Default.AgentOperationId
                )
                : null
        );
}
