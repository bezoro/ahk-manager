using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.Backend;

internal sealed class FrameReader(Stream input)
{
    readonly byte[] buffer = new byte[16384];
    int position,
        length;

    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (position == length)
            {
                length = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                position = 0;
                if (length == 0)
                {
                    if (frame.Length != 0)
                        throw new ProtocolException("InvalidFrame");
                    return null;
                }
            }
            int newline = Array.IndexOf(buffer, (byte)'\n', position, length - position);
            int end = newline >= 0 ? newline : length;
            if (frame.Length + end - position > Protocol.MaximumFrameBytes + 1)
                throw new ProtocolException("FrameTooLarge");
            frame.Write(buffer, position, end - position);
            position = end;
            if (newline < 0)
                continue;
            position++;
            byte[] bytes = frame.ToArray();
            if (bytes.Length > 0 && bytes[^1] == '\r')
                Array.Resize(ref bytes, bytes.Length - 1);
            if (bytes.Length == 0)
                throw new ProtocolException("InvalidFrame");
            if (bytes.Length > Protocol.MaximumFrameBytes)
                throw new ProtocolException("FrameTooLarge");
            try
            {
                _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new ProtocolException("InvalidFrame");
            }
            if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }))
                throw new ProtocolException("InvalidFrame");
            return bytes;
        }
    }
}

internal static class FrameWriter
{
    public static byte[] Encode(ResponseFrame frame)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Protocol.Json.ResponseFrame);
        if (bytes.Length > Protocol.MaximumFrameBytes)
            throw new ProtocolException("FrameTooLarge");
        return bytes;
    }

    public static async Task WriteAsync(Stream output, byte[] bytes, CancellationToken ct)
    {
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
        await output.WriteAsync(new byte[] { (byte)'\n' }, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
