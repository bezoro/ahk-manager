using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

internal static class Program
{
    static readonly string Usage =
        "ahk-manager <command> [--json OBJECT | --stdin] [--start]\nahk-manager updates.installLatest [--elevate]\nahk-manager mcp";

    static async Task<int> Main(string[] args)
    {
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };
        bool privateMode = args is ["--server"];
        bool updateChild = args.Length > 0 && args[0] == StableUpdateHandoff.ChildAction;
        bool authenticatedServer = false;
        Guid requestId = Guid.NewGuid();
        string? executable = Environment.ProcessPath;
        string version = executable is null
            ? "0.0.0"
            : FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+', 2)[0]
                ?? "0.0.0";
        if (args is ["mcp", ..])
        {
            if (args.Length == 1)
                return await RunMcp(version, stopping.Token);
            Console.Error.WriteLine("Usage: ahk-manager mcp");
            return 2;
        }
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
                authenticatedServer = true;
                await new AgentServer(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    parent
                ).Run(layout, self, stopping.Token);
                return 0;
            }
            if (updateChild)
                return await RunElevatedUpdate(args, version, stopping.Token);
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
            var stable = StableUpdateHandoff.Invocation(args);
            if (stable == StableUpdateInvocation.Invalid)
                throw new AgentFailure("InvalidInput");
            var options =
                stable == StableUpdateInvocation.Other
                    ? await Parse(args, stopping.Token)
                    : (Arguments: AgentProtocol.Empty, Start: false);
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
            {
                if (
                    stable == StableUpdateInvocation.RequestConsent
                    && local.Security is { Elevated: false, IntegrityRid: >= 0x2000 and < 0x3000 }
                )
                {
                    var layout = AgentLayout.FromExecutable(local.Stamp.Executable);
                    Environment.CurrentDirectory = layout.Root;
                    return Write(
                        await StableUpdateLauncher.RequestConsent(
                            layout,
                            local,
                            new StableUpdateIdentity(
                                requestId,
                                local.Stamp.Sid,
                                local.Stamp.Session
                            ),
                            version,
                            stopping.Token
                        )
                    );
                }
                throw new AgentFailure("ElevationRequired");
            }
            return Write(
                await InvokeLive(local, request, payload, options.Start, version, stopping.Token)
            );
        }
        catch (Exception ex) when (AgentFailure.Expected(ex))
        {
            string code = FailureCode(ex);
            if (updateChild)
                return
                    code
                        is "PeerUnverified"
                            or "ElevationRequired"
                            or "HostUnavailable"
                            or "HostBusy"
                            or "InvalidInstallation"
                            or "CancelledBeforeDispatch"
                    ? StableUpdateHandoff.ChildRefused
                    : StableUpdateHandoff.ChildUnknown;
            if (privateMode)
            {
                if (authenticatedServer)
                    AgentDiagnostics.Write(
                        "server",
                        ex is AgentFailure ? code : AgentDiagnostics.Classify(ex)
                    );
                return Exit("Failed", code);
            }
            return Write(Failed(requestId, args.FirstOrDefault(), version, code));
        }
    }

    static string FailureCode(Exception ex) =>
        ex switch
        {
            AgentFailure failure => failure.Code,
            JsonException or DecoderFallbackException => "InvalidInput",
            OperationCanceledException => "CancelledBeforeDispatch",
            UnauthorizedAccessException or ManagementException => "PeerUnverified",
            _ => "InvalidInstallation",
        };

    static AgentResponse Failed(Guid id, string? command, string version, string code)
    {
        string outcome = code is "ReplyUnconfirmed" or "StartupUnconfirmed" ? "Unknown" : "Failed";
        return outcome == "Unknown" && command == StableUpdateHandoff.Command
            ? StableUpdateHandoff.FromExit(id, version, StableUpdateHandoff.ChildUnknown)
            : new(AgentProtocol.Version, id, version, null, outcome, code, null);
    }

    static async Task<AgentResponse> InvokeLive(
        AgentPeer local,
        AgentRequest request,
        byte[] payload,
        bool start,
        string version,
        CancellationToken ct
    )
    {
        var layout = AgentLayout.FromExecutable(local.Stamp.Executable);
        bool stable = request.Command == StableUpdateHandoff.Command;
        if (stable)
            Environment.CurrentDirectory = layout.Root;
        var response = await new AgentClient(layout, local).Invoke(request, payload, start, ct);
        return
            stable
            && response.Outcome is "Confirmed" or "Accepted"
            && StableUpdateHandoff.ChildExit(request.RequestId, response)
                == StableUpdateHandoff.ChildUnknown
            ? StableUpdateHandoff.FromExit(
                request.RequestId,
                version,
                StableUpdateHandoff.ChildUnknown
            )
            : response;
    }

    static async Task<int> RunMcp(string version, CancellationToken ct)
    {
        try
        {
            // A client may start us with a working directory in the portable
            // payload. Do not retain that directory while waiting on stdio.
            if (OperatingSystem.IsWindows())
                Environment.CurrentDirectory = Environment.SystemDirectory;
            var catalog = new McpToolCatalog(AgentCatalog.Json);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await new McpServer(version, catalog.Tools, catalog.Resolve, Invoke).Run(
                input,
                output,
                ct
            );
            return 0;

            async Task<AgentResponse> Invoke(AgentRequest request, CancellationToken calling)
            {
                try
                {
                    if (
                        request.Command == "commands.preview"
                        && !catalog.AcceptsPreview(request.Arguments)
                    )
                        throw new AgentFailure("InvalidArguments");
                    // The startup command is dispatched locally by AgentClient. Its
                    // argument contract must hold before the host can be launched.
                    if (request.Command == "app.start" && request.Arguments.EnumerateObject().Any())
                        throw new AgentFailure("InvalidArguments");
                    var payload = JsonSerializer.SerializeToUtf8Bytes(
                        request,
                        AgentJson.Default.AgentRequest
                    );
                    if (payload.Length > AgentProtocol.MaxRequest)
                        throw new AgentFailure("InvalidInput");
                    calling.ThrowIfCancellationRequested();
                    if (!OperatingSystem.IsWindows())
                        throw new AgentFailure("PlatformUnsupported");
                    using var local = new AgentPeer(Environment.ProcessId);
                    if (!local.Administrator)
                        throw new AgentFailure("ElevationRequired");
                    return await InvokeLive(local, request, payload, false, version, calling);
                }
                catch (Exception ex) when (AgentFailure.Expected(ex))
                {
                    return Failed(request.RequestId, request.Command, version, FailureCode(ex));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex) when (AgentFailure.Expected(ex))
        {
            // After stdio service starts, CLI JSON and diagnostics must never mix
            // with the client's JSON-RPC stream.
            Console.Error.WriteLine("The AHK Manager MCP session could not continue.");
            return Exit("Failed", FailureCode(ex));
        }
    }

    static async Task<int> RunElevatedUpdate(string[] args, string version, CancellationToken ct)
    {
        if (StableUpdateHandoff.ParseChild(args) is not { } intent)
            return StableUpdateHandoff.ChildRefused;
        using var local = new AgentPeer(Environment.ProcessId);
        if (
            !StableUpdateHandoff.MatchesAccount(
                intent,
                local.Stamp.Sid,
                local.Stamp.Session,
                local.Administrator
            )
        )
            return StableUpdateHandoff.ChildRefused;
        var layout = AgentLayout.FromExecutable(local.Stamp.Executable);
        Environment.CurrentDirectory = layout.Root;
        // Verify again in the elevated process, before contact with the host.
        using (new StableUpdateLaunchLease(layout, local.Stamp.Executable, version)) { }
        var request = new AgentRequest(
            AgentProtocol.Version,
            intent.OperationId,
            StableUpdateHandoff.Command,
            AgentProtocol.Empty
        );
        var response = await new AgentClient(layout, local).Invoke(
            request,
            JsonSerializer.SerializeToUtf8Bytes(request, AgentJson.Default.AgentRequest),
            false,
            ct
        );
        return StableUpdateHandoff.ChildExit(intent.OperationId, response);
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
            "ElevationRequired"
            or "PeerUnverified"
            or "AccessDenied"
            or "PrivateParentRequired"
            or "ElevationHandoffRefused" => 3,
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
