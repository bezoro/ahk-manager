using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

// MCP stdio uses one UTF-8 message per line. A fixed-size read buffer handles
// split and coalesced messages without letting a client grow an unbounded line.
internal sealed class McpTransport(Stream input, Stream output)
{
    internal const int MaximumLine = AgentProtocol.MaxRequest + 4096;

    // A client may need the same result as an object and as escaped JSON text.
    // Account for Unicode escaping in both copies of the bounded agent response.
    internal const int MaximumResponse = 7 * AgentProtocol.MaxResponse + 4096;
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly byte[] buffer = new byte[8192];
    int begin;
    int end;

    internal async Task<byte[]?> Read(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (begin == end)
            {
                end = await input.ReadAsync(buffer, ct);
                begin = 0;
                if (end == 0)
                {
                    if (line.Length != 0)
                        throw new EndOfStreamException("Incomplete MCP message.");
                    return null;
                }
            }
            int newline = Array.IndexOf(buffer, (byte)'\n', begin, end - begin);
            int count = (newline < 0 ? end : newline) - begin;
            if (line.Length + count > MaximumLine)
                throw new InvalidDataException("MCP message exceeds the line limit.");
            line.Write(buffer, begin, count);
            begin += count;
            if (newline >= 0)
            {
                begin++;
                if (line.Length > 0 && line.GetBuffer()[line.Length - 1] == (byte)'\r')
                    line.SetLength(line.Length - 1);
                return line.ToArray();
            }
        }
    }

    internal async Task Write(
        JsonElement id,
        string member,
        Action<Utf8JsonWriter> write,
        CancellationToken ct
    )
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id.ValueKind == JsonValueKind.Undefined)
                writer.WriteNullValue();
            else
                id.WriteTo(writer);
            writer.WritePropertyName(member);
            write(writer);
            writer.WriteEndObject();
            writer.Flush();
        }
        if (bytes.WrittenCount > MaximumResponse)
            throw new InvalidDataException("MCP response exceeds the line limit.");
        bytes.GetSpan(1)[0] = (byte)'\n';
        bytes.Advance(1);
        await output.WriteAsync(bytes.WrittenMemory, ct);
        await output.FlushAsync(ct);
    }
}
