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
        using (
            var serialized = JsonDocument.Parse(
                JsonSerializer.SerializeToUtf8Bytes(plan, UpdateProtocol.Json)
            )
        )
        {
            var wire = serialized.RootElement;
            check(
                wire.GetProperty("schemaVersion").GetInt32() == 2
                    && wire.GetProperty("deployment").GetString() == "portable"
                    && wire.GetProperty("installDirectory").GetString() == plan.ApplicationDirectory
                    && wire.GetProperty("installerPath").GetString() == plan.ArchivePath
                    && wire.GetProperty("installerSha256").GetString() == plan.ArchiveSha256
                    && !wire.TryGetProperty("archivePath", out _),
                "portable plans retain the deployed protocol 2 JSON keys without adding another format"
            );
        }
        var withFallback = plan with
        {
            Fallback = new CachedPackage(
                Path.Combine(root, "installers", "1.2.0", "archive.zip"),
                new string('b', 64),
                "1.2.0"
            ),
        };
        UpdateProtocol.Validate(withFallback);
        byte[] fallbackBytes = JsonSerializer.SerializeToUtf8Bytes(
            withFallback,
            UpdateProtocol.Json
        );
        using (var serialized = JsonDocument.Parse(fallbackBytes))
        {
            var fallback = serialized.RootElement.GetProperty("fallback");
            check(
                fallback.GetProperty("installerPath").GetString()
                    == withFallback.Fallback!.ArchivePath
                    && fallback.GetProperty("installerSha256").GetString()
                        == withFallback.Fallback.ArchiveSha256
                    && UpdateProtocol.Read<UpdatePlan>(fallbackBytes) == withFallback,
                "existing portable fallback receipts retain their exact wire identity"
            );
        }
        var outcome = new UpdateOutcome(
            1,
            plan.OperationId,
            "Installed",
            "Portable payload promoted.",
            plan.Version,
            0,
            null,
            DateTimeOffset.UtcNow
        );
        byte[] outcomeBytes = JsonSerializer.SerializeToUtf8Bytes(outcome, UpdateProtocol.Json);
        using (var serialized = JsonDocument.Parse(outcomeBytes))
            check(
                serialized.RootElement.GetProperty("installerExitCode").GetInt32() == 0
                    && !serialized.RootElement.TryGetProperty("applyExitCode", out _)
                    && UpdateProtocol.Read<UpdateOutcome>(outcomeBytes) == outcome,
                "existing portable clients can read the unchanged completed-operation JSON"
            );
        bool setupRefused = false;
        try
        {
            UpdateProtocol.Validate(plan with { SchemaVersion = 1, Deployment = null });
        }
        catch (InvalidDataException)
        {
            setupRefused = true;
        }
        check(
            setupRefused,
            "the retired Setup update protocol is refused before any package or filesystem effect"
        );

        foreach (
            var invalid in new[]
            {
                plan with
                {
                    SchemaVersion = 1,
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
                    ArchiveSha256 = new string('A', 64),
                },
                plan with
                {
                    ArchivePath = "https://example.invalid/installer.exe",
                },
                plan with
                {
                    ApplicationDirectory = @"\\host\share",
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
                UpdatePaths.ValidateArchiveLocation(path, plan.Version, root);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            check(refused, "only the exact immutable version cache can supply a portable archive");
        }
    }
}
