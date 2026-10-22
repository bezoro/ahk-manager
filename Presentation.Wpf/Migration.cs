using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.Presentation;

public static class JournalStore
{
    public static readonly string[] Steps = ["BackupVerified", "SupervisionPaused", "LegacyShortcutArchived", "SupervisorStopped", "ChildrenAdopted", "NormalLoginPrepared", "Completed"];
    public static string PathFor(string directory) => System.IO.Path.Combine(directory, "migration-journal.json");
    public static string Save(string directory, MigrationJournal journal)
    {
        var path = PathFor(directory); var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, AppJson.Default.MigrationJournal); var temporary = path + ".tmp";
        bool temporaryOwned = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { temporaryOwned = true; output.Write(bytes); output.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (temporaryOwned && File.Exists(temporary)) File.Delete(temporary); }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    public static bool Compatible(string directory, MigrationRecord record, bool fixture)
    {
        try
        {
            var path = PathFor(directory); if (new FileInfo(path).Length > 64000) return false;
            var bytes = File.ReadAllBytes(path); var journal = JsonSerializer.Deserialize(bytes, AppJson.Default.MigrationJournal);
            return journal is not null && journal.State == MigrationState.Completed && journal.Fixture == fixture && journal.ManifestHash == record.ManifestHash && journal.OwnerSid == record.OwnerSid && journal.AppliedSteps is not null &&
                (fixture && journal.Schema == 1 && journal.AppliedSteps.SequenceEqual(Steps) || journal.Schema == 3 && journal.LoginKey.Length > 0 && journal.PendingStep == "" && CatalogStore.HashValid(journal.BackupDigest) && journal.BackupDirectory.Length > 0 && journal.Children.Count == 2 && journal.AppliedSteps.SequenceEqual(NormalCutoverCoordinator.Steps)) &&
                Convert.ToHexString(SHA256.HashData(bytes)) == record.JournalDigest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }
}
public sealed class CutoverChoice
{
    public string RelativePath { get; set; } = "";
    public string ExpectedSHA256 { get; set; } = "";
    public bool RequestedEnabled { get; set; }
    public ManagementMode Mode { get; set; }
}
public sealed class CutoverManifest
{
    public int Schema { get; set; } = 1;
    public string Root { get; set; } = "";
    public string LegacyShortcut { get; set; } = "";
    public string LegacyShortcutSHA256 { get; set; } = "";
    public string ProposedArchive { get; set; } = "";
    public string NormalLoginExecutable { get; set; } = "";
    public string NormalLoginArguments { get; set; } = "--tray";
    public string CatalogExpectedSHA256 { get; set; } = "";
    public List<CutoverChoice> Choices { get; set; } = [];
    public List<string> Pending { get; set; } = [];
    public List<string> OrderedSteps { get; set; } = [.. JournalStore.Steps];
    public string Rollback { get; set; } = "Restaurar somente o atalho arquivado e catálogo do backup após conferir hashes; manter migração incompleta e supervisão desarmada. Não encerrar filhos implicitamente.";
}
[JsonSerializable(typeof(CutoverManifest))]
[JsonSerializable(typeof(NormalCutoverManifest))]
[JsonSerializable(typeof(NormalBackup))]
[JsonSerializable(typeof(Result))]
internal partial class MigrationJson : JsonSerializerContext { }
public interface IMigrationEffects
{
    Result Apply(string step, CutoverManifest manifest, ProcessIdentity? supervisor);
    Result Rollback(CutoverManifest manifest);
}
// No real Startup/Task adapter exists in this phase. This coordinator is fixture-only.
public static class MigrationSimulation
{
    public static string Hash(CutoverManifest manifest) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, MigrationJson.Default.CutoverManifest)));
    public static Catalog ApplyChoices(Catalog original, CutoverManifest manifest)
    {
        var next = original.Copy();
        foreach (var choice in manifest.Choices)
        {
            var path = System.IO.Path.Combine(manifest.Root, choice.RelativePath);
            var entry = next.Scripts.FirstOrDefault(e => Paths.Equal(e.Path, path));
            if (entry is null) continue;
            entry.ManagementMode = choice.Mode; entry.RequestedEnabled = choice.RequestedEnabled;
        }
        return next;
    }
    public static Result Execute(CatalogStore store, Catalog original, CutoverManifest manifest, Snapshot snapshot, IEffectOwner owner, IClock clock, IMigrationEffects effects, bool confirmed)
    {
        if (!confirmed) return Result.Fail("Cancelado antes de qualquer escrita ou efeito.");
        if (!owner.Fixture || !owner.Trusted || !owner.Held) return Result.Fail("Cutover real não implementado/autorizado nesta fase.");
        if (store.ReadOnly || !store.PrimaryValid || !store.VerifyPrimary(original) || !snapshot.Complete || snapshot.Unknown || manifest.Pending.Count != 0) return Result.Fail("Pré-condições incompletas; nenhuma etapa de cutover.");
        var supervisorPath = System.IO.Path.Combine(manifest.Root, "startup.ahk"); var active = snapshot.For(supervisorPath);
        if (active.Count > 1 || active.Any(p => p.Created == 0 || string.IsNullOrEmpty(p.Executable))) return Result.Fail("Supervisor sem identidade única confiável.");
        var journal = new MigrationJournal { Fixture = true, ManifestHash = Hash(manifest), OwnerSid = owner.Sid, State = MigrationState.Incomplete };
        var next = ApplyChoices(original, manifest); next.Migration = new() { State = MigrationState.Incomplete, ManifestHash = journal.ManifestHash, OwnerSid = owner.Sid };
        try
        {
            store.Save(next); JournalStore.Save(store.DirectoryPath, journal);
            foreach (var step in JournalStore.Steps[..^1])
            {
                var applied = effects.Apply(step, manifest, active.SingleOrDefault());
                if (!applied.Success) { effects.Rollback(manifest); return Result.Fail("Migração interrompida/desarmada: " + applied.Message); }
                journal.AppliedSteps.Add(step); JournalStore.Save(store.DirectoryPath, journal);
            }
            journal.AppliedSteps.Add("Completed"); journal.State = MigrationState.Completed;
            var digest = JournalStore.Save(store.DirectoryPath, journal);
            next.Migration = new() { State = MigrationState.Completed, ManifestHash = journal.ManifestHash, OwnerSid = owner.Sid, JournalDigest = digest, CompletedUtcTicks = clock.UtcNow.Ticks };
            store.Save(next); return Result.Ok("Conclusão e journal duráveis. Apenas efeitos fake foram aplicados.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            effects.Rollback(manifest); return Result.Fail("Gravação incompleta; supervisão desarmada. " + ex.Message);
        }
    }
    public static void WritePlan(string path, CutoverManifest manifest) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, MigrationJson.Default.CutoverManifest));
}
