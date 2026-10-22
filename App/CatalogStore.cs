using System.Text.Json;

namespace Bezoro.AhkManager;

public sealed class CatalogStore : IDisposable
{
    readonly string directory;
    readonly FileStream writer;
    string loadedFingerprint = "";
    public string FilePath => System.IO.Path.Combine(directory, "catalog.json");
    public string DirectoryPath => directory;
    public bool ReadOnly { get; private set; }
    public bool PrimaryValid { get; private set; }
    public bool LastWriteFailed { get; private set; }
    public int LoadedSchema { get; private set; }
    public string LoadedFingerprint => loadedFingerprint;
    public string Notice { get; private set; } = "";
    public CatalogStore(string directory)
    {
        this.directory = System.IO.Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        try { writer = new FileStream(System.IO.Path.Combine(this.directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("Outro gerenciador já usa este catálogo, ou a pasta não permite escrita."); }
    }
    static Catalog Read(string path) => Read(path, out _);
    static Catalog Read(string path, out string fingerprint)
    {
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Catálogo excede limite.");
        var bytes = File.ReadAllBytes(path);
        fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version is not (1 or 2 or 3 or 4 or 5)) throw new InvalidDataException("Versão explícita do catálogo ausente ou inválida.");
        if (version < 3 && root.TryGetProperty("Scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Array && scripts.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("RunAsAdministrator", out _))) throw new InvalidDataException("Preferência de administrador não pertence ao schema antigo.");
        if (version >= 3 && root.TryGetProperty("Scripts", out var currentScripts) && currentScripts.ValueKind == JsonValueKind.Array && currentScripts.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object || e.EnumerateObject().Count(p => p.NameEquals("RunAsAdministrator")) != 1 || !e.TryGetProperty("RunAsAdministrator", out var preference) || preference.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) throw new InvalidDataException("Schema 3/4 exige preferência booleana explícita para cada script.");
        if (version < 4 && root.TryGetProperty("Authority", out _)) throw new InvalidDataException("Vínculo de autoridade não pertence ao schema antigo.");
        if (version >= 4 && (root.EnumerateObject().Count(p => p.NameEquals("Authority")) != 1 || !root.TryGetProperty("Authority", out var authority) || authority.ValueKind != JsonValueKind.Object || new[] { "LedgerId", "Version", "Sid", "Fixture" }.Any(name => authority.EnumerateObject().Count(p => p.NameEquals(name)) != 1))) throw new InvalidDataException("Schema 4 exige vínculo explícito e único.");
        if (root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) throw new InvalidDataException("Campo duplicado no catálogo.");
        if (!root.TryGetProperty("Scripts", out var records) || records.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Lista de scripts explícita necessária.");
        if (records.ValueKind == JsonValueKind.Array)
            foreach (var record in records.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object || record.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) throw new InvalidDataException("Campo de script duplicado.");
                if (version == 5 && (!record.TryGetProperty("StartWithWindows", out var flag) || flag.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False) || record.TryGetProperty("RequestedEnabled", out _))) throw new InvalidDataException("Schema 5 exige uma única preferência de próximo login.");
            }
        if (version == 5 && !root.TryGetProperty("Runtime", out _)) throw new InvalidDataException("Decisão de login explícita necessária.");
        if (version == 5)
        {
            var runtime = root.GetProperty("Runtime");
            if (runtime.ValueKind != JsonValueKind.Object || runtime.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1) || new[] { "LoginKey", "Decided", "AutomaticPaused", "Intents" }.Any(n => !runtime.TryGetProperty(n, out _))) throw new InvalidDataException("Estado de login explícito e único necessário.");
            var intents = runtime.GetProperty("Intents");
            if (intents.ValueKind != JsonValueKind.Array || intents.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.Object || i.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1) || !i.TryGetProperty("ScriptId", out _) || !i.TryGetProperty("DesiredRunning", out var desired) || desired.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) throw new InvalidDataException("Intenção de login explícita inválida.");
        }
        var c = (version < 5 ? JsonSerializer.Deserialize(bytes, AppJson.Default.LegacyCatalog)?.Convert() : JsonSerializer.Deserialize(bytes, AppJson.Default.Catalog)) ?? throw new InvalidDataException("Catálogo vazio.");
        Validate(c); return c;
    }
    public static void Validate(Catalog c)
    {
        if (c.Schema is not (1 or 2 or 3 or 4 or 5) || c.Scripts is null || c.Scripts.Count > 2000) throw new InvalidDataException("Schema inválido.");
        if (c.Schema >= 4 && !AuthorityBinding.Valid(c.Authority) || c.Schema < 4 && c.Authority is not null) throw new InvalidDataException("Vínculo do catálogo inválido.");
        HashSet<Guid> ids = []; HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var e in c.Scripts)
        {
            if (e is null || e.Id == Guid.Empty || !ids.Add(e.Id) || !paths.Add(Paths.Lexical(e.Path)) || e.Description is null || e.Description.Length > 4000 || e.DescriptionSource is not ("Informada" or "Inferida") || e.Interpreter is null || e.Interpreter.Length > 1000) throw new InvalidDataException("Registro inválido ou duplicado.");
            e.Path = Paths.Lexical(e.Path);
            if (c.Schema < 3 && e.RunAsAdministrator) throw new InvalidDataException("Preferência de administrador exige schema 3.");
            if (c.Schema >= 2 && e.ManagementMode is not (ManagementMode.Unmanaged or ManagementMode.Normal or ManagementMode.ExternalPending or ManagementMode.Retired)) throw new InvalidDataException("Modo de gerenciamento inválido.");
            if (c.Schema >= 2 && e.ManagementMode == ManagementMode.Retired && e.RequestedEnabled == true) throw new InvalidDataException("Supervisor aposentado não pode ser ativado.");
        }
        if (c.Schema == 5)
        {
            var r = c.Runtime;
            if (r is null || r.LoginKey is null || r.LoginKey.Length > 200 || r.Intents is null || r.Intents.Count > 2000 || r.Decided && string.IsNullOrWhiteSpace(r.LoginKey) || !r.Decided && (r.AutomaticPaused || r.Intents.Count != 0)) throw new InvalidDataException("Estado de login inválido.");
            HashSet<Guid> runtimeIds = [];
            foreach (var intent in r.Intents) if (intent is null || !ids.Contains(intent.ScriptId) || !runtimeIds.Add(intent.ScriptId)) throw new InvalidDataException("Intenção de sessão inválida.");
        }
        if (c.Migration is null || c.Recovery is null || c.Recovery.Count > 2000 || c.Migration.State is not (MigrationState.NotPrepared or MigrationState.Prepared or MigrationState.Incomplete or MigrationState.Completed)) throw new InvalidDataException("Estado de migração inválido.");
        if (c.Migration.OwnerSid is null || c.Migration.OwnerSid.Length > 200 || c.Migration.ManifestHash is null || c.Migration.JournalDigest is null) throw new InvalidDataException("Identidade de migração inválida.");
        if (c.Migration.State != MigrationState.NotPrepared && !HashValid(c.Migration.ManifestHash)) throw new InvalidDataException("Manifesto inválido.");
        if (c.Migration.State == MigrationState.Completed && (!HashValid(c.Migration.JournalDigest) || c.Migration.OwnerSid.Length == 0 || c.Migration.CompletedUtcTicks <= 0)) throw new InvalidDataException("Conclusão não durável.");
        HashSet<Guid> recoveryIds = [];
        foreach (var record in c.Recovery)
            if (record is null || !ids.Contains(record.ScriptId) || !recoveryIds.Add(record.ScriptId) || record.LoginKey is null || record.LoginKey.Length > 200 || record.AttemptsUtcTicks is null || record.AttemptsUtcTicks.Count > 3 || record.AttemptsUtcTicks.Any(t => t <= 0 || t > DateTime.MaxValue.Ticks) || record.Failures is < 0 or > 3 || record.NextTryUtcTicks < 0 || record.NextTryUtcTicks > DateTime.MaxValue.Ticks) throw new InvalidDataException("Limite de recuperação inválido.");
    }
    public static bool HashValid(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    public static Catalog ConvertLegacy(Catalog legacy)
    {
        Validate(legacy);
        if (legacy.Schema == 5) return legacy;
        var current = legacy.Copy(); current.Schema = 5; current.Runtime = new(); return current;
    }
    public bool VerifyLoadedPrimary()
    {
        if (ReadOnly || LastWriteFailed || !PrimaryValid || LoadedSchema is not (1 or 2 or 3 or 4 or 5) || loadedFingerprint.Length == 0) return false;
        try
        {
            _ = Read(FilePath, out var currentFingerprint);
            PrimaryValid = currentFingerprint == loadedFingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { PrimaryValid = false; }
        return PrimaryValid;
    }
    public bool VerifyPrimary(Catalog expected)
    {
        if (ReadOnly || LoadedSchema != 5 || LastWriteFailed || expected.Authority?.Version != 2) return false;
        try
        {
            var current = Read(FilePath, out var currentFingerprint);
            PrimaryValid = currentFingerprint == loadedFingerprint && current.Schema == 5 && current.Authority?.Version == 2 && JsonSerializer.SerializeToUtf8Bytes(current, AppJson.Default.Catalog).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, AppJson.Default.Catalog));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { PrimaryValid = false; }
        return PrimaryValid;
    }
    public Catalog Load()
    {
        PrimaryValid = false; LoadedSchema = 0; loadedFingerprint = "";
        if (!File.Exists(FilePath)) return new();
        try
        {
            var loaded = Read(FilePath, out var fingerprint); loadedFingerprint = fingerprint; LoadedSchema = loaded.Schema; PrimaryValid = true; ReadOnly = false; LastWriteFailed = false;
            Notice = loaded.Schema < 5 ? "Catálogo antigo convertido para consulta. Nenhum script foi ativado automaticamente; migração explícita necessária." : "";
            return ConvertLegacy(loaded);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            ReadOnly = true;
            Notice = "Catálogo inválido ou inacessível. Arquivos preservados; gravação bloqueada.";
            try { var backup = Read(FilePath + ".bak"); Notice += " Backup válido carregado para consulta."; return ConvertLegacy(backup); }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { return new(); }
        }
    }
    public void Save(Catalog c)
    {
        if (ReadOnly) throw new IOException(Notice);
        if (c.Schema != 5 || c.Authority?.Version != 2) throw new InvalidDataException("Converta o schema antigo explicitamente antes de salvar.");
        Validate(c);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(c, AppJson.Default.Catalog);
        if (bytes.Length > 2_000_000) throw new InvalidDataException("Catálogo excede limite.");
        var temporary = FilePath + ".tmp";
        bool temporaryOwned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { temporaryOwned = true; stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
            loadedFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            PrimaryValid = true; LoadedSchema = 5; LastWriteFailed = false;
        }
        catch { LastWriteFailed = true; throw; }
        finally { if (temporaryOwned && File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Log(string code, bool success)
    {
        try
        {
            var log = System.IO.Path.Combine(directory, "operations.log");
            if (File.Exists(log) && new FileInfo(log).Length > 128_000) File.Move(log, log + ".1", true);
            File.AppendAllText(log, $"{DateTime.UtcNow:O} {code} {(success ? "OK" : "REFUSED")}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Logging must never authorize or prevent an action. */ }
    }
    public void Dispose() => writer.Dispose();
}
