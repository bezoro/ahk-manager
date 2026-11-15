using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

internal static class Program
{
    static readonly string Usage = "ahk-manager <command> [--json OBJECT | --stdin] [--start]";

    static async Task<int> Main(string[] args)
    {
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };
        bool privateMode = args is ["--server"];
        Guid requestId = Guid.NewGuid();
        string? executable = Environment.ProcessPath;
        string version = executable is null
            ? "0.0.0"
            : FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+', 2)[0]
                ?? "0.0.0";
        try
        {
            if (privateMode)
            {
                using var self = new AgentPeer(Environment.ProcessId);
                if (!self.Administrator)
                    throw new AgentFailure("ElevationRequired");
                var layout = AgentLayout.FromExecutable(self.Stamp.Executable);
                using var parent = AgentPeer.AuthenticateParent(self, self, layout.Host);
                AgentPipe.RequirePrivateStdio(parent.Pid);
                await new AgentServer(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    parent
                ).Run(layout, self, stopping.Token);
                return 0;
            }
            if (args.Length == 0 || args is ["help"] or ["--help"] or ["-h"])
                return Write(
                    Offline(
                        requestId,
                        version,
                        JsonSerializer.SerializeToElement(
                            new AgentHelp(Usage, "commands", "version"),
                            AgentJson.Default.AgentHelp
                        )
                    )
                );
            if (args is ["version"] or ["--version"])
                return Write(
                    Offline(
                        requestId,
                        version,
                        JsonSerializer.SerializeToElement(
                            new AgentVersion(version),
                            AgentJson.Default.AgentVersion
                        )
                    )
                );
            if (args is ["commands"])
            {
                return Write(
                    new AgentResponse(
                        1,
                        requestId,
                        version,
                        null,
                        "Confirmed",
                        "Ok",
                        AgentProtocol.Parse(
                            AgentProtocol.Utf8.GetBytes(AgentCatalog.Json),
                            JsonValueKind.Array
                        )
                    )
                );
            }
            var options = await Parse(args, stopping.Token);
            var request = new AgentRequest(
                AgentProtocol.Version,
                requestId,
                args[0],
                options.Arguments
            );
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                request,
                AgentJson.Default.AgentRequest
            );
            if (payload.Length > AgentProtocol.MaxRequest)
                throw new AgentFailure("InvalidInput");
            using var local = new AgentPeer(Environment.ProcessId);
            if (!local.Administrator)
                throw new AgentFailure("ElevationRequired");
            return Write(
                await new AgentClient(
                    AgentLayout.FromExecutable(local.Stamp.Executable),
                    local
                ).Invoke(request, payload, options.Start, stopping.Token)
            );
        }
        catch (Exception ex) when (AgentFailure.Expected(ex))
        {
            string code = ex switch
            {
                AgentFailure failure => failure.Code,
                JsonException or DecoderFallbackException => "InvalidInput",
                OperationCanceledException => "CancelledBeforeDispatch",
                UnauthorizedAccessException or ManagementException => "PeerUnverified",
                _ => "InvalidInstallation",
            };
            if (privateMode)
            {
                Console.Error.WriteLine(code);
                return Exit("Failed", code);
            }
            string outcome = code is "ReplyUnconfirmed" or "StartupUnconfirmed"
                ? "Unknown"
                : "Failed";
            return Write(
                new AgentResponse(
                    AgentProtocol.Version,
                    requestId,
                    version,
                    null,
                    outcome,
                    code,
                    null
                )
            );
        }
    }

    static AgentResponse Offline(Guid requestId, string version, JsonElement result) =>
        new(AgentProtocol.Version, requestId, version, null, "Confirmed", "Ok", result);

    static int Write(AgentResponse response)
    {
        using var destination = Console.OpenStandardOutput();
        destination.Write(
            JsonSerializer.SerializeToUtf8Bytes(response, AgentJson.Default.AgentResponse)
        );
        destination.WriteByte((byte)'\n');
        destination.Flush();
        return Exit(response.Outcome, response.Code);
    }

    static int Exit(string outcome, string code) =>
        code switch
        {
            "InvalidInput" or "UnknownCommand" or "InvalidArguments" => 2,
            "ElevationRequired" or "PeerUnverified" or "AccessDenied" or "PrivateParentRequired" =>
                3,
            "HostUnavailable" or "HostBusy" or "InvalidInstallation" => 4,
            _ => outcome switch
            {
                "Confirmed" => 0,
                "Accepted" => 5,
                "Partial" => 6,
                "Unknown" => 8,
                _ => 7,
            },
        };

    static async Task<(JsonElement Arguments, bool Start)> Parse(
        string[] args,
        CancellationToken ct
    )
    {
        if (!AgentProtocol.CommandName(args[0]) || args[0] is "help" or "version" or "commands")
            throw new AgentFailure("InvalidInput");
        bool start = false;
        byte[]? bytes = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--start" when !start && args[0] != "app.start":
                    start = true;
                    break;
                case "--json"
                    when bytes is null && index + 1 < args.Length && args[0] != "app.start":
                    bytes = AgentProtocol.Utf8.GetBytes(args[++index]);
                    break;
                case "--stdin"
                    when bytes is null && args[0] != "app.start" && Console.IsInputRedirected:
                    using (var input = Console.OpenStandardInput())
                        bytes = await ReadBounded(input, deadline.Token).WaitAsync(deadline.Token);
                    break;
                default:
                    throw new AgentFailure("InvalidInput");
            }
        }
        if (bytes is null)
            return (AgentProtocol.Empty, start);
        if (bytes.Length > AgentProtocol.MaxRequest)
            throw new AgentFailure("InvalidInput");
        try
        {
            return (AgentProtocol.Parse(bytes, JsonValueKind.Object), start);
        }
        catch (Exception ex)
            when (ex is InvalidDataException or JsonException or DecoderFallbackException)
        {
            throw new AgentFailure("InvalidInput");
        }
    }

    static async Task<byte[]> ReadBounded(Stream source, CancellationToken ct)
    {
        using var target = new MemoryStream();
        byte[] buffer = new byte[8192];
        int length;
        while ((length = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + length > AgentProtocol.MaxRequest)
                throw new AgentFailure("InvalidInput");
            target.Write(buffer, 0, length);
        }
        return target.ToArray();
    }
}

internal sealed class AgentFailure(string code) : Exception(code)
{
    internal string Code { get; } = code;

    internal static bool Expected(Exception ex) =>
        ex
            is AgentFailure
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or ManagementException
                or InvalidOperationException
                or OperationCanceledException
                or JsonException
                or ArgumentException
                or TimeoutException;
}
