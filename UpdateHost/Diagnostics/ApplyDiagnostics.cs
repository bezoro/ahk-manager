using System.ComponentModel;
using System.Text.RegularExpressions;

namespace Bezoro.AhkManager.UpdateHost;

// Detailed local evidence is written only after operation ownership is established.
// File paths remain useful; recognized credentials never belong in helper logs.
internal static partial class ApplyDiagnostics
{
    internal static string Redact(string message)
    {
        string value = GithubToken().Replace(message, "[redacted]");
        value = BearerToken().Replace(value, "$1[redacted]");
        value = CookieHeader().Replace(value, "$1[redacted]");
        value = Credential().Replace(value, "$1[redacted]");
        value = SignedQuery().Replace(value, "$1[redacted]");
        value = UserInfo().Replace(value, "$1[redacted]@");
        return value.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    internal static void Failure(Action<string>? log, string stage, Exception exception)
    {
        if (log is null)
            return;
        try
        {
            Exception? current = exception;
            for (int depth = 0; current is not null && depth < 4; depth++)
            {
                string native = current is Win32Exception windows
                    ? " nativeCode=" + windows.NativeErrorCode
                    : "";
                log(
                    "Update failure stage="
                        + stage
                        + " cause="
                        + depth
                        + " type="
                        + current.GetType().Name
                        + " hresult=0x"
                        + unchecked((uint)current.HResult).ToString("X8")
                        + native
                        + " message="
                        + Redact(current.Message)
                );
                current = current.InnerException;
            }
            if (current is not null)
                log(
                    "Update failure stage="
                        + stage
                        + " [additional inner causes omitted after 4 levels]"
                );
        }
        catch (Exception)
        {
            // Diagnostics cannot change the installation or recovery decision.
        }
    }

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{10,}|github_pat_[A-Za-z0-9_]{10,})\b")]
    private static partial Regex GithubToken();

    [GeneratedRegex(@"(?i)(\b(?:Bearer|Basic)\s+)[^\s,;""'<>]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex(
        @"(?i)((?:""|')?\b(?:token|access[_-]?token|auth(?:entication|orization)?[_-]?token|authorization|password|passwd|secret|nonce|cookie|api[_-]?key)\b(?:""|')?\s*[:=]\s*)(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\s,;]+)"
    )]
    private static partial Regex Credential();

    [GeneratedRegex(@"(?i)(\bcookie\s*:\s*)[^\r\n]+")]
    private static partial Regex CookieHeader();

    [GeneratedRegex(
        @"(?i)([?&](?:token|access_token|sig|signature|x-amz-credential|x-amz-signature|x-amz-security-token|x-goog-credential|x-goog-signature)=)[^&\s]*"
    )]
    private static partial Regex SignedQuery();

    [GeneratedRegex(@"(?i)(https?://)[^/\s:@]+(?::[^/@\s]*)?@")]
    private static partial Regex UserInfo();
}
