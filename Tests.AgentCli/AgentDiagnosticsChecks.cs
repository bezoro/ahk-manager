using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Bezoro.AhkManager.AgentCli;

internal static class AgentDiagnosticsChecks
{
    internal static void Run()
    {
        const string sensitive = "private-token C:\\Users\\Private\\settings.json";
        (Exception Failure, string Code)[] cases =
        [
            (new InvalidDataException(sensitive), "InvalidInput"),
            (new JsonException(sensitive), "InvalidInput"),
            (new DecoderFallbackException(sensitive), "InvalidInput"),
            (new UnauthorizedAccessException(sensitive), "AccessDenied"),
            (new OperationCanceledException(sensitive), "CancelledBeforeDispatch"),
            (new Win32Exception(5, sensitive), "NativeFailure"),
            (new IOException(sensitive), "TransportFailure"),
            (new InvalidOperationException(sensitive), "InvalidState"),
            (new Exception(sensitive), "InternalFailure"),
        ];
        foreach (var item in cases)
        {
            Check(AgentDiagnostics.Classify(item.Failure) == item.Code);
            foreach (string phase in new[] { "server", "relay" })
            {
                string line = AgentDiagnostics.Encode(phase, item.Code)!;
                Check(line.Length < 512 && !line.Contains("Private") && !line.Contains("token"));
                using var packet = JsonDocument.Parse(line[AgentDiagnostics.Prefix.Length..]);
                Check(
                    packet.RootElement.EnumerateObject().Count() == 3
                        && packet.RootElement.GetProperty("schemaVersion").GetInt32() == 1
                        && packet.RootElement.GetProperty("phase").GetString() == phase
                        && packet.RootElement.GetProperty("code").GetString() == item.Code
                );
            }
        }
        Check(AgentDiagnostics.Encode("private-phase", "InvalidInput") is null);
        Check(AgentDiagnostics.Encode("relay", sensitive) is null);
        using var output = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(output);
            AgentDiagnostics.Write("relay", "InvalidInput");
        }
        finally
        {
            Console.SetError(original);
        }
        Check(
            output.ToString() == AgentDiagnostics.Encode("relay", "InvalidInput") + output.NewLine
        );
        foreach (
            var failure in new Exception[]
            {
                new IOException(sensitive),
                new UnauthorizedAccessException(sensitive),
                new ObjectDisposedException(sensitive),
            }
        )
        {
            using var unavailable = new UnavailableSink(failure);
            try
            {
                Console.SetError(unavailable);
                AgentDiagnostics.Write("relay", "InvalidInput");
            }
            finally
            {
                Console.SetError(original);
            }
        }
    }

    static void Check(bool pass)
    {
        if (!pass)
            throw new InvalidOperationException("The finite agent diagnostic contract changed.");
    }

    sealed class UnavailableSink(Exception failure) : StringWriter
    {
        public override void WriteLine(string? value) => throw failure;
    }
}
