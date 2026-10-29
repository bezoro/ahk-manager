using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

internal static class SafeJsonFiles
{
    public const int MaximumBytes = 2_000_000;

    public static bool Known(Exception ex) =>
        ex
            is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or System.Security.SecurityException;

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static void SafePath(string path)
    {
        if (
            !System.IO.Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.AsSpan(2).Contains(':')
        )
            throw new IOException("Local absolute path required.");
        for (
            string? current = path;
            current is not null;
            current = System.IO.Path.GetDirectoryName(current)
        )
            if (
                (File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
            )
                throw new IOException("Redirected path refused.");
    }

    public static async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        SafePath(path);
        if (!File.Exists(path))
            return null;
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16384,
            FileOptions.Asynchronous
        );
        if (input.Length > MaximumBytes)
            throw new InvalidDataException("File exceeds limit.");
        byte[] bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (input.Position != input.Length)
            throw new InvalidDataException("File changed during read.");
        return bytes;
    }

    public static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
    {
        SafePath(path);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("File exceeds limit.");
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16384,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(true);
    }

    public static async Task<string> ReplaceAsync(
        string path,
        byte[] bytes,
        string? expectedHash,
        CancellationToken ct,
        bool backup = true
    )
    {
        SafePath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await using var lease = new FileStream(
            path + ".write.lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var current = await ReadAsync(path, ct).ConfigureAwait(false);
        if ((current is null ? null : Hash(current)) != expectedHash)
            throw new ConcurrentWriteException();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool owned = false;
        try
        {
            await WriteNewAsync(temporary, bytes, ct).ConfigureAwait(false);
            owned = true;
            ct.ThrowIfCancellationRequested();
            if (current is not null)
                File.Replace(temporary, path, backup ? path + ".bak" : null);
            else
                File.Move(temporary, path);
            return Hash(bytes);
        }
        finally
        {
            if (owned && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = [];
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Duplicate JSON property.");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                UniqueProperties(item);
    }
}
