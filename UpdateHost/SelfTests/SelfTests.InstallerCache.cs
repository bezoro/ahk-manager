using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static async Task CheckInstallerCache(string root, UpdatePlan plan, Action<bool, string> check)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.InstallerPath)!);
        await File.WriteAllTextAsync(plan.InstallerPath, "inert package fixture; never executed");
        string packageHash = Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(plan.InstallerPath)
                )
            )
            .ToLowerInvariant();
        string receiptPath = Path.Combine(
            Path.GetDirectoryName(plan.InstallerPath)!,
            "receipt.json"
        );
        var receipt = new UpdateReceipt(
            1,
            1,
            plan.Version,
            packageHash.ToUpperInvariant(),
            new FileInfo(plan.InstallerPath).Length
        );
        await File.WriteAllBytesAsync(
            receiptPath,
            JsonSerializer.SerializeToUtf8Bytes(receipt, UpdateProtocol.Json)
        );
        using (var verified = new PackageLease(plan.InstallerPath, plan.Version, packageHash, root))
            check(
                verified.Installer.Length > 0,
                "cached package accepts matching protocol receipt and uppercase NSIS checksum"
            );
        check(
            PackageLease.MaximumInstallerBytes == 1024L * 1024 * 1024,
            "installer size contract is one GiB independently of the snapshot file limit"
        );
        string overLimit = Path.Combine(root, "oversized-inert-package.exe");
        using (
            var sparse = new FileStream(
                overLimit,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )
        )
            sparse.SetLength(PackageLease.MaximumInstallerBytes + 1);
        bool oversized = false;
        try
        {
            using var invalid = FileLease.Open(overLimit, PackageLease.MaximumInstallerBytes);
        }
        catch (InvalidDataException)
        {
            oversized = true;
        }

        check(oversized, "an installer exceeding one GiB is refused before hashing or execution");
        using (
            var fixture = new FileStream(overLimit, FileMode.Open, FileAccess.Write, FileShare.None)
        )
            fixture.SetLength(0);
        foreach (
            var changedReceipt in new[]
            {
                receipt with
                {
                    UpdateProtocol = 0,
                },
                receipt with
                {
                    Version = "1.2.0",
                },
                receipt with
                {
                    Bytes = 1,
                },
                receipt with
                {
                    Sha256 = new string('b', 64),
                },
            }
        )
        {
            await File.WriteAllBytesAsync(
                receiptPath,
                JsonSerializer.SerializeToUtf8Bytes(changedReceipt, UpdateProtocol.Json)
            );
            bool refused = false;
            try
            {
                using var invalid = new PackageLease(
                    plan.InstallerPath,
                    plan.Version,
                    packageHash,
                    root
                );
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            check(
                refused,
                "changed protocol version bytes or checksum cannot authorize cached package use"
            );
        }
    }
}
