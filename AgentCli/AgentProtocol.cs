using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.AgentCli;

internal sealed record AgentRequest(
    int Protocol,
    Guid RequestId,
    string Command,
    JsonElement Arguments
);

internal sealed record AgentResponse(
    int Protocol,
    Guid RequestId,
    string Version,
    string? Generation,
    string Outcome,
    string Code,
    JsonElement? Result
);

internal sealed record AgentReceipt(int Protocol, Guid RequestId, bool Delivered);

internal sealed record AgentCancellation(int Protocol, Guid RequestId, bool Canceled);

internal sealed record AgentHelp(string Usage, string Commands, string Version);

internal sealed record AgentVersion(string ClientVersion);

internal sealed record AgentOperationId(Guid OperationId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
[JsonSerializable(typeof(AgentReceipt))]
[JsonSerializable(typeof(AgentCancellation))]
[JsonSerializable(typeof(AgentHelp))]
[JsonSerializable(typeof(AgentVersion))]
[JsonSerializable(typeof(AgentOperationId))]
internal partial class AgentJson : JsonSerializerContext { }

internal static class AgentProtocol
{
    internal const int Version = 1;
    internal const int MaxRequest = 1024 * 1024;
    internal const int MaxResponse = 8 * 1024 * 1024;
    internal const int MaxReceipt = 512;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonElement Empty = Parse("{}"u8.ToArray(), JsonValueKind.Object);

    internal static async Task<byte[]> Read(Stream stream, int limit, CancellationToken ct)
    {
        byte[] prefix = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(prefix, ct);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0 || length > limit)
            throw new InvalidDataException("FrameSize");
        byte[] bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    internal static async Task Write(Stream stream, byte[] bytes, int limit, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes.Length > limit)
            throw new InvalidDataException("FrameSize");
        byte[] prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)bytes.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    internal static JsonElement Parse(byte[] bytes, JsonValueKind kind)
    {
        _ = Utf8.GetCharCount(bytes);
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != kind)
            throw new InvalidDataException("JsonType");
        UniqueProperties(json.RootElement);
        return json.RootElement.Clone();
    }

    static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("DuplicateProperty");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                UniqueProperties(child);
        }
    }

    static JsonElement Envelope(byte[] bytes, params string[] fields)
    {
        var value = Parse(bytes, JsonValueKind.Object);
        if (
            value.EnumerateObject().Count() != fields.Length
            || fields.Any(field => !value.TryGetProperty(field, out _))
            || value.GetProperty("protocol").ValueKind != JsonValueKind.Number
            || !value.GetProperty("protocol").TryGetInt32(out var protocol)
            || protocol != Version
            || value.GetProperty("requestId").ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(
                value.GetProperty("requestId").GetString(),
                "D",
                out var requestId
            )
            || requestId == Guid.Empty
        )
            throw new InvalidDataException("InvalidEnvelope");
        return value;
    }

    internal static AgentRequest ReadRequest(byte[] bytes)
    {
        var json = Envelope(bytes, "protocol", "requestId", "command", "arguments");
        if (
            json.GetProperty("command").ValueKind != JsonValueKind.String
            || !CommandName(json.GetProperty("command").GetString())
            || json.GetProperty("arguments").ValueKind != JsonValueKind.Object
        )
            throw new InvalidDataException("InvalidRequest");
        return json.Deserialize(AgentJson.Default.AgentRequest)!;
    }

    internal static AgentResponse ReadResponse(byte[] bytes)
    {
        var json = Envelope(
            bytes,
            "protocol",
            "requestId",
            "version",
            "generation",
            "outcome",
            "code",
            "result"
        );
        if (
            new[] { "version", "generation", "outcome", "code" }.Any(field =>
                json.GetProperty(field).ValueKind != JsonValueKind.String
                || json.GetProperty(field).GetString()!.Length is < 1 or > 200
            )
        )
            throw new InvalidDataException("InvalidResponse");
        var result = json.Deserialize(AgentJson.Default.AgentResponse)!;
        if (result.Outcome is not ("Confirmed" or "Accepted" or "Partial" or "Failed" or "Unknown"))
            throw new InvalidDataException("InvalidOutcome");
        return result;
    }

    internal static AgentReceipt ReadReceipt(byte[] bytes)
    {
        var json = Envelope(bytes, "protocol", "requestId", "delivered");
        if (
            json.GetProperty("delivered").ValueKind
            is not (JsonValueKind.True or JsonValueKind.False)
        )
            throw new InvalidDataException("InvalidReceipt");
        return json.Deserialize(AgentJson.Default.AgentReceipt)!;
    }

    internal static bool CommandName(string? name) =>
        name is { Length: > 0 and <= 100 }
        && char.IsAsciiLetter(name[0])
        && name.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_');
}
