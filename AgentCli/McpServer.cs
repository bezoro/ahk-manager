using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

internal sealed class McpServer(
    string version,
    JsonElement tools,
    Func<string, string?> resolve,
    Func<AgentRequest, CancellationToken, Task<AgentResponse>> invoke
)
{
    const string LatestProtocol = "2025-11-25";
    const int ParseError = -32700;
    const int InvalidRequest = -32600;
    const int MethodNotFound = -32601;
    const int InvalidParameters = -32602;

    static readonly AgentJson WireJson = new(
        new JsonSerializerOptions
        {
            Encoder = McpTransport.WriterOptions.Encoder,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }
    );

    sealed class Call(
        JsonElement id,
        string command,
        CancellationTokenSource cancellation,
        Task<AgentResponse> reply
    ) : IDisposable
    {
        internal JsonElement Id { get; } = id;
        internal string Command { get; } = command;
        internal Task<AgentResponse> Reply { get; } = reply;
        internal bool Cancelled => cancellation.IsCancellationRequested;

        internal void Cancel() => cancellation.Cancel();

        public void Dispose() => cancellation.Dispose();
    }

    sealed record Request(JsonElement Id, string Method, JsonElement Parameters)
    {
        internal bool Notification => Id.ValueKind == JsonValueKind.Undefined;
    }

    internal async Task Run(Stream input, Stream output, CancellationToken stopping)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var transport = new McpTransport(input, output);
        CancellationToken ct = session.Token;
        string? protocol = null;
        bool ready = false;
        Call? active = null;
        Task<byte[]?>? incoming = null;
        try
        {
            while (true)
            {
                // A call may be waiting for the authenticated backend. Keep servicing
                // stdio so cancellation and discovery remain available in that period.
                if (active is not null && active.Reply.IsCompleted)
                {
                    var finished = active;
                    active = null;
                    using (finished)
                    {
                        var response = await finished.Reply;
                        if (!finished.Cancelled)
                            await ToolResult(transport, finished.Id, protocol!, response, ct);
                        if (
                            finished.Command is "updates.install" or "updates.installLatest"
                            && response.Outcome is "Accepted" or "Unknown"
                        )
                            // An update can replace the directory containing this executable.
                            return;
                    }
                    continue;
                }

                incoming ??= transport.Read(ct);
                if (active is not null)
                {
                    await Task.WhenAny(incoming, active.Reply).WaitAsync(ct);
                    if (active.Reply.IsCompleted)
                        continue;
                }
                byte[]? bytes;
                try
                {
                    bytes = await incoming.WaitAsync(ct);
                }
                catch (Exception failure)
                    when (failure is EndOfStreamException or InvalidDataException)
                {
                    await Error(transport, default, ParseError, "Invalid MCP frame.", ct);
                    return;
                }
                incoming = null;
                if (bytes is null)
                    return;

                JsonElement message;
                try
                {
                    message = AgentProtocol.Parse(bytes, JsonValueKind.Object);
                }
                catch (Exception failure)
                    when (failure
                            is JsonException
                                or DecoderFallbackException
                                or InvalidOperationException
                    )
                {
                    await Error(transport, default, ParseError, "Invalid JSON.", ct);
                    continue;
                }
                catch (InvalidDataException)
                {
                    await Error(transport, default, InvalidRequest, "Invalid request.", ct);
                    continue;
                }
                if (ParseRequest(message) is not { } request)
                {
                    await Error(transport, default, InvalidRequest, "Invalid request.", ct);
                    continue;
                }
                var (id, method, parameters) = request;
                if (request.Notification)
                {
                    if (method == "notifications/initialized" && protocol is not null)
                        ready = true;
                    if (
                        ready
                        && method == "notifications/cancelled"
                        && active is not null
                        && SameId(Property(parameters, "requestId"), active.Id)
                    )
                        active.Cancel();
                    continue;
                }
                if (active is not null && SameId(id, active.Id))
                {
                    await Error(transport, default, InvalidRequest, "Request ID is in use.", ct);
                    continue;
                }
                if (method == "ping")
                {
                    await transport.Write(id, "result", AgentProtocol.Empty.WriteTo, ct);
                    continue;
                }
                if (method == "initialize")
                {
                    if (protocol is not null)
                        await Error(transport, id, InvalidRequest, "Already initialized.", ct);
                    else if (Negotiate(parameters) is not { } negotiated)
                        await Error(transport, id, InvalidParameters, "Invalid client info.", ct);
                    else
                    {
                        protocol = negotiated;
                        await Initialized(transport, id, protocol, ct);
                    }
                    continue;
                }
                if (!ready)
                {
                    await Error(transport, id, -32002, "Session is not initialized.", ct);
                    continue;
                }
                switch (method)
                {
                    case "tools/list":
                        if (Property(parameters, "cursor").ValueKind != JsonValueKind.Undefined)
                            await Error(transport, id, InvalidParameters, "Unknown cursor.", ct);
                        else
                            await transport.Write(
                                id,
                                "result",
                                writer =>
                                {
                                    writer.WriteStartObject();
                                    writer.WritePropertyName("tools");
                                    WriteTools(writer, protocol!);
                                    writer.WriteEndObject();
                                },
                                ct
                            );
                        break;
                    case "tools/call":
                        string? name = String(Property(parameters, "name"));
                        JsonElement arguments = Property(parameters, "arguments");
                        if (
                            name is null
                            || resolve(name) is not { } command
                            || arguments.ValueKind
                                is not (JsonValueKind.Undefined or JsonValueKind.Object)
                        )
                            await Error(
                                transport,
                                id,
                                InvalidParameters,
                                "Invalid tool or arguments.",
                                ct
                            );
                        else if (active is not null)
                            await Error(
                                transport,
                                id,
                                -32000,
                                "A tool call is already in progress.",
                                ct
                            );
                        else
                        {
                            var operation = new AgentRequest(
                                AgentProtocol.Version,
                                Guid.NewGuid(),
                                command,
                                arguments.ValueKind == JsonValueKind.Undefined
                                    ? AgentProtocol.Empty
                                    : arguments
                            );
                            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            active = new(
                                id,
                                command,
                                cancellation,
                                invoke(operation, cancellation.Token)
                            );
                        }
                        break;
                    default:
                        await Error(transport, id, MethodNotFound, "Unknown method.", ct);
                        break;
                }
            }
        }
        finally
        {
            session.Cancel();
            if (active is not null)
                using (active)
                    _ = await active.Reply;
            if (incoming is not null)
                _ = incoming.ContinueWith(
                    fault => _ = fault.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
        }
    }

    static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            ? field
            : default;

    static string? String(JsonElement value) => Text(value) is { Length: > 0 } text ? text : null;

    static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            return value.GetString() is { Length: <= 512 } text ? text : null;
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json decodes escaped strings lazily. Refuse an unpaired
            // surrogate without ending a session or attempting to echo the ID.
            return null;
        }
    }

    static Request? ParseRequest(JsonElement message)
    {
        var id = Property(message, "id");
        var parameters = Property(message, "params");
        if (
            String(Property(message, "jsonrpc")) != "2.0"
            || String(Property(message, "method")) is not { } method
            || parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object)
            || message
                .EnumerateObject()
                .Any(item => item.Name is not ("id" or "jsonrpc" or "method" or "params"))
            || !(id.ValueKind == JsonValueKind.Undefined || ValidId(id))
        )
            return null;
        return new(id, method, parameters);
    }

    static bool ValidId(JsonElement id) =>
        id.ValueKind switch
        {
            JsonValueKind.String => Text(id) is not null,
            JsonValueKind.Number => id.TryGetInt64(out _),
            _ => false,
        };

    static bool Structured(string protocol) => protocol is LatestProtocol or "2025-06-18";

    void WriteTools(Utf8JsonWriter writer, string protocol)
    {
        if (Structured(protocol))
        {
            tools.WriteTo(writer);
            return;
        }
        writer.WriteStartArray();
        foreach (var tool in tools.EnumerateArray())
        {
            writer.WriteStartObject();
            foreach (var property in tool.EnumerateObject())
                if (
                    property.Name != "outputSchema"
                    && (protocol != "2024-11-05" || property.Name != "annotations")
                )
                    property.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static bool SameId(JsonElement first, JsonElement second) =>
        ValidId(first)
        && first.ValueKind == second.ValueKind
        && (
            first.ValueKind == JsonValueKind.String
                ? Text(first) == Text(second)
                : first.GetInt64() == second.GetInt64()
        );

    static string? Negotiate(JsonElement parameters)
    {
        var client = Property(parameters, "clientInfo");
        if (
            String(Property(parameters, "protocolVersion")) is not { } requested
            || Property(parameters, "capabilities").ValueKind != JsonValueKind.Object
            || String(Property(client, "name")) is null
            || String(Property(client, "version")) is null
        )
            return null;
        // The 2025-03-26 revision requires JSON-RPC batch reception.
        return requested is LatestProtocol or "2025-06-18" or "2024-11-05"
            ? requested
            : LatestProtocol;
    }

    Task Initialized(
        McpTransport transport,
        JsonElement id,
        string protocol,
        CancellationToken ct
    ) =>
        transport.Write(
            id,
            "result",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("protocolVersion", protocol);
                writer.WriteStartObject("capabilities");
                writer.WriteStartObject("tools");
                writer.WriteBoolean("listChanged", false);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteStartObject("serverInfo");
                writer.WriteString("name", "ahk-manager");
                writer.WriteString("version", version);
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            ct
        );

    static Task Error(
        McpTransport transport,
        JsonElement id,
        int code,
        string message,
        CancellationToken ct
    ) =>
        transport.Write(
            id,
            "error",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();
            },
            ct
        );

    static Task ToolResult(
        McpTransport transport,
        JsonElement id,
        string protocol,
        AgentResponse response,
        CancellationToken ct
    )
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(response, WireJson.AgentResponse);
        return transport.Write(
            id,
            "result",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("isError", response.Outcome is not ("Confirmed" or "Accepted"));
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", json);
                writer.WriteEndObject();
                writer.WriteEndArray();
                if (Structured(protocol))
                {
                    writer.WritePropertyName("structuredContent");
                    writer.WriteRawValue(json, skipInputValidation: true);
                }
                writer.WriteEndObject();
            },
            ct
        );
    }
}
