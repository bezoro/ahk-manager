using System.Text.Json;
using Bezoro.AhkManager.AgentCli;

internal static class McpTransportChecks
{
    const string Version = "2.3.0";
    const string Initialize = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"MCP fixture","version":"1"}}}
        """;
    const string Ready = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    const string List = """{"jsonrpc":"2.0","id":"list","method":"tools/list","params":{}}""";
    const string Ping = """{"jsonrpc":"2.0","id":"ping","method":"ping"}""";
    const string Status = """
        {"jsonrpc":"2.0","id":"status","method":"tools/call","params":{"name":"app_status","arguments":{}}}
        """;
    const string Update = """
        {"jsonrpc":"2.0","id":"update","method":"tools/call","params":{"name":"updates_installLatest"}}
        """;
    const string Unknown = """
        {"jsonrpc":"2.0","id":"unlisted","method":"tools/call","params":{"name":"arbitrary_command"}}
        """;
    const string Cancel = """
        {"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"status"}}
        """;

    static readonly JsonElement Tools = AgentProtocol.Parse(
        """[{"name":"app_status","description":"Read status","inputSchema":{"type":"object"},"outputSchema":{"type":"object"}}]"""u8.ToArray(),
        JsonValueKind.Array
    );

    internal static async Task Run()
    {
        var calls = new List<AgentRequest>();
        Task<AgentResponse> Invoke(AgentRequest request, CancellationToken _)
        {
            calls.Add(request);
            return Task.FromResult(Response(request, "Failed", "ElevationRequired"));
        }

        var lines = await Exchange(Invoke, List, Initialize, List, Ready, List, Unknown, Status);
        Check(lines.Length == 6, "one JSON-RPC response per request and no notification response");
        Check(
            Error(lines[0]) == -32002 && Error(lines[2]) == -32002,
            "reject calls until initialized"
        );
        var initial = lines[1].GetProperty("result");
        Check(
            initial.GetProperty("protocolVersion").GetString() == "2025-06-18"
                && initial.GetProperty("serverInfo").GetProperty("version").GetString() == Version,
            "initialization negotiates the client's supported version and advertises the build"
        );
        Check(
            lines[3].GetProperty("id").GetString() == "list"
                && lines[3]
                    .GetProperty("result")
                    .GetProperty("tools")[0]
                    .GetProperty("name")
                    .GetString() == "app_status",
            "offline listing preserves the string RPC ID and publishes exact catalog tools"
        );
        Check(
            Error(lines[4]) == -32602 && calls is [{ Command: "app.status" }],
            "dispatch only known tools"
        );
        var result = lines[5].GetProperty("result");
        var body = result.GetProperty("structuredContent");
        Check(
            result.GetProperty("isError").GetBoolean()
                && body.GetProperty("requestId").GetGuid() == calls[0].RequestId
                && body.GetProperty("code").GetString() == "ElevationRequired"
                && result.GetProperty("content")[0].GetProperty("text").GetString()
                    == body.GetRawText(),
            "native refusals retain command identity and identical structured/text content"
        );

        lines = await Exchange(
            Invoke,
            Initialize.Replace("2025-06-18", "2024-11-05", StringComparison.Ordinal),
            Ready,
            List,
            Status
        );
        Check(
            !lines[1]
                .GetProperty("result")
                .GetProperty("tools")[0]
                .TryGetProperty("outputSchema", out _)
                && !lines[2].GetProperty("result").TryGetProperty("structuredContent", out _),
            "a legacy MCP client receives compatible tool metadata and text content"
        );
        lines = await Exchange(
            Invoke,
            Initialize.Replace("2025-06-18", "2099-01-01", StringComparison.Ordinal)
        );
        Check(
            lines[0].GetProperty("result").GetProperty("protocolVersion").GetString()
                == "2025-11-25",
            "unknown protocol receives latest supported version"
        );

        lines = await Exchange(
            Invoke,
            Initialize.Replace("2025-06-18", "2025-03-26", StringComparison.Ordinal)
        );
        Check(
            lines[0].GetProperty("result").GetProperty("protocolVersion").GetString()
                == "2025-11-25",
            "a batching client can decide whether it supports the offered non-batching revision"
        );

        calls.Clear();
        lines = await Exchange(
            Invoke,
            "{",
            "[]",
            """{"jsonrpc":"2.0","method":"ping","id":"\ud800"}""",
            Initialize,
            Ready,
            Status.Replace("{}", "[]", StringComparison.Ordinal),
            Ping
        );
        Check(
            lines.Length == 6
                && Error(lines[0]) == -32700
                && Error(lines[1]) == -32600
                && Error(lines[2]) == -32600
                && Error(lines[4]) == -32602
                && lines[5].GetProperty("id").GetString() == "ping"
                && calls.Count == 0,
            "malformed inputs get finite errors without losing framing or invoking a command"
        );

        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using (var input = new HoldOpenStream(Encode(Initialize, Ready, Status, Ping, Cancel)))
        using (var output = new MemoryStream())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var session = Server(WaitForCancellation).Run(input, output, timeout.Token);
            await cancelled.Task.WaitAsync(timeout.Token);
            input.Finish();
            await session;
            lines = ReadLines(output);
            Check(
                lines.Length == 2 && lines[1].GetProperty("id").GetString() == "ping",
                "ping and cancellation can proceed before stdin closes while an operation is pending"
            );
        }

        cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lines = await Exchange(WaitForCancellation, Initialize, Ready, Status);
        Check(
            cancelled.Task.IsCompleted && lines.Length == 1,
            "stdio EOF cancels the call and omits its reply"
        );

        // NEL can be one of the largest expansions in the narrower .NET JSON
        // encoder. The full result must survive normalized and repeated escaping.
        string padding = new('\u0085', 128 * 1024);
        string detail = padding + "\r\ntext\"";
        var content = AgentProtocol.Parse(
            AgentProtocol.Utf8.GetBytes("{\"detail\":\"" + padding + "\\r\\ntext\\\"\"}"),
            JsonValueKind.Object
        );
        lines = await Exchange(
            (request, _) =>
                Task.FromResult(
                    Response(request, "Unknown", "ReplyUnconfirmed") with
                    {
                        Result = content,
                    }
                ),
            Initialize,
            Ready,
            Status
        );
        var largeReply = lines[1].GetProperty("result");
        var structured = largeReply.GetProperty("structuredContent");
        Check(
            structured.GetProperty("result").GetProperty("detail").GetString() == detail
                && structured.GetProperty("outcome").GetString() == "Unknown"
                && largeReply.GetProperty("isError").GetBoolean()
                && largeReply.GetProperty("content")[0].GetProperty("text").GetString()
                    == structured.GetRawText(),
            "large escaped content fits one line while preserving structured data and the uncertain outcome"
        );

        lines = await Exchange(
            (request, _) => Task.FromResult(Response(request, "Accepted", "OperationAccepted")),
            Initialize,
            Ready,
            Update,
            Ping
        );
        Check(
            lines.Length == 2
                && lines[1].GetProperty("id").GetString() == "update"
                && !lines[1].GetProperty("result").GetProperty("isError").GetBoolean()
                && lines[1]
                    .GetProperty("result")
                    .GetProperty("structuredContent")
                    .GetProperty("outcome")
                    .GetString() == "Accepted",
            "an accepted replacement sends its full result before ending the session"
        );

        await CheckFrameLimit(new string(' ', McpTransport.MaximumLine + 1) + "\n");
        await CheckFrameLimit("{");

        async Task<AgentResponse> WaitForCancellation(AgentRequest request, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled.TrySetResult();
            }
            return Response(request, "Unknown", "ReplyUnconfirmed");
        }

        async Task CheckFrameLimit(string bytes)
        {
            using var input = new MemoryStream(AgentProtocol.Utf8.GetBytes(bytes));
            using var output = new MemoryStream();
            await Server(Invoke).Run(input, output, default);
            var frame = AgentProtocol.Parse(output.ToArray(), JsonValueKind.Object);
            Check(Error(frame) == -32700, "oversized and incomplete frames fail closed");
        }
    }

    static async Task<JsonElement[]> Exchange(
        Func<AgentRequest, CancellationToken, Task<AgentResponse>> invoke,
        params string[] lines
    )
    {
        // Exercise both CRLF and single-byte fragmented reads.
        using var input = new FragmentedStream(Encode(lines));
        using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Server(invoke).Run(input, output, timeout.Token);
        return ReadLines(output);
    }

    static byte[] Encode(params string[] lines) =>
        AgentProtocol.Utf8.GetBytes(string.Join("\r\n", lines) + "\r\n");

    static JsonElement[] ReadLines(MemoryStream output)
    {
        var text = AgentProtocol.Utf8.GetString(output.ToArray());
        Check(text.EndsWith('\n'), "each output frame is delimited");
        return text[..^1]
            .Split('\n')
            .Select(line =>
                AgentProtocol.Parse(AgentProtocol.Utf8.GetBytes(line), JsonValueKind.Object)
            )
            .ToArray();
    }

    static McpServer Server(Func<AgentRequest, CancellationToken, Task<AgentResponse>> invoke) =>
        new(
            Version,
            Tools,
            name =>
                name switch
                {
                    "app_status" => "app.status",
                    "updates_installLatest" => "updates.installLatest",
                    _ => null,
                },
            invoke
        );

    static AgentResponse Response(AgentRequest request, string outcome, string code) =>
        new(AgentProtocol.Version, request.RequestId, Version, null, outcome, code, null);

    static int Error(JsonElement response) =>
        response.GetProperty("error").GetProperty("code").GetInt32();

    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
    }

    sealed class HoldOpenStream(byte[] bytes) : MemoryStream(bytes)
    {
        readonly TaskCompletionSource eof = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Finish() => eof.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            int length = await base.ReadAsync(buffer, ct);
            if (length == 0)
                await eof.Task.WaitAsync(ct);
            return length;
        }
    }
}
