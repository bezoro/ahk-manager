using System.ComponentModel;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckPortableInitializationDiagnostics(Action<bool, string> check)
    {
        var original = new UnauthorizedAccessException("private-token C:\\Users\\Private");
        Exception? captured = null;
        try
        {
            PortableInitializationDiagnostics.Step(
                "prepare-app",
                "application",
                () =>
                    PortableInitializationDiagnostics.Step(
                        "protect-root",
                        "root",
                        () =>
                        {
                            throw original;
                        }
                    )
            );
        }
        catch (PortableInitializationContextException context)
        {
            captured = context;
        }
        check(
            captured
                is PortableInitializationContextException
                {
                    Stage: "protect-root",
                    Resource: "root"
                }
                && ReferenceEquals(PortableInitializationDiagnostics.Original(captured), original),
            "portable initialization retains the innermost failing stage and original exception"
        );
        string line = PortableInitializationDiagnostics.Encode(
            UpdatePhase.PortableInitialization,
            captured!
        )!;
        using var packet = System.Text.Json.JsonDocument.Parse(
            line[PortableInitializationDiagnostics.Prefix.Length..]
        );
        var fields = packet.RootElement;
        check(
            fields.GetProperty("schemaVersion").GetInt32() == 1
                && fields.GetProperty("phase").GetString() == "portable-initialization"
                && fields.GetProperty("stage").GetString() == "protect-root"
                && fields.GetProperty("resource").GetString() == "root"
                && fields.GetProperty("hresult").GetString() == "0x80070005"
                && fields.GetProperty("nativeError").ValueKind
                    == System.Text.Json.JsonValueKind.Null,
            "portable bootstrap context uses a bounded finite stage resource and literal HRESULT protocol"
        );
        check(
            fields.GetProperty("userSid").ValueKind == System.Text.Json.JsonValueKind.Null
                || System.Text.RegularExpressions.Regex.IsMatch(
                    fields.GetProperty("userSid").GetString()!,
                    @"\AS-\d+(?:-\d+){1,15}\z"
                ),
            "bootstrap diagnostics include only the current token SID or an explicit unavailable value"
        );
        check(
            fields.GetProperty("administrator").ValueKind
                is System.Text.Json.JsonValueKind.Null
                    or System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False,
            "bootstrap diagnostics preserve administrator token role as a finite optional boolean"
        );
        check(
            !line.Contains("Private") && !line.Contains("private-token") && line.Length < 2048,
            "portable bootstrap context excludes raw paths messages and credentials"
        );
        check(
            AdmissionDiagnostics.Encode(UpdatePhase.PortableInitialization, captured!)
                == AdmissionDiagnostics.Encode(UpdatePhase.PortableInitialization, original),
            "bootstrap stage context preserves the legacy strict refusal classification"
        );
        var data = new PortableDataInitializationException(
            new PortableInitializationContextException("verify-data-access", "data", original)
        );
        string dataLine = PortableInitializationDiagnostics.Encode(
            UpdatePhase.PortableDataInitialization,
            data
        )!;
        check(
            dataLine.Contains("\"phase\":\"portable-data-initialization\"")
                && dataLine.Contains("\"resource\":\"data\"")
                && AdmissionDiagnostics
                    .Encode(UpdatePhase.PortableDataInitialization, data)
                    .Contains("\"code\":\"access-denied\""),
            "bootstrap data diagnostics preserve data scope and the original permission refusal"
        );
        var native = new PortableInitializationContextException(
            "retain-root",
            "root",
            new Win32Exception(5)
        );
        check(
            AdmissionDiagnostics
                .Encode(UpdatePhase.PortableInitialization, native)
                .Contains("\"code\":\"access-denied\""),
            "bootstrap context preserves native Win32 refusal codes"
        );
        check(
            PortableInitializationDiagnostics
                .Encode(UpdatePhase.PortableInitialization, native)!
                .Contains("\"nativeError\":5"),
            "bootstrap native context preserves the Win32 error independently of generic exception HRESULTs"
        );
        check(
            PortableInitializationDiagnostics.Encode(UpdatePhase.Apply, captured!) is null
                && PortableInitializationDiagnostics.Encode(
                    UpdatePhase.PortableInitialization,
                    new PortableInitializationContextException("secret-stage", "root", original)
                )
                    is null,
            "unsupported phases and stages cannot inject bootstrap diagnostic fields"
        );
    }
}
