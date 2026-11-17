using System.Text.Json;

namespace Bezoro.AhkManager.AgentCli;

internal sealed class McpToolCatalog
{
    static readonly JsonElement ResponseSchema = AgentProtocol.Parse(
        """
        {
          "type": "object",
          "properties": {
            "protocol": { "const": 1 },
            "requestId": { "type": "string", "format": "uuid" },
            "version": { "type": "string" },
            "generation": { "type": ["string", "null"] },
            "outcome": { "enum": ["Confirmed", "Accepted", "Partial", "Failed", "Unknown"] },
            "code": { "type": "string" },
            "result": { "description": "The result or diagnostic data for this command." }
          },
          "required": ["protocol", "requestId", "version", "generation", "outcome", "code", "result"],
          "additionalProperties": false
        }
        """u8.ToArray(),
        JsonValueKind.Object
    );

    readonly Dictionary<string, string> commands = new(StringComparer.Ordinal);
    readonly HashSet<string> reviewed = new(StringComparer.Ordinal);

    internal McpToolCatalog(string catalogJson)
    {
        var entries = AgentProtocol.Parse(
            AgentProtocol.Utf8.GetBytes(catalogJson),
            JsonValueKind.Array
        );
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var entry in entries.EnumerateArray())
            {
                var surface = entry.GetProperty("surface").GetString();
                if (surface == "desktop")
                    continue;
                if (
                    surface != "management"
                    || entry.GetProperty("name").GetString() is not { } command
                    || !AgentProtocol.CommandName(command)
                )
                    throw new InvalidDataException("Invalid tool catalog entry.");
                string name = command.Replace('.', '_');
                if (!commands.TryAdd(name, command))
                    throw new InvalidDataException("Duplicate tool catalog entry.");
                if (entry.TryGetProperty("confirmation", out _))
                    reviewed.Add(command);
                WriteTool(writer, name, entry);
            }
            writer.WriteEndArray();
        }
        Tools = AgentProtocol.Parse(output.ToArray(), JsonValueKind.Array);
    }

    internal JsonElement Tools { get; }

    internal string? Resolve(string name) => commands.GetValueOrDefault(name);

    internal bool AcceptsPreview(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty("command", out var command)
        && command.ValueKind == JsonValueKind.String
        && reviewed.Contains(command.GetString()!)
        && args.TryGetProperty("arguments", out var arguments)
        && arguments.ValueKind == JsonValueKind.Object;

    static void WriteTool(Utf8JsonWriter writer, string name, JsonElement entry)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        string? description = entry.GetProperty("description").GetString();
        if (entry.TryGetProperty("mcpNotes", out var notes))
            description += ". " + notes.GetString();
        writer.WriteString("description", description);
        writer.WritePropertyName("inputSchema");
        entry.GetProperty("inputSchema").WriteTo(writer);
        writer.WritePropertyName("outputSchema");
        ResponseSchema.WriteTo(writer);
        writer.WriteStartObject("annotations");
        var effect = entry.GetProperty("effect").GetString();
        if (effect is not ("read" or "change" or "destructive"))
            throw new InvalidDataException("Invalid tool effect.");
        writer.WriteBoolean("readOnlyHint", effect == "read");
        if (effect != "read")
            writer.WriteBoolean("destructiveHint", effect == "destructive");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
