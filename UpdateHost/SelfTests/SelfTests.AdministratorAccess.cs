using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckPortableAdministratorAccess(Action<bool, string> check)
    {
        PortableAdministratorAccess.Require(true, true);
        check(true, "portable initialization accepts an administrative helper and Manager parent");

        (bool? Helper, bool? Parent)[] denied =
        [
            (false, false),
            (false, true),
            (true, false),
            (null, null),
            (null, true),
            (true, null),
            (false, null),
            (null, false),
        ];
        PortableInitializationContextException? last = null;
        foreach (var roles in denied)
        {
            bool layoutReached = false;
            PortableInitializationContextException? failure = null;
            try
            {
                PortableInitializationDiagnostics.Step(
                    "resolve-layout",
                    "application",
                    () =>
                    {
                        PortableInitializationDiagnostics.Step(
                            "verify-administrator",
                            "application",
                            () => PortableAdministratorAccess.Require(roles.Helper, roles.Parent)
                        );
                        layoutReached = true;
                    }
                );
            }
            catch (PortableInitializationContextException refused)
            {
                failure = refused;
            }
            check(
                !layoutReached
                    && failure
                        is {
                            Stage: "verify-administrator",
                            Resource: "application",
                            Failure: PortableAdministratorRequiredException,
                        },
                "a medium or unresolved helper/parent role cannot authorize portable initialization: "
                    + (roles.Helper?.ToString() ?? "unavailable")
                    + "/"
                    + (roles.Parent?.ToString() ?? "unavailable")
            );
            last = failure;
        }
        check(
            AdmissionDiagnostics.Encode(UpdatePhase.PortableInitialization, last!)
                == AdmissionDiagnostics.Prefix
                    + "{\"schemaVersion\":1,\"phase\":\"portable-initialization\",\"code\":\"administrator-required\"}",
            "administrator startup refusal has a dedicated finite code instead of a folder-access error"
        );
        string context = PortableInitializationDiagnostics.Encode(
            UpdatePhase.PortableInitialization,
            last!
        )!;
        using var parsed = JsonDocument.Parse(
            context[PortableInitializationDiagnostics.Prefix.Length..]
        );
        check(
            parsed.RootElement.GetProperty("stage").GetString() == "verify-administrator"
                && parsed.RootElement.GetProperty("resource").GetString() == "application"
                && parsed.RootElement.GetProperty("hresult").GetString() == "0x80070005"
                && !context.Contains("Open the root", StringComparison.Ordinal),
            "administrator startup context is path-free and preserves the earliest authorization stage"
        );
    }
}
