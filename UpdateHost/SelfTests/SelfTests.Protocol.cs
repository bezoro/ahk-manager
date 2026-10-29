using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckProtocol(string root, UpdatePlan plan, Action<bool, string> check)
    {
        UpdateProtocol.Validate(plan);
        UpdatePaths.ValidatePlanLocation(
            Path.Combine(root, "operations", plan.OperationId.ToString("D"), "plan.json"),
            plan,
            root
        );
        check(
            UpdateProtocol.Read<UpdatePlan>(
                JsonSerializer.SerializeToUtf8Bytes(plan, UpdateProtocol.Json)
            ) == plan,
            "update plan strict JSON roundtrip preserves operation and parent identity"
        );
        foreach (
            var invalid in new[]
            {
                plan with
                {
                    SchemaVersion = 2,
                },
                plan with
                {
                    OperationId = Guid.Empty,
                },
                plan with
                {
                    ParentPid = 0,
                },
                plan with
                {
                    Version = "1.2.0-beta",
                },
                plan with
                {
                    InstallerSha256 = new string('A', 64),
                },
                plan with
                {
                    InstallerPath = "https://example.invalid/installer.exe",
                },
                plan with
                {
                    InstallDirectory = @"\\host\share",
                },
            }
        )
        {
            bool refused = false;
            try
            {
                UpdateProtocol.Validate(invalid);
            }
            catch (Exception ex) when (UpdateProtocol.Known(ex))
            {
                refused = true;
            }

            check(refused, "invalid protocol identity version checksum or remote path is refused");
        }

        foreach (
            string invalid in new[]
            {
                "{\"schemaVersion\":1,\"schemaVersion\":1}",
                "{}",
                JsonSerializer
                    .Serialize(plan, UpdateProtocol.Json)
                    .Replace(
                        "\"parentPid\":42",
                        "\"parentPid\":42,\"unknown\":true",
                        StringComparison.Ordinal
                    ),
            }
        )
        {
            bool refused = false;
            try
            {
                _ = UpdateProtocol.Read<UpdatePlan>(Encoding.UTF8.GetBytes(invalid));
            }
            catch (Exception ex) when (UpdateProtocol.Known(ex))
            {
                refused = true;
            }

            check(
                refused,
                "duplicate missing and unknown update properties do not become apply authority"
            );
        }

        check(
            UpdateProtocol.SameVersion("1.2", "1.2.0.0")
                && !UpdateProtocol.SameVersion("1.2.0", "1.2.1")
                && UpdateProtocol.Hash(new string('A', 64), false),
            "numeric version and receipt hash rules are explicit"
        );
        foreach (
            string path in new[]
            {
                Path.Combine(root, "installers", "1.2.0", "installer.exe"),
                Path.Combine(root, "downloads", "installer.exe"),
                Path.Combine(root, "installers", "1.2.1", "other.exe"),
            }
        )
        {
            bool refused = false;
            try
            {
                UpdatePaths.ValidateInstallerLocation(path, plan.Version, root);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            check(refused, "only the exact immutable version cache can supply an installer");
        }
    }

    static void CheckInstallerCommandLine(string root, UpdatePlan plan, Action<bool, string> check)
    {
        string spacedDestination = Path.Combine(root, "Manager with spaces");
        var nsis = NativeApplyEffects.InstallerStartInfo(plan.InstallerPath, spacedDestination);
        check(
            !nsis.UseShellExecute
                && nsis.ArgumentList.Count == 0
                && nsis.Arguments == "/S --updated /D=" + spacedDestination,
            "NSIS receives its final destination with spaces unquoted and without a shell"
        );
        foreach (
            string invalid in new[]
            {
                spacedDestination + "\r/S",
                spacedDestination + "\n/S",
                spacedDestination + "\0/S",
                spacedDestination + "\" /S",
            }
        )
        {
            bool refused = false;
            try
            {
                _ = NativeApplyEffects.InstallerStartInfo(plan.InstallerPath, invalid);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            check(refused, "NSIS destination refuses command line control characters and quotes");
        }
    }
}
