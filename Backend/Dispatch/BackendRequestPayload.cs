using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Backend;

internal static class BackendRequestPayload
{
    internal static T Read<T>(JsonElement payload, JsonTypeInfo<T> type) =>
        payload.Deserialize(type) ?? throw new ProtocolException("InvalidRequest");

    internal static JsonElement Pack<T>(T value, JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToElement(value, type);

    internal static void Empty(JsonElement payload) =>
        _ = Read(payload, Protocol.Json.EmptyPayload);

    internal static Guid Nonempty(Guid id) =>
        id != Guid.Empty ? id : throw new ProtocolException("InvalidRequest");

    internal static void ValidateSelection(SelectionContext selection)
    {
        if (
            selection is null
            || selection.ScriptId == Guid.Empty
            || selection.StateRevision < 0
            || selection.Query is null
            || selection.Query.Version < 0
            || selection.Query.Search is null
            || selection.Query.Search.Length > 1000
            || !Enum.IsDefined(selection.Query.Status)
            || !Enum.IsDefined(selection.Query.Sort)
        )
            throw new ProtocolException("InvalidRequest");
    }
}
