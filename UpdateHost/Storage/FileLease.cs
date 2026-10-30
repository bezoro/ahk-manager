using System.Security.Cryptography;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class FileLease : IDisposable
{
    internal readonly FileStream Stream;
    internal readonly string Path;
    internal long Length => Stream.Length;

    FileLease(string path)
    {
        Path = UpdatePaths.Local(path);
        UpdatePaths.NoRedirects(Path);
        if ((File.GetAttributes(Path) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new IOException("Regular update file required.");
        // Inspect the entry without following a reparse point, then bind the
        // ordinary data open to that same file. Hydrated cloud providers keep
        // their normal data-open behavior; a concurrent link swap is refused.
        using var metadata = WindowsStoragePath.OpenVerifiedMetadata(Path, retainName: true);
        if (!Native.GetFileInformationByHandle(metadata, out var entryIdentity))
            throw new IOException("Update entry identity could not be verified.");
        Stream = new(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.SequentialScan
        );
        try
        {
            WindowsStoragePath.VerifyHandle(Stream.SafeFileHandle);
            if (!Native.GetFileInformationByHandle(Stream.SafeFileHandle, out var identity))
                throw new IOException("Update file identity could not be verified.");
            VerifyIdentity(entryIdentity, identity);
        }
        catch
        {
            Stream.Dispose();
            throw;
        }
    }

    internal static void VerifyIdentity(Native.FileStamp entry, Native.FileStamp opened)
    {
        const uint nonFile = (uint)(FileAttributes.Directory | FileAttributes.Device);
        if (
            entry.Links != 1
            || opened.Links != 1
            || entry.Volume != opened.Volume
            || entry.IndexHigh != opened.IndexHigh
            || entry.IndexLow != opened.IndexLow
            || ((entry.Attributes | opened.Attributes) & nonFile) != 0
        )
            throw new IOException("Changed or multiply linked update file refused.");
    }

    internal static FileLease Open(string path, long maximumBytes)
    {
        var lease = new FileLease(path);
        if (lease.Length > maximumBytes)
        {
            lease.Dispose();
            throw new InvalidDataException("Update file exceeds its size limit.");
        }

        return lease;
    }

    internal string Hash()
    {
        Stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(Stream)).ToLowerInvariant();
        Stream.Position = 0;
        return hash;
    }

    internal byte[] Read(int maximumBytes)
    {
        if (Length > maximumBytes)
            throw new InvalidDataException("Update document exceeds its size limit.");
        Stream.Position = 0;
        using var bytes = new MemoryStream();
        Stream.CopyTo(bytes);
        Stream.Position = 0;
        return bytes.ToArray();
    }

    public void Dispose() => Stream.Dispose();
}
