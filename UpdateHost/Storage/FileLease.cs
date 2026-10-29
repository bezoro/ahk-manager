using System.Security.Cryptography;

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
            if (
                !Native.GetFileInformationByHandle(Stream.SafeFileHandle, out var identity)
                || identity.Links != 1
            )
                throw new IOException("Multiply linked update file refused.");
        }
        catch
        {
            Stream.Dispose();
            throw;
        }
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
