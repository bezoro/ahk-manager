using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.Presentation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NormalCutoverManifest
{
    public int Schema { get; set; } = 3;
    public int CatalogSchema { get; set; } = 5;
    public string ShortcutArguments { get; set; } = "--tray --login";
    public bool Fixture { get; set; }
    public string ExpectedSid { get; set; } = "";
    public int ExpectedSession { get; set; }
    public string Root { get; set; } = "";
    public string ProfileDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public string LegacyShortcut { get; set; } = "";
    public string LegacyShortcutSHA256 { get; set; } = "";
    public string NormalShortcut { get; set; } = "";
    public string ManagerExecutable { get; set; } = "";
    public string ManagerSHA256 { get; set; } = "";
    public string CatalogSHA256 { get; set; } = "";
    public AuthorityBinding Authority { get; set; } = new();
    public List<CutoverChoice> Choices { get; set; } = [];
}

public interface INormalMigrationEffects
{
    bool Fixture { get; }
    Result Check(NormalCutoverManifest manifest, MigrationJournal? journal);
    Task<Snapshot> Observe(CancellationToken token);
    string Backup(NormalCutoverManifest manifest);
    Result VerifyBackup(NormalCutoverManifest manifest, MigrationJournal journal);
    Task<Result> Apply(string step, NormalCutoverManifest manifest, MigrationJournal journal, CancellationToken token);
}

// The same coordinator drives production and isolated fake effects. It never starts a process.
public static class NormalCutoverCoordinator
{
    public static readonly string[] Steps = ["BackupVerified", "SupervisionPaused", "LegacyShortcutArchived", "SupervisorStopped", "SupervisorFileArchived", "NormalLoginPrepared", "ChildrenAdopted", "Completed"];
    public static readonly string[] RelativePaths = ["startup.ahk", @"scripts\startup\ahk_hide-desktopicons-on-doubleclick.ahk", @"scripts\startup\ahk_kill-and-restart-unity.ahk", @"scripts\elevated\ahk_volume-to-brush-size.ahk"];
    public static string Hash(NormalCutoverManifest manifest) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, MigrationJson.Default.NormalCutoverManifest)));
    public static NormalCutoverManifest ReadManifest(string path)
    {
        NormalMigrationEffects.SafePath(path);
        if (new FileInfo(path).Length > 64000) throw new InvalidDataException("Manifesto excede limite.");
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), MigrationJson.Default.NormalCutoverManifest) ?? throw new InvalidDataException("Manifesto vazio.");
    }
    public static void ValidateManifest(NormalCutoverManifest m)
    {
        if (m.Schema != 3 || m.CatalogSchema != 5 || m.ShortcutArguments != "--tray --login" || m.ExpectedSid.Length is 0 or > 200 || m.ExpectedSession < 0 || !AuthorityBinding.Valid(m.Authority) || m.Authority.Version != 2 || m.Authority.Sid != m.ExpectedSid || m.Authority.Fixture != m.Fixture || !CatalogStore.HashValid(m.CatalogSHA256) || !CatalogStore.HashValid(m.ManagerSHA256) || !CatalogStore.HashValid(m.LegacyShortcutSHA256) || m.Choices.Count != 4) throw new InvalidDataException("Manifesto normal incompleto.");
        var modes = new[] { ManagementMode.Retired, ManagementMode.Normal, ManagementMode.Normal, ManagementMode.ExternalPending };
        for (int i = 0; i < 4; i++)
            if (m.Choices[i].RelativePath != RelativePaths[i] || !CatalogStore.HashValid(m.Choices[i].ExpectedSHA256) || m.Choices[i].Mode != modes[i] || i == 0 && m.Choices[i].RequestedEnabled) throw new InvalidDataException("Escolhas fora do escopo exato.");
        foreach (var p in new[] { m.Root, m.ProfileDirectory, m.BackupDirectory, m.LegacyShortcut, m.NormalShortcut, m.ManagerExecutable }) NormalMigrationEffects.SafePath(p);
        if (Paths.Equal(m.LegacyShortcut, m.NormalShortcut) || !m.LegacyShortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !m.NormalShortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Destinos de atalho inválidos.");
        if (!m.Fixture) NormalMigrationEffects.ValidateScope(m);
    }
    public static Catalog Merge(Catalog original, NormalCutoverManifest m)
    {
        var next = original.Copy(); next.Schema = 5; next.Authority = m.Authority.Copy();
        for (int i = 0; i < 4; i++)
        {
            var path = Path.Combine(m.Root, RelativePaths[i]);
            var entry = next.Scripts.SingleOrDefault(e => Paths.Equal(e.Path, path)) ?? throw new InvalidDataException("Registro selecionado ausente.");
            if (i is 1 or 2)
            {
                if (entry.RunAsAdministrator || entry.ManagementMode is not (ManagementMode.Unmanaged or ManagementMode.Normal)) throw new InvalidDataException("Preferência normal conflitante.");
                entry.StartWithWindows = entry.StartWithWindows ?? m.Choices[i].RequestedEnabled;
            }
            if (i == 0) { if (entry.RunAsAdministrator) throw new InvalidDataException("Supervisor privilegiado recusado."); entry.StartWithWindows = false; entry.LaunchBlocked = true; }
            if (i == 3 && entry.StartWithWindows is null) entry.StartWithWindows = false;
            entry.ManagementMode = m.Choices[i].Mode;
        }
        return next;
    }
    static MigrationJournal? ReadJournal(string directory)
    {
        var path = JournalStore.PathFor(directory); if (!File.Exists(path)) return null;
        NormalMigrationEffects.SafePath(path);
        if (new FileInfo(path).Length > 64000) throw new InvalidDataException("Journal excede limite.");
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJson.Default.MigrationJournal) ?? throw new InvalidDataException("Journal vazio.");
    }
    static Result SnapshotGate(Snapshot snapshot, NormalCutoverManifest m, MigrationJournal? j)
    {
        if (snapshot.Unknown) return Result.Fail("Snapshot parcial ou AutoHotkey desconhecido.");
        var supervisors = snapshot.For(Path.Combine(m.Root, RelativePaths[0]));
        bool stopped = j?.AppliedSteps.Contains("SupervisorStopped") == true;
        if (supervisors.Count > 1 || stopped && supervisors.Count != 0 || supervisors.Any(p => p.Created <= 0 || p.Pid <= 0 || !Commands.IsInterpreter(p.Executable))) return Result.Fail("Supervisor duplicado, reaparecido ou sem identidade.");
        if (j is not null && !stopped && supervisors.Count == 1 && !ProcessIdentity.SameCore(supervisors[0], j.Supervisor)) return Result.Fail("Identidade do supervisor mudou.");
        for (int i = 1; i <= 2; i++)
        {
            var children = snapshot.For(Path.Combine(m.Root, RelativePaths[i]));
            if (children.Count != 1 || children[0].Pid <= 0 || children[0].Created <= 0 || !Commands.IsInterpreter(children[0].Executable) || j is not null && !j.Children.Any(c => ProcessIdentity.SameCore(c, children[0]))) return Result.Fail("Filho ausente, duplicado ou com identidade alterada.");
        }
        return Result.Ok("Identidades verificadas.");
    }
    public static async Task<Result> Execute(CatalogStore store, NormalCutoverManifest m, IEffectOwner owner, RequestLedger ledger, IClock clock, INormalMigrationEffects effects, bool resume, bool preflightOnly, CancellationToken token)
    {
        try
        {
            ValidateManifest(m);
            var catalog = store.Load(); var hash = Hash(m); var journal = ReadJournal(store.DirectoryPath);
            if (owner.Fixture != m.Fixture || effects.Fixture != m.Fixture || ledger.Fixture != m.Fixture || !owner.Held || !owner.Trusted || owner.LoginKey.Length == 0 || owner.Sid != m.ExpectedSid || !Paths.Equal(store.DirectoryPath, m.ProfileDirectory)) return Result.Fail("Owner/SID/modo/perfil diverge.");
            if (store.ReadOnly || !store.PrimaryValid || !store.VerifyLoadedPrimary() || !ledger.Verify(m.Authority).Success || ledger.HasPending || catalog.Authority is not null && !AuthorityBinding.Same(catalog.Authority, m.Authority)) return Result.Fail("Primário ou autoridade indisponível, alterado ou com pedido pendente.");
            if (journal is not null)
            {
                if (!resume || journal.Schema != 3 || journal.LoginKey != owner.LoginKey || journal.Fixture != m.Fixture || journal.ManifestHash != hash || journal.OwnerSid != owner.Sid || journal.BackupDirectory != m.BackupDirectory || journal.AppliedSteps.Count > Steps.Length || !journal.AppliedSteps.SequenceEqual(Steps.Take(journal.AppliedSteps.Count)) || journal.PendingStep != "" && journal.PendingStep != Steps.ElementAtOrDefault(journal.AppliedSteps.Count) || journal.Children.Count != 2) return Result.Fail("Journal existente exige recuperação explícita da mesma transação.");
                if (journal.CatalogDigest != store.LoadedFingerprint && !(journal.AppliedSteps.Count == 1 && store.LoadedFingerprint == m.CatalogSHA256) && !JournalStore.Compatible(store.DirectoryPath, catalog.Migration, m.Fixture)) return Result.Fail("Catálogo mudou após a transação; metadados preservados, recuperação recusada.");
                var backup = effects.VerifyBackup(m, journal); if (!backup.Success) return backup;
            }
            else if (resume || store.LoadedFingerprint != m.CatalogSHA256 || catalog.Migration.State is MigrationState.Incomplete or MigrationState.Completed) return Result.Fail("Primário/estado não corresponde ao início revisado.");
            var merged = Merge(catalog, m);
            if (journal is null || journal.AppliedSteps.Count == 1)
            {
                merged.Runtime = new() { LoginKey = owner.LoginKey, Decided = true };
                foreach (var e in merged.Scripts.Where(e => e.ManagementMode == ManagementMode.Normal && (Paths.Equal(e.Path, Path.Combine(m.Root, RelativePaths[1])) || Paths.Equal(e.Path, Path.Combine(m.Root, RelativePaths[2]))))) merged.Runtime.Set(e.Id, true);
            }
            var transactionFingerprint = store.LoadedFingerprint;
            Result CommitGate(MigrationJournal? currentJournal)
            {
                token.ThrowIfCancellationRequested();
                var files = effects.Check(m, currentJournal); if (!files.Success) return files;
                bool Owns() => owner.Held && owner.Trusted && owner.LoginKey.Length > 0 && owner.Sid == m.ExpectedSid && owner.Fixture == m.Fixture && effects.Fixture == m.Fixture && ledger.Fixture == m.Fixture;
                if (!Owns() || store.LoadedFingerprint != transactionFingerprint || !store.VerifyLoadedPrimary()) return Result.Fail("Owner ou primário mudou antes do commit/efeito; bytes preservados.");
                var authority = ledger.Verify(m.Authority); if (!authority.Success || ledger.HasPending) return Result.Fail("Autoridade mudou antes do commit/efeito.");
                token.ThrowIfCancellationRequested();
                return Owns() && store.LoadedFingerprint == transactionFingerprint && store.VerifyLoadedPrimary() ? Result.Ok("Commit atual verificado.") : Result.Fail("Owner/primário mudou durante o gate; bytes preservados.");
            }
            var gate = effects.Check(m, journal); if (!gate.Success) return gate;
            var snapshot = await effects.Observe(token); gate = SnapshotGate(snapshot, m, journal); if (!gate.Success) return gate;
            gate = CommitGate(journal); if (!gate.Success) return gate;
            if (preflightOnly) return Result.Ok("Preflight normal verificado; nenhum efeito aplicado.");
            if (journal?.State == MigrationState.Completed && catalog.Migration.State == MigrationState.Completed && JournalStore.Compatible(store.DirectoryPath, catalog.Migration, m.Fixture)) return Result.Ok("Mesma transação já concluída; nenhum efeito repetido.");
            token.ThrowIfCancellationRequested();
            if (journal is null)
            {
                journal = new() { Schema = 3, LoginKey = owner.LoginKey, Fixture = m.Fixture, OwnerSid = owner.Sid, ManifestHash = hash, State = MigrationState.Incomplete, BackupDirectory = m.BackupDirectory, Supervisor = snapshot.For(Path.Combine(m.Root, RelativePaths[0])).SingleOrDefault(), Children = [snapshot.For(Path.Combine(m.Root, RelativePaths[1])).Single(), snapshot.For(Path.Combine(m.Root, RelativePaths[2])).Single()] };
                journal.BackupDigest = effects.Backup(m); journal.AppliedSteps.Add(Steps[0]);
                gate = CommitGate(null); if (!gate.Success) return gate;
                merged.Migration = new() { State = MigrationState.Incomplete, ManifestHash = hash, OwnerSid = owner.Sid };
                journal.CatalogDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(merged, AppJson.Default.Catalog)));
                journal.PendingStep = Steps[1]; JournalStore.Save(store.DirectoryPath, journal);
            }
            if (journal.AppliedSteps.Count == 1)
            {
                gate = CommitGate(journal); if (!gate.Success) return gate;
                merged.Migration = new() { State = MigrationState.Incomplete, ManifestHash = hash, OwnerSid = owner.Sid };
                store.Save(merged); transactionFingerprint = store.LoadedFingerprint;
                gate = CommitGate(journal); if (!gate.Success) return gate;
                journal.AppliedSteps.Add(Steps[1]); journal.PendingStep = ""; JournalStore.Save(store.DirectoryPath, journal);
            }
            for (int i = journal.AppliedSteps.Count; i < Steps.Length - 1; i++)
            {
                token.ThrowIfCancellationRequested();
                gate = CommitGate(journal); if (!gate.Success) return gate;
                gate = SnapshotGate(await effects.Observe(token), m, journal); if (!gate.Success) return gate;
                gate = CommitGate(journal); if (!gate.Success) return gate;
                journal.PendingStep = Steps[i]; JournalStore.Save(store.DirectoryPath, journal);
                gate = CommitGate(journal); if (!gate.Success) return gate;
                var result = await effects.Apply(Steps[i], m, journal, token); if (!result.Success) return Result.Fail("Transação incompleta/desarmada: " + result.Message);
                gate = CommitGate(journal); if (!gate.Success) return gate;
                journal.AppliedSteps.Add(Steps[i]); journal.PendingStep = ""; JournalStore.Save(store.DirectoryPath, journal);
            }
            gate = effects.Check(m, journal); if (!gate.Success) return gate;
            gate = SnapshotGate(await effects.Observe(token), m, journal); if (!gate.Success) return gate;
            gate = CommitGate(journal); if (!gate.Success) return gate;
            if (journal.State != MigrationState.Completed) { journal.AppliedSteps.Add("Completed"); journal.State = MigrationState.Completed; journal.PendingStep = ""; }
            var digest = JournalStore.Save(store.DirectoryPath, journal);
            gate = CommitGate(journal); if (!gate.Success) return gate;
            // Keep the transaction's pinned metadata; never rebase over a late external edit.
            var completed = merged.Copy(); completed.Migration = new() { State = MigrationState.Completed, ManifestHash = hash, OwnerSid = owner.Sid, JournalDigest = digest, CompletedUtcTicks = clock.UtcNow.Ticks };
            store.Save(completed);
            if (!store.VerifyPrimary(completed) || !JournalStore.Compatible(store.DirectoryPath, completed.Migration, m.Fixture)) return Result.Fail("Conclusão não verificada; nenhum timer foi iniciado.");
            return Result.Ok("Cutover normal concluído. Filhos adotados sem efeito. Backup: " + m.BackupDirectory + "; journal SHA256: " + digest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException or JsonException or OperationCanceledException)
        { return Result.Fail("Cutover recusado ou incompleto; supervisão desarmada, evidências preservadas. " + ex.Message); }
    }
}
