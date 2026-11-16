using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

// stderr belongs to the already authenticated Manager parent, never the named pipe peer.
internal static class AgentDiagnostics
{
    internal const string Prefix = "AHK_AGENT_REFUSAL:";
    static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "AccessDenied",
        "CancelledBeforeDispatch",
        "ElevationRequired",
        "HostBusy",
        "InternalFailure",
        "InvalidInput",
        "InvalidInstallation",
        "InvalidState",
        "NativeFailure",
        "PeerUnverified",
        "PrivateParentRequired",
        "TransportFailure",
    };

    internal static string Classify(Exception failure) =>
        failure switch
        {
            InvalidDataException or JsonException or DecoderFallbackException => "InvalidInput",
            UnauthorizedAccessException => "AccessDenied",
            OperationCanceledException => "CancelledBeforeDispatch",
            Win32Exception => "NativeFailure",
            IOException => "TransportFailure",
            InvalidOperationException => "InvalidState",
            _ => "InternalFailure",
        };

    internal static string? Encode(string phase, string code)
    {
        if (phase is not ("server" or "relay") || !Codes.Contains(code))
            return null;
        return Prefix
            + "{\"schemaVersion\":1,\"phase\":\""
            + phase
            + "\",\"code\":\""
            + code
            + "\"}";
    }

    internal static void Write(string phase, string code)
    {
        try
        {
            if (Encode(phase, code) is { } line)
                Console.Error.WriteLine(line);
        }
        catch (Exception failure)
            when (failure is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { }
    }
}
