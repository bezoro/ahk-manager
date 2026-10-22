using System.IO;
using System.Text.Json.Nodes;
using Bezoro.AhkManager;

static partial class Tests
{
    static void MalformSchema(CatalogStore store, string value)
    {
        var node = JsonNode.Parse(File.ReadAllBytes(store.FilePath))!.AsObject();
        node["Schema"] = JsonNode.Parse(value);
        File.WriteAllText(store.FilePath, node.ToJsonString());
    }
    static async Task RunElevationSchemaReview()
    {
        foreach (string value in new[] { "\"3\"", "null", "true", "[]", "{}", "3.5", "999999999999999999999999" })
        {
            using var r = new FunctionalRig("non-numeric-schema-" + checks);
            r.Store.Save(r.Session.CurrentCatalog);
            MalformSchema(r.Store, value);
            var primary = File.ReadAllBytes(r.Store.FilePath); var backup = File.ReadAllBytes(r.Store.FilePath + ".bak");
            r.Reload();
            Check(r.Store.ReadOnly && !r.Store.PrimaryValid && r.Session.Entries.Count == 3, "malformed Schema consults valid backup read-only:" + value);
            Check(primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && backup.SequenceEqual(File.ReadAllBytes(r.Store.FilePath + ".bak")), "malformed Schema preserves primary and backup:" + value);
            await r.Session.Reconcile(); await r.Session.Act(ScriptAction.Start); r.Session.SetRunAsAdministrator(true);
            Check(r.Processes.Starts == 0 && r.Processes.Stops == 0 && r.Recycler.Calls == 0 && primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)), "malformed Schema authorizes zero effects or preference saves:" + value);
        }
        using (var r = new FunctionalRig("verify-after-load"))
        {
            r.Arm(); var expected = r.Session.CurrentCatalog;
            Check(r.Store.VerifyPrimary(expected) && r.Store.VerifyLoadedPrimary(), "loaded primary initially current");
            MalformSchema(r.Store, "false");
            var primary = File.ReadAllBytes(r.Store.FilePath); var backup = File.ReadAllBytes(r.Store.FilePath + ".bak");
            Check(!r.Store.VerifyPrimary(expected) && !r.Store.PrimaryValid, "VerifyPrimary handles malformed type without throwing");
            await r.Session.Reconcile();
            Check(primary.SequenceEqual(File.ReadAllBytes(r.Store.FilePath)) && backup.SequenceEqual(File.ReadAllBytes(r.Store.FilePath + ".bak")) && r.Processes.Starts == 0 && r.Processes.Stops == 0, "malformed primary after load preserves bytes and blocks automatic effects");
        }
        using (var r = new FunctionalRig("verify-loaded-after-load"))
        {
            Check(r.Store.VerifyLoadedPrimary(), "loaded fingerprint initially current");
            MalformSchema(r.Store, "[]");
            Check(!r.Store.VerifyLoadedPrimary() && !r.Store.PrimaryValid, "VerifyLoadedPrimary handles nonnumeric schema without throwing");
        }
        using (var r = new FunctionalRig("checked-disabled-stop"))
        {
            r.Arm(false); r.Session.SetRunAsAdministrator(true); await r.Session.Reconcile();
            Check(r.Processes.Stops == 1 && r.Processes.Starts == 0 && r.Session.CurrentCatalog.Recovery.Count == 0, "checked Disabled retains normal verified Stop without launch or budget");
        }
    }
}
