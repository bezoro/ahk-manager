using System.ComponentModel;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckApplyDiagnostics(Action<bool, string> check)
    {
        var lines = new List<string>();
        var failure = new IOException(
            @"The process cannot access C:\Users\Fixture\OneDrive\App because it is in use. token=private-value Bearer private-bearer https://user:pass@example.test/file?sig=private-signature",
            new Win32Exception(32, "inner sharing violation github_pat_private_token_0123456789")
        );
        ApplyDiagnostics.Failure(lines.Add, "move-current-application", failure);
        string evidence = string.Join("\n", lines);
        check(
            evidence.Contains("stage=move-current-application")
                && evidence.Contains("type=IOException")
                && evidence.Contains("hresult=0x")
                && evidence.Contains("nativeCode=32"),
            "native failure diagnostics preserve stage type HRESULT and inner Win32 cause"
        );
        check(
            evidence.Contains(@"C:\Users\Fixture\OneDrive\App")
                && evidence.Contains("inner sharing violation"),
            "native local diagnostics retain useful paths and error explanations"
        );
        check(
            !evidence.Contains("private-value")
                && !evidence.Contains("private-bearer")
                && !evidence.Contains("user:pass")
                && !evidence.Contains("private-signature")
                && !evidence.Contains("github_pat_private"),
            "native exception diagnostics redact recognized credentials"
        );
        string quoted = ApplyDiagnostics.Redact(
            "{\"accessToken\":\"private-json\", \"password\":\"private-password\"} Authorization: Basic private-basic\nCookie: first=private-cookie; second=private-second"
        );
        check(
            !quoted.Contains("private-"),
            "native exception redaction also covers quoted credential fields and authentication headers"
        );
        string escaped = ApplyDiagnostics.Redact(
            "{\"password\":\"private-prefix\\\"private-tail\"}"
        );
        check(
            !escaped.Contains("private-"),
            "native credential redaction preserves JSON escaped-string boundaries"
        );
        bool callbackThrew = false;
        ApplyDiagnostics.Failure(
            _ =>
            {
                callbackThrew = true;
                throw new IOException("unavailable");
            },
            "install",
            failure
        );
        check(callbackThrew, "unavailable diagnostics never replace the original apply decision");
        string bounded = OperationLog.BoundedMessage(new string('x', 3000));
        check(
            bounded.Length <= 2000
                && bounded.Contains("message truncated")
                && bounded.Contains("3000"),
            "native log message limits explicitly report omitted content"
        );
    }

    static void CheckOperationLogLimits(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "operation log limits");
        Directory.CreateDirectory(directory);
        using (var log = new OperationLog(directory))
            for (int index = 0; index < 50; index++)
                log.Write(new string('x', 2000));
        string file = Path.Combine(directory, "helper.log");
        string contents = File.ReadAllText(file);
        check(
            new FileInfo(file).Length <= OperationLog.MaximumBytes
                && contents.Contains("size limit reached; additional entries omitted"),
            "native log total-size retention explicitly reports omissions within its byte bound"
        );
    }
}
