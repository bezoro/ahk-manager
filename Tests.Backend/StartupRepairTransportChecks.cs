using System.Text.Json;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.Core.Models;

namespace Tests.Backend;

internal static class StartupRepairTransportChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string fingerprint = new('c', 64);
        var preview = new StartupRepairPreview(
            true,
            @"V:\Previous Manager\Bezoro.AhkManager.exe",
            @"C:\Portable Manager\Bezoro.AhkManager.exe",
            fingerprint,
            ReasonCode.None,
            "Review the previous startup entry."
        );
        var manager = new FakeManager { StartupPreview = preview };
        await using var session = new BackendSession(
            manager,
            Path.Combine(root, "startup-repair-transport"),
            Path.Combine(root, "Bezoro.AhkManager.exe"),
            () => true,
            new FakeStartup()
        );
        check(
            await Refused("previewStartupRepair", "{}")
                && await Refused(
                    "repairStartup",
                    "{\"fingerprint\":\"" + fingerprint + "\",\"confirmed\":true}"
                )
                && manager.StartupPreviewCalls == 0
                && manager.StartupRepairs.Count == 0,
            "startup repair transport requires a loaded backend session before preview or mutation"
        );
        await session.ExecuteAsync("load", Payload("{}"), CancellationToken.None);
        var serialized = await session.ExecuteAsync(
            "previewStartupRepair",
            Payload("{}"),
            CancellationToken.None
        );
        check(
            serialized.Deserialize(Protocol.Json.StartupRepairPreview) == preview
                && manager.StartupPreviewCalls == 1
                && manager.StartupRepairs.Count == 0,
            "source-generated startup preview preserves only its typed paths reason and exact review token"
        );
        check(
            await Refused("previewStartupRepair", "{\"registryPath\":\"another key\"}")
                && manager.StartupPreviewCalls == 1,
            "startup preview accepts no renderer-selected registry path"
        );
        foreach (
            string invalid in new[]
            {
                "{}",
                "{\"confirmed\":true}",
                "{\"fingerprint\":\"" + fingerprint + "\"}",
                "{\"fingerprint\":null,\"confirmed\":true}",
                "{\"fingerprint\":42,\"confirmed\":true}",
                "{\"fingerprint\":\"" + new string('c', 63) + "\",\"confirmed\":true}",
                "{\"fingerprint\":\"" + new string('C', 64) + "\",\"confirmed\":true}",
                "{\"fingerprint\":\"" + fingerprint + "\\n\",\"confirmed\":true}",
                "{\"fingerprint\":\"" + fingerprint + "\",\"confirmed\":\"yes\"}",
                "{\"fingerprint\":\""
                    + fingerprint
                    + "\",\"confirmed\":true,\"command\":\"unapproved command\"}",
            }
        )
            check(
                await Refused("repairStartup", invalid) && manager.StartupRepairs.Count == 0,
                "startup repair transport refuses malformed tokens consent types and extra payload fields before effects"
            );
        var declined = await session.ExecuteAsync(
            "repairStartup",
            Payload("{\"fingerprint\":\"" + fingerprint + "\",\"confirmed\":false}"),
            CancellationToken.None
        );
        check(
            declined.Deserialize(Protocol.Json.OperationResult)!.Code
                == ReasonCode.StartupConsentRequired
                && manager.StartupRepairs is [(var token, false)]
                && token == fingerprint
                && manager.StartupRepairEffects == 0,
            "startup repair transport preserves declined consent rather than upgrading it to authorization"
        );
        var applied = await session.ExecuteAsync(
            "repairStartup",
            Payload("{\"fingerprint\":\"" + fingerprint + "\",\"confirmed\":true}"),
            CancellationToken.None
        );
        check(
            applied.Deserialize(Protocol.Json.OperationResult)!.Succeeded
                && manager.StartupRepairs.Count == 2
                && manager.StartupRepairs[1] == (fingerprint, true)
                && manager.StartupRepairEffects == 1,
            "startup repair transport forwards exactly the reviewed token and affirmative consent once"
        );

        async Task<bool> Refused(string action, string json)
        {
            try
            {
                await session.ExecuteAsync(action, Payload(json), CancellationToken.None);
                return false;
            }
            catch (Exception error) when (error is JsonException or ProtocolException)
            {
                return true;
            }
        }
    }

    static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
