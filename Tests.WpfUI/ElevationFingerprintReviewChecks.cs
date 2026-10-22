using System.IO;
using System.Text.Json.Nodes;

static partial class Tests
{
    static async Task RunElevationFingerprintReview()
    {
        foreach (string change in new[] { "whitespace", "property-order" })
        {
            using var r = new FunctionalRig("fingerprint-" + change); r.Arm();
            var expected = r.Session.CurrentCatalog;
            Check(r.Store.VerifyPrimary(expected), "initial raw and semantic primary match:" + change);
            if (change == "whitespace") File.AppendAllText(r.Store.FilePath, " \r\n");
            else
            {
                var original = JsonNode.Parse(File.ReadAllBytes(r.Store.FilePath))!.AsObject();
                var reordered = new JsonObject(original.Reverse().Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())));
                File.WriteAllText(r.Store.FilePath, reordered.ToJsonString());
            }
            var primary = File.ReadAllBytes(r.Store.FilePath); var backup = File.ReadAllBytes(r.Store.FilePath + ".bak");
            Check(!r.Store.VerifyPrimary(expected) && !r.Store.PrimaryValid, "same semantics external raw change refuses current primary:" + change);
            int probes = r.Processes.SnapshotCalls;
            await r.Session.Reconcile(); await r.Session.Act(Bezoro.AhkManager.ScriptAction.Start); r.Session.SetRunAsAdministrator(true);
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Processes.SnapshotCalls == probes && r.Recycler.Calls == 0, "raw mismatch authorizes zero automatic/manual/preference effects:" + change);
            Check(primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && backup.SequenceEqual(File.ReadAllBytes(r.Store.FilePath + ".bak")), "raw mismatch preserves primary and backup without overwrite:" + change);
        }
        using (var r = new FunctionalRig("legitimate-save-fingerprint"))
        {
            var original = File.ReadAllBytes(r.Store.FilePath); var next = r.Session.CurrentCatalog;
            next.Scripts[0].Description = "Legitimate metadata update"; r.Store.Save(next);
            Check(!original.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), "legitimate Save changes raw bytes");
            Check(r.Store.VerifyPrimary(next) && r.Store.VerifyLoadedPrimary() && r.Store.PrimaryValid, "legitimate Save refreshes raw fingerprint and semantic verification");
        }
    }
}
