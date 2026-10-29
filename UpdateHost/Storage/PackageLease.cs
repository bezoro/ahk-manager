namespace Bezoro.AhkManager.UpdateHost;

internal sealed class PackageLease : IDisposable
{
    internal const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    internal readonly FileLease Installer;
    readonly FileLease receipt;

    internal PackageLease(string path, string version, string sha256, string updatesRoot)
    {
        UpdatePaths.ValidateInstallerLocation(path, version, updatesRoot);
        Installer = FileLease.Open(path, MaximumInstallerBytes);
        try
        {
            if (Installer.Length == 0 || Installer.Hash() != sha256)
                throw new InvalidDataException("Installer checksum changed or installer is empty.");
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
                    || proof.UpdateProtocol != 1
                    || proof.Version != version
                    || !UpdateProtocol.Hash(proof.Sha256, false)
                    || !proof.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)
                    || proof.Bytes.HasValue && proof.Bytes.Value != Installer.Length
                )
                    throw new InvalidDataException("Installer protocol receipt changed.");
            }
            catch
            {
                receipt.Dispose();
                throw;
            }
        }
        catch
        {
            Installer.Dispose();
            throw;
        }
    }

    internal void Verify(string hash)
    {
        if (Installer.Hash() != hash)
            throw new InvalidDataException("Pinned installer checksum changed.");
    }

    public void Dispose()
    {
        receipt.Dispose();
        Installer.Dispose();
    }
}
