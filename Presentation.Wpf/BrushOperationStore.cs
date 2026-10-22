using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.Presentation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BrushJournal
{
    public int Version { get; set; } = 1;
    public bool Fixture { get; set; }
    public Guid Nonce { get; set; }
    public BrushOperation Operation { get; set; }
    public bool? RequestedLogon { get; set; }
    public string Sid { get; set; } = "";
    public string LoginKey { get; set; } = "";
    public int ManagerPid { get; set; }
    public long ManagerCreated { get; set; }
    public int Session { get; set; }
    public string Executable { get; set; } = "";
    public string ExecutableHash { get; set; } = "";
    public string AssemblyHash { get; set; } = "";
    public bool AllowedLogon { get; set; } = true;
    public string CatalogHash { get; set; } = "";
    public string LedgerHash { get; set; } = "";
    public string AnchorHash { get; set; } = "";
    public Guid AuthorityId { get; set; }
    public string Definition { get; set; } = "";
    public string Security { get; set; } = "";
    public string Stage { get; set; } = "Prepared";
    public bool Completed { get; set; }
    public string Outcome { get; set; } = "";
    public long? RunAuthorizedFileTime { get; set; }
    public long? RunBeganFileTime { get; set; }
    public bool? RequestAccepted { get; set; }
    public int? ConfirmedPid { get; set; }
    public long? ConfirmedCreated { get; set; }
    public long DeadlineUtcTicks { get; set; }
}
[JsonSerializable(typeof(BrushJournal))]
[JsonSerializable(typeof(BrushReply))]
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class BrushJson : JsonSerializerContext { }

public sealed class BrushOperationStore
{
    public string FilePath { get; }
    public bool Blocked { get; private set; }
    BrushJournal? committed;
    static BrushJournal Copy(BrushJournal j) => JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(j, BrushJson.Default.BrushJournal), BrushJson.Default.BrushJournal)!;
    public BrushJournal? Current => committed is null ? null : Copy(committed);
    public string CommittedHash => fingerprint ?? "";
    public bool Pending => Blocked || Current is { Completed: false };
    public BrushOperationStore(string directory)
    {
        FilePath = Path.Combine(directory, "brush-operation.json");
        try
        {
            Guard();
            if (File.Exists(FilePath + ".bak")) _ = Read(FilePath + ".bak");
            if (File.Exists(FilePath + ".tmp")) throw new IOException("Escrita Brush anterior nao confirmada.");
            if (File.Exists(FilePath)) { fingerprint = NormalMigrationEffects.FileHash(FilePath); committed = Read(FilePath); }
            else if (File.Exists(FilePath + ".bak")) throw new IOException("Journal Brush principal ausente com backup existente.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { Blocked = true; }
    }
    void Guard()
    {
        NormalMigrationEffects.SafePath(FilePath);
        foreach (var path in new[] { FilePath, FilePath + ".bak", FilePath + ".tmp" })
            if (File.Exists(path)) _ = NormalMigrationEffects.FileHash(path);
    }
    public static BrushJournal Read(string path)
    {
        _ = NormalMigrationEffects.FileHash(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length is <= 0 or > 65536) throw new InvalidDataException("Journal Brush fora do limite.");
        using var document = JsonDocument.Parse(f); Unique(document.RootElement);
        var j = JsonSerializer.Deserialize(document.RootElement, BrushJson.Default.BrushJournal) ?? throw new InvalidDataException("Journal Brush ausente.");
        if (new[] { j.Sid, j.LoginKey, j.Executable, j.ExecutableHash, j.AssemblyHash, j.CatalogHash, j.LedgerHash, j.AnchorHash, j.Definition, j.Security, j.Stage, j.Outcome }.Any(value => value is null)) throw new InvalidDataException("Campo textual nulo no journal Brush.");
        if (j.Version is not (1 or 2) || j.Nonce == Guid.Empty || !Enum.IsDefined(j.Operation) || j.ManagerPid <= 0 || j.ManagerCreated <= 0 || j.Session <= 0 || j.LoginKey.Length is < 3 or > 256 || j.Sid.Length is < 3 or > 256 || j.AuthorityId == Guid.Empty || j.Stage is not ("Prepared" or "Run" or "Stop" or "Register" or "Completed") || j.Operation == BrushOperation.SetLogonEnabled && j.RequestedLogon is null) throw new InvalidDataException("Journal Brush invalido.");
        foreach (var h in new[] { j.CatalogHash, j.LedgerHash, j.AnchorHash, j.ExecutableHash, j.AssemblyHash }) if (h.Length != 64 || !h.All(Uri.IsHexDigit)) throw new InvalidDataException("Vinculo Brush invalido.");
        if (j.Definition.Length > 32768 || j.Security.Length > 8192 || j.Outcome.Length > 2048 || j.Executable.Length > 4096) throw new InvalidDataException("Journal Brush excede limite.");
        ValidateRunEvidence(j); return j;
    }
    public static void ValidateRunEvidence(BrushJournal j)
    {
        bool any = j.RunAuthorizedFileTime is not null || j.RunBeganFileTime is not null || j.RequestAccepted is not null || j.ConfirmedPid is not null || j.ConfirmedCreated is not null;
        if (j.Version == 1 && any) throw new InvalidDataException("Journal legado nao recebe prova inventada.");
        if (!any) return;
        long upper;
        try { upper = new DateTime(j.DeadlineUtcTicks, DateTimeKind.Utc).ToFileTimeUtc(); } catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Limite temporal Brush invalido."); }
        if (j.Operation is not (BrushOperation.Start or BrushOperation.Restart) || j.RunAuthorizedFileTime is not > 0 || j.RunAuthorizedFileTime > upper || j.RunBeganFileTime is <= 0 || j.RunBeganFileTime < j.RunAuthorizedFileTime || j.RunBeganFileTime > upper || j.RequestAccepted == true && j.RunBeganFileTime is null || (j.ConfirmedPid is null) != (j.ConfirmedCreated is null) || j.ConfirmedPid is <= 0 || j.ConfirmedCreated is <= 0 || j.ConfirmedCreated < j.RunBeganFileTime || j.ConfirmedCreated > upper || j.ConfirmedPid is not null && j.RequestAccepted != true) throw new InvalidDataException("Prova temporal/identidade Brush invalida.");
    }
    public static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { if (e.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) throw new InvalidDataException("Campo duplicado no vinculo Brush."); foreach (var p in e.EnumerateObject()) Unique(p.Value); }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Unique(item);
    }
    public void Save(BrushJournal j)
    {
        bool created = false;
        try
        {
            if (Blocked) throw new IOException("Journal Brush bloqueado; nenhuma tentativa automatica.");
            Guard();
            if (committed is null && File.Exists(FilePath)) throw new IOException("Journal Brush surgiu externamente.");
            if (committed is not null && (!File.Exists(FilePath) || NormalMigrationEffects.FileHash(FilePath) != Fingerprint)) throw new IOException("Journal Brush mudou externamente.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(j, BrushJson.Default.BrushJournal);
            if (bytes.Length > 65536) throw new IOException("Journal Brush excede limite.");
            ValidateRunEvidence(j); var candidate = Copy(j);
            using (var f = new FileStream(FilePath + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { created = true; f.Write(bytes); f.Flush(true); }
            Guard();
            if (File.Exists(FilePath))
            {
                if (!File.Exists(FilePath + ".bak")) File.Copy(FilePath, FilePath + ".bak", false);
                File.Replace(FilePath + ".tmp", FilePath, null);
            }
            else File.Move(FilePath + ".tmp", FilePath);
            created = false; Fingerprint = NormalMigrationEffects.FileHash(FilePath); committed = candidate;
        }
        catch { Blocked = true; throw; }
        finally { if (created && File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); }
    }
    string Fingerprint { get => fingerprint ??= committed is null ? "" : NormalMigrationEffects.FileHash(FilePath); set => fingerprint = value; }
    string? fingerprint;
}
