namespace Bezoro.AhkManager.UpdateHost;

internal sealed class PackageLease : IDisposable
{
    internal const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    internal readonly FileLease Archive;
    readonly FileLease receipt;

    internal PackageLease(string path, string version, string sha256, string updatesRoot)
    {
        UpdatePaths.ValidateArchiveLocation(path, version, updatesRoot);
        Archive = FileLease.Open(path, MaximumArchiveBytes);
        try
        {
            if (Archive.Length == 0 || Archive.Hash() != sha256)
                throw new InvalidDataException("Archive checksum changed or archive is empty.");
            receipt = FileLease.Open(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "receipt.json"),
                UpdateProtocol.MaximumPlanBytes
            );
            try
            {
                var proof = UpdateProtocol.Read<UpdateReceipt>(
                    receipt.Read(UpdateProtocol.MaximumPlanBytes)
                );
                if (
                    proof.SchemaVersion != 1
                    || proof.UpdateProtocol != 2
                    || proof.Version != version
                    || !UpdateProtocol.Hash(proof.Sha256, false)
                    || !proof.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)
                    || proof.Bytes.HasValue && proof.Bytes.Value != Archive.Length
                )
                    throw new InvalidDataException("Archive protocol receipt changed.");
            }
            catch
            {
                receipt.Dispose();
                throw;
            }
        }
        catch
        {
            Archive.Dispose();
            throw;
        }
    }

    internal void Verify(string hash)
    {
        if (Archive.Hash() != hash)
            throw new InvalidDataException("Pinned archive checksum changed.");
    }

    public void Dispose()
    {
        receipt.Dispose();
        Archive.Dispose();
    }
}
