using System.Security.Cryptography;
using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckVersionIdentity(Action<bool, string> check)
    {
        const string preview = "1.3.0-test.2.18.1";
        foreach (string value in new[] { "1.2.0", preview, "2147483647.0.0-test.1.2147483647.1" })
            check(
                UpdateProtocol.ReleaseVersion(value),
                "bounded stable and test release versions are accepted"
            );
        foreach (
            string value in new[]
            {
                "1.2",
                "1.2.0.0",
                "01.2.0",
                "1.3.0-test.02.18.1",
                "1.3.0-test.2.0.1",
                "1.3.0-test.2.18.0",
                "1.3.0-test.2.18",
                "1.3.0-beta.2.18.1",
                "1.3.0-test.2.18.1+build",
                "2147483648.0.0",
                "1.3.0-test.2.2147483648.1",
                preview + "/../archive.zip",
                preview + "\n",
            }
        )
            check(
                !UpdateProtocol.ReleaseVersion(value),
                "ambiguous or unbounded release identity is refused"
            );
        check(
            UpdateProtocol.SameVersion(preview, preview)
                && !UpdateProtocol.SameVersion(preview, "1.3.0")
                && !UpdateProtocol.SameVersion(preview, "1.3.0-test.2.18.2")
                && !UpdateProtocol.SameVersion(preview, "1.3.0-test.3.18.1"),
            "test release identity cannot alias a stable release or another build"
        );
        check(
            UpdateProtocol.InstalledImageVersionMatches(preview, "1.3.0.0", preview)
                && !UpdateProtocol.InstalledImageVersionMatches(preview, "1.3.0.0", "1.3.0")
                && !UpdateProtocol.InstalledImageVersionMatches(
                    preview,
                    "1.3.0.0",
                    "1.3.0-test.2.18.2"
                )
                && !UpdateProtocol.InstalledImageVersionMatches(preview, "1.3.1.0", preview)
                && !UpdateProtocol.InstalledImageVersionMatches("1.3.0", preview, preview)
                && !UpdateProtocol.InstalledImageVersionMatches("1.3.0", "1.3.0.0", preview)
                && UpdateProtocol.InstalledImageVersionMatches("1.3.0", "1.3.0.0", "1.3.0"),
            "installed test image requires exact full FileVersion and matching numeric ProductVersion"
        );
    }

    static void CheckReleaseVersions(string root, UpdatePlan plan, Action<bool, string> check)
    {
        CheckVersionIdentity(check);
        const string preview = "1.3.0-test.18.1.2";
        var testPlan = plan with
        {
            Version = preview,
            ArchivePath = Path.Combine(root, "installers", preview, "archive.zip"),
            Fallback = new CachedPackage(
                Path.Combine(root, "installers", "1.3.0", "archive.zip"),
                new string('b', 64),
                "1.3.0"
            ),
        };
        UpdateProtocol.Validate(testPlan);
        UpdatePaths.ValidatePlanLocation(
            Path.Combine(root, "operations", plan.OperationId.ToString("D"), "plan.json"),
            testPlan,
            root
        );
        check(true, "test update plan can preserve a stable fallback with the same numeric base");
        bool refused = false;
        try
        {
            UpdatePaths.ValidateArchiveLocation(
                Path.Combine(root, "installers", "1.3.0-test.2.18.2", "archive.zip"),
                preview,
                root
            );
        }
        catch (InvalidDataException)
        {
            refused = true;
        }
        check(refused, "a different test build cache cannot supply this test release archive");
    }

    static async Task CheckTestArchiveCache(string root, Action<bool, string> check)
    {
        const string preview = "1.3.0-test.18.1.2";
        string directory = Path.Combine(root, "installers", preview);
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "archive.zip");
        byte[] contents = "inert test package; never executed"u8.ToArray();
        await File.WriteAllBytesAsync(archive, contents);
        string hash = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
        string receiptPath = Path.Combine(directory, "receipt.json");
        var receipt = new UpdateReceipt(1, 2, preview, hash, contents.Length);
        await File.WriteAllBytesAsync(
            receiptPath,
            JsonSerializer.SerializeToUtf8Bytes(receipt, UpdateProtocol.Json)
        );
        using (var package = new PackageLease(archive, preview, hash, root))
            check(
                package.Archive.Length == contents.Length,
                "test archive cache accepts its exact complete version receipt"
            );
        foreach (string changedVersion in new[] { "1.3.0", "1.3.0-test.18.2.2" })
        {
            await File.WriteAllBytesAsync(
                receiptPath,
                JsonSerializer.SerializeToUtf8Bytes(
                    receipt with
                    {
                        Version = changedVersion,
                    },
                    UpdateProtocol.Json
                )
            );
            bool refused = false;
            try
            {
                using var invalid = new PackageLease(archive, preview, hash, root);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }
            check(
                refused,
                "stable or different test receipt cannot authorize the same numeric-base archive"
            );
        }
    }
}
