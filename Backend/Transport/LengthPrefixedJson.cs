using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bezoro.AhkManager.Backend;

/// <summary>Bounded, duplicate-free JSON framing for the private coordination pipes.</summary>
internal static class LengthPrefixedJson
{
    internal static async Task SendAsync<T>(
        Stream stream,
        T frame,
        JsonTypeInfo<T> type,
        int maximumBytes,
        CancellationToken ct
    )
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(frame, type);
        if (body.Length > maximumBytes)
            throw new ProtocolException("FrameTooLarge");
        await stream.WriteAsync(BitConverter.GetBytes(body.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<T> ReceiveAsync<T>(
        Stream stream,
        JsonTypeInfo<T> type,
        int maximumBytes,
        string invalidFrameCode,
        CancellationToken ct
    )
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        int length = BitConverter.ToInt32(prefix);
        if (length < 2 || length > maximumBytes)
            throw new ProtocolException("FrameTooLarge");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        Protocol.RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize(body, type)
            ?? throw new ProtocolException(invalidFrameCode);
    }
}
