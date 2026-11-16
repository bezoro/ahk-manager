using System.Text.Json;
using Bezoro.AhkManager.AgentCli;

internal static class StableUpdateHandoffChecks
{
    internal static void Run()
    {
        string command = StableUpdateHandoff.Command;
        Check(StableUpdateHandoff.Invocation([command]) == StableUpdateInvocation.Direct);
        Check(
            StableUpdateHandoff.Invocation([command, "--elevate"])
                == StableUpdateInvocation.RequestConsent
        );
        foreach (
            string[] args in new string[][]
            {
                [command, "--start"],
                [command, "--json", "{}"],
                [command, "--stdin"],
                [command, "--elevate", "--elevate"],
                [command, "--elevate", "--start"],
            }
        )
            Check(StableUpdateHandoff.Invocation(args) == StableUpdateInvocation.Invalid);
        Check(
            StableUpdateHandoff.Invocation(["app.quit", "--elevate"])
                == StableUpdateInvocation.Other
        );

        var identity = new StableUpdateIdentity(
            Guid.Parse("13f7debf-3d11-4ef9-90ab-cbc1d0f2748a"),
            "S-1-5-21-1-2-3-1001",
            4
        );
        string[] parameters = StableUpdateHandoff.ChildParameters(identity).Split(' ');
        Check(
            parameters
                is [
                    StableUpdateHandoff.ChildAction,
                    "13f7debf-3d11-4ef9-90ab-cbc1d0f2748a",
                    "S-1-5-21-1-2-3-1001",
                    "4",
                ]
        );
        Check(StableUpdateHandoff.ParseChild(parameters) == identity);
        Check(StableUpdateHandoff.ParseChild([.. parameters, "app.quit"]) is null);
        Check(
            StableUpdateHandoff.ParseChild([parameters[0], parameters[1], parameters[2], "04"])
                is null
        );
        Check(
            StableUpdateHandoff.ParseChild([
                parameters[0],
                parameters[1],
                "S-1-5-21-1-2-3-1001 --stdin",
                "4",
            ])
                is null
        );
        Check(StableUpdateHandoff.MatchesAccount(identity, identity.Sid, 4, admin: true));
        Check(!StableUpdateHandoff.MatchesAccount(identity, "S-1-5-21-1-2-3-1002", 4, admin: true));
        Check(!StableUpdateHandoff.MatchesAccount(identity, identity.Sid, 5, admin: true));
        Check(!StableUpdateHandoff.MatchesAccount(identity, identity.Sid, 4, admin: false));

        Guid id = identity.OperationId;
        const string version = "2.1.3";
        var reply = new AgentResponse(
            1,
            id,
            version,
            "generation",
            "Confirmed",
            "NoNewerStable",
            null
        );
        Check(StableUpdateHandoff.ChildExit(id, reply) == 0);
        Check(StableUpdateHandoff.ChildExit(id, reply with { Code = "Ok" }) == 8);
        Check(StableUpdateHandoff.ChildExit(id, reply with { RequestId = Guid.NewGuid() }) == 8);
        Check(
            StableUpdateHandoff.ChildExit(
                id,
                reply with
                {
                    Outcome = "Failed",
                    Code = "UpdateCheckFailed",
                }
            ) == 7
        );
        var accepted = StableUpdateHandoff.FromExit(id, version, 5);
        Check(
            accepted is { Outcome: "Accepted", Code: "OperationAccepted" }
                && OperationId(accepted) == id
        );
        Check(StableUpdateHandoff.ChildExit(id, accepted) == 5);
        Check(StableUpdateHandoff.ChildExit(id, accepted with { Result = null }) == 8);
        var mismatched = StableUpdateHandoff.FromExit(Guid.NewGuid(), version, 5);
        Check(StableUpdateHandoff.ChildExit(id, accepted with { Result = mismatched.Result }) == 8);
        Check(
            StableUpdateHandoff.FromExit(id, version, 3)
                is { Outcome: "Failed", Code: "ElevationHandoffRefused", Result: null }
        );
        Check(
            StableUpdateHandoff.FromExit(id, version, 7)
                is { Outcome: "Failed", Code: "UpdateCheckFailed", Result: null }
        );
        Check(
            StableUpdateHandoff.FromShellError(id, version, 1223)
                is { Outcome: "Failed", Code: "CancelledBeforeDispatch", Result: null }
        );
        Check(
            StableUpdateHandoff.FromShellError(id, version, 5)
                is { Outcome: "Failed", Code: "ElevationHandoffRefused", Result: null }
        );
        var uncertain = StableUpdateHandoff.FromShellError(id, version, 87);
        Check(
            uncertain is { Outcome: "Unknown", Code: "UpdateDispatchUnconfirmed" }
                && OperationId(uncertain) == id
        );
        var timedOut = StableUpdateHandoff.FromExit(id, version, 8);
        Check(
            timedOut is { Outcome: "Unknown", Code: "UpdateDispatchUnconfirmed" }
                && OperationId(timedOut) == id
        );
        Console.WriteLine(
            "Stable update syntax, bound child credentials, cancellation and exit receipts passed."
        );
    }

    static Guid OperationId(AgentResponse response) =>
        response.Result is { ValueKind: JsonValueKind.Object } result
            ? result.GetProperty("operationId").GetGuid()
            : Guid.Empty;

    static void Check(
        bool result,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(result))]
            string? check = null
    )
    {
        if (!result)
            throw new InvalidOperationException("Stable update handoff: " + check);
    }
}
