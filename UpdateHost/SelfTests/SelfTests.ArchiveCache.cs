using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static async Task CheckArchiveCache(string root, UpdatePlan plan, Action<bool, string> check)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);
        await File.WriteAllTextAsync(plan.ArchivePath, "inert package fixture; never executed");
        string packageHash = Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(plan.ArchivePath)
                )
            )
            .ToLowerInvariant();
        string receiptPath = Path.Combine(Path.GetDirectoryName(plan.ArchivePath)!, "receipt.json");
        var receipt = new UpdateReceipt(
            1,
            2,
            plan.Version,
            packageHash.ToUpperInvariant(),
            new FileInfo(plan.ArchivePath).Length
        );
        await File.WriteAllBytesAsync(
            receiptPath,
            JsonSerializer.SerializeToUtf8Bytes(receipt, UpdateProtocol.Json)
        );
        using (var verified = new PackageLease(plan.ArchivePath, plan.Version, packageHash, root))
        {
            check(
                verified.Archive.Length > 0,
                "cached archive accepts a matching portable receipt and uppercase checksum"
            );
            PortableRefused(
                () => PortablePayload.ValidateArchive(verified, plan.Version),
                check,
                "a matching cache receipt cannot make a non-ZIP payload promotable"
            );
        }
        check(
            PackageLease.MaximumArchiveBytes == 1024L * 1024 * 1024,
            "archive size contract is one GiB independently of the snapshot file limit"
        );
        string overLimit = Path.Combine(root, "oversized-inert-package.zip");
        using (
            var sparse = new FileStream(
                overLimit,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )
        )
            sparse.SetLength(PackageLease.MaximumArchiveBytes + 1);
        bool oversized = false;
        try
        {
            using var invalid = FileLease.Open(overLimit, PackageLease.MaximumArchiveBytes);
        }
        catch (InvalidDataException)
        {
            oversized = true;
        }

        check(oversized, "an archive exceeding one GiB is refused before hashing or execution");
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
                    UpdateProtocol = 1,
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
                    plan.ArchivePath,
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
