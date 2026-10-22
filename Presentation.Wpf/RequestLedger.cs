using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.Presentation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LaunchRequest
{
    public Guid Nonce { get; set; }
    public string ScriptPath { get; set; } = "";
    public string InterpreterPath { get; set; } = "";
    public string Sid { get; set; } = "";
    public int ManagerPid { get; set; }
    public long ManagerCreated { get; set; }
    public string LoginKey { get; set; } = "";
    public int SessionId { get; set; }
    public long RequestedUtcTicks { get; set; }
    public LaunchCandidate? Candidate { get; set; }
    public RequestContext Context { get; set; } = new();
    public LaunchRequest Copy() => new() { Nonce = Nonce, ScriptPath = ScriptPath, InterpreterPath = InterpreterPath, Sid = Sid, ManagerPid = ManagerPid, ManagerCreated = ManagerCreated, LoginKey = LoginKey, SessionId = SessionId, RequestedUtcTicks = RequestedUtcTicks, Candidate = Candidate, Context = Context.Copy() };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LedgerDocument
{
    public int Schema { get; set; } = 2;
    public AuthorityBinding Binding { get; set; } = new();
    public List<LaunchRequest> Requests { get; set; } = [];
    public LedgerDocument Copy() => new() { Schema = Schema, Binding = Binding.Copy(), Requests = Requests.Select(r => r.Copy()).ToList() };
}
[JsonSerializable(typeof(LedgerDocument))]
[JsonSerializable(typeof(AuthorityBinding))]
[JsonSerializable(typeof(LaunchRequest))]
internal partial class LedgerJson : JsonSerializerContext { }

// Fixed real namespace is chosen here, never from --data-dir. Fixture namespace is explicit.
public sealed class RequestLedger
{
    readonly IEffectOwner owner;
    readonly int managerPid;
    readonly long managerCreated;
    readonly int sessionId;
    readonly bool fixture;
    string fingerprint = "";
    string anchorFingerprint = "";
    LedgerDocument? document;
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "launch-requests.json");
    public string AnchorPath => Path.Combine(DirectoryPath, "launch-authority.anchor.json");
    public bool Ready { get; private set; }
    public bool ReadOnly { get; private set; }
    public bool NeedsUpgrade => Ready && document?.Schema == 1;
    public string Fingerprint => fingerprint;
    public string Notice { get; private set; } = "";
    public bool Fixture => fixture;
    public AuthorityBinding? Binding => Ready ? document?.Binding.Copy() : null;
    public IReadOnlyList<LaunchRequest> Requests => document?.Requests.Select(r => r.Copy()).ToList() ?? [];
    public bool HasPending => document?.Requests.Count > 0;
    public string PendingReason => HasPending ? "Pedido registrado para " + document!.Requests[0].ScriptPath + "; execução não confirmada. Novos inícios e reinícios ficam bloqueados." : Notice;
    RequestLedger(string directory, IEffectOwner owner, bool fixture, IEnumerable<string> profileFiles)
    {
        DirectoryPath = Path.GetFullPath(directory); this.owner = owner; this.fixture = fixture;
        if (fixture) { managerPid = 7100; managerCreated = new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(); sessionId = 1; }
        else { using var manager = Process.GetCurrentProcess(); managerPid = manager.Id; managerCreated = manager.StartTime.ToUniversalTime().ToFileTimeUtc(); sessionId = manager.SessionId; }
        try
        {
            SafeDirectory();
            bool history = File.Exists(AnchorPath) || File.Exists(FilePath) || File.Exists(FilePath + ".bak") || File.Exists(AnchorPath + ".bak") || File.Exists(FilePath + ".tmp") || File.Exists(Path.Combine(DirectoryPath, "authority.write.lock")) || profileFiles.Distinct(StringComparer.OrdinalIgnoreCase).Any(ProfileRequiresAuthority);
            if (!history)
            {
                if (!Owns()) throw new IOException("Identidade ou owner indisponível para inicializar autoridade.");
                Directory.CreateDirectory(DirectoryPath);
                using var lease = Lease();
                if (File.Exists(AnchorPath) || File.Exists(FilePath) || File.Exists(FilePath + ".bak")) throw new IOException("Autoridade surgiu durante inicialização; reabra para consulta.");
                var initial = new LedgerDocument { Binding = new() { LedgerId = Guid.NewGuid(), Version = 2, Sid = owner.Sid, Fixture = fixture } };
                AtomicWrite(initial, false);
                var anchor = JsonSerializer.SerializeToUtf8Bytes(initial.Binding, LedgerJson.Default.AuthorityBinding);
                using var stream = new FileStream(AnchorPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); stream.Write(anchor); stream.Flush(true);
            }
            LoadAuthority();
        }
        catch (Exception ex) when (Known(ex)) { Block("Autoridade indisponível: " + ex.Message); ConsultBackup(); }
    }
    public static RequestLedger OpenReal(UserEffectOwner owner, CatalogStore profile)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManager");
        return new(root, owner, false, [profile.FilePath, profile.FilePath + ".bak", Path.Combine(root, "catalog.json"), Path.Combine(root, "catalog.json.bak")]);
    }
    public static RequestLedger OpenFixture(string isolatedNamespace, IEffectOwner owner, params string[] profileFiles)
    {
        if (!owner.Fixture) throw new ArgumentException("Owner fixture explícito necessário.");
        return new(isolatedNamespace, owner, true, profileFiles);
    }
    bool Owns() => owner.Held && owner.Trusted && owner.Sid.Length > 0 && owner.LoginKey.Length > 0 && owner.Fixture == fixture && (fixture || owner is UserEffectOwner);
    static bool Known(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException;
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    void SafeDirectory()
    {
        if (!Path.IsPathFullyQualified(DirectoryPath) || DirectoryPath.StartsWith(@"\\") || DirectoryPath[2..].Contains(':')) throw new IOException("Autoridade exige disco local.");
        for (string? path = DirectoryPath; path is not null; path = Path.GetDirectoryName(path))
            if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Autoridade em pasta redirecionada recusada.");
        foreach (var path in new[] { FilePath, AnchorPath, FilePath + ".bak" })
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Arquivo de autoridade com link recusado.");
    }
    static bool ProfileRequiresAuthority(string file)
    {
        if (!File.Exists(file)) return false;
        if (new FileInfo(file).Length > 2_000_000) return true;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(file)); var root = json.RootElement;
            return root.ValueKind != JsonValueKind.Object || root.TryGetProperty("Authority", out _) || !root.TryGetProperty("Schema", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema is not (1 or 2 or 3);
        }
        catch (Exception e) when (Known(e)) { return true; }
    }
    FileStream Lease() => new(Path.Combine(DirectoryPath, "authority.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    static void Require(JsonElement obj, params string[] properties)
    {
        if (obj.ValueKind != JsonValueKind.Object || properties.Any(name => obj.EnumerateObject().Count(p => p.NameEquals(name)) != 1)) throw new InvalidDataException("Campos obrigatórios de autoridade ausentes ou duplicados.");
    }
    static byte[] Bounded(string path)
    { if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException("Autoridade excede limite."); return File.ReadAllBytes(path); }
    static LedgerDocument Read(string path, out string raw)
    {
        var bytes = Bounded(path); raw = Hash(bytes); using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        Require(root, "Schema", "Binding", "Requests");
        if (root.GetProperty("Schema").ValueKind != JsonValueKind.Number || !root.GetProperty("Schema").TryGetInt32(out var version) || version is not (1 or 2)) throw new InvalidDataException("Versão do ledger inválida."); Require(root.GetProperty("Binding"), "LedgerId", "Version", "Sid", "Fixture");
        if (root.GetProperty("Requests").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Pedidos inválidos.");
        foreach (var request in root.GetProperty("Requests").EnumerateArray())
        {
            Require(request, "Nonce", "ScriptPath", "InterpreterPath", "Sid", "ManagerPid", "ManagerCreated", "LoginKey", "SessionId", "RequestedUtcTicks", "Candidate");
            if (version == 2) { Require(request, "Context"); Require(request.GetProperty("Context"), "Kind", "Origin", "Blob"); }
            else if (request.TryGetProperty("Context", out _)) throw new InvalidDataException("Contexto não pertence ao ledger antigo.");
            var candidate = request.GetProperty("Candidate");
            if (candidate.ValueKind != JsonValueKind.Null) Require(candidate, "Pid", "Created", "Executable");
        }
        var result = JsonSerializer.Deserialize(bytes, LedgerJson.Default.LedgerDocument) ?? throw new InvalidDataException("Autoridade vazia."); Validate(result); return result;
    }
    static void Validate(LedgerDocument value)
    {
        if (value.Schema is not (1 or 2) || value.Binding.Version != value.Schema || !AuthorityBinding.Valid(value.Binding) || value.Requests is null || value.Requests.Count > 2000) throw new InvalidDataException("Autoridade inválida.");
        HashSet<Guid> nonces = []; HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in value.Requests)
        {
            if (r is null || r.Context is null) throw new InvalidDataException("Pedido/contexto ausente.");
            RequestContext.Validate(r.Context);
            if (value.Schema == 1 && r.Context.Kind != "Legacy") throw new InvalidDataException("Pedido legado não pode receber contexto retroativo.");
            if (r is null || r.Nonce == Guid.Empty || !nonces.Add(r.Nonce) || !paths.Add(Paths.Lexical(r.ScriptPath)) || Paths.LexicalInterpreter(r.InterpreterPath) != r.InterpreterPath || r.Sid != value.Binding.Sid || r.ManagerPid <= 0 || r.ManagerCreated <= 0 || r.LoginKey is null || r.LoginKey.Length is 0 or > 200 || r.SessionId < 0 || r.RequestedUtcTicks <= 0 || r.RequestedUtcTicks > DateTime.MaxValue.Ticks || r.Candidate is { } c && (c.Pid <= 0 || c.Created <= 0 || !Paths.Equal(c.Executable, r.InterpreterPath))) throw new InvalidDataException("Pedido com identidade inválida.");
        }
    }
    void LoadAuthority()
    {
        SafeDirectory(); if (!File.Exists(AnchorPath) || !File.Exists(FilePath)) throw new IOException("Autoridade parcial ou removida após histórico. Não será recriada vazia.");
        var anchorBytes = Bounded(AnchorPath); using var anchorJson = JsonDocument.Parse(anchorBytes); Require(anchorJson.RootElement, "LedgerId", "Version", "Sid", "Fixture");
        var anchor = JsonSerializer.Deserialize(anchorBytes, LedgerJson.Default.AuthorityBinding);
        var loaded = Read(FilePath, out var raw);
        if (!AuthorityBinding.Same(anchor, loaded.Binding) || loaded.Binding.Sid != owner.Sid || loaded.Binding.Fixture != fixture) throw new InvalidDataException("Vínculo/SID/modo da autoridade diverge.");
        document = loaded; fingerprint = raw; anchorFingerprint = Hash(anchorBytes); Ready = true; ReadOnly = false; Notice = "";
    }
    void ConsultBackup()
    { try { var backup = Read(FilePath + ".bak", out _); if (backup.Binding.Sid == owner.Sid && backup.Binding.Fixture == fixture) document = backup; } catch (Exception e) when (Known(e)) { } }
    void Block(string message) { Ready = false; ReadOnly = true; Notice = message; }
    public Result Verify(AuthorityBinding? binding)
    {
        if (NeedsUpgrade || binding?.Version != 2) return Result.Fail("Conversão explícita da autoridade para versão 2 necessária. Efeitos bloqueados.");
        return VerifyCurrent(binding);
    }
    Result VerifyCurrent(AuthorityBinding? binding)
    {
        if (!Ready || ReadOnly || document is null) return Result.Fail(Notice.Length == 0 ? "Autoridade não inicializada." : Notice);
        try
        {
            SafeDirectory(); var current = Read(FilePath, out var raw);
            var same = raw == fingerprint && Hash(Bounded(AnchorPath)) == anchorFingerprint && AuthorityBinding.Same(binding, document.Binding) && AuthorityBinding.Same(current.Binding, document.Binding) && JsonSerializer.SerializeToUtf8Bytes(current, LedgerJson.Default.LedgerDocument).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(document, LedgerJson.Default.LedgerDocument));
            if (!same) { Block("Autoridade alterada, vínculo divergente ou arquivo perdido. Reabra para consulta; nenhum início autorizado."); return Result.Fail(Notice); }
            return Result.Ok("Autoridade íntegra e atual.");
        }
        catch (Exception e) when (Known(e)) { Block("Autoridade inacessível: " + e.Message); return Result.Fail(Notice); }
    }
    public Result Refresh(AuthorityBinding? binding)
    {
        // External edits never rebase a known request away. Only our durable Save advances this instance.
        var current = Verify(binding);
        return current.Success ? Result.Ok("Autoridade consultada sem alterar pedidos conhecidos.") : current;
    }
    public LaunchRequest? Find(string path) => document?.Requests.FirstOrDefault(r => Paths.Equal(r.ScriptPath, path))?.Copy();
    void AtomicWrite(LedgerDocument next, bool replacing)
    {
        Validate(next); var bytes = JsonSerializer.SerializeToUtf8Bytes(next, LedgerJson.Default.LedgerDocument); var temporary = FilePath + ".tmp";
        if (bytes.Length > 1_000_000) throw new InvalidDataException("Autoridade excede limite.");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (replacing) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
            document = next; fingerprint = Hash(bytes);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }
    void Save(LedgerDocument next)
    {
        if (!Owns()) throw new IOException("Owner da autoridade indisponível.");
        using var lease = Lease(); var valid = Verify(next.Binding); if (!valid.Success) throw new IOException(valid.Message);
        try { AtomicWrite(next, true); } catch { Block("Gravação durável da autoridade falhou. Pedidos anteriores permanecem barreira."); throw; }
    }
    public Result Upgrade(AuthorityBinding? profileBinding, Func<bool> profileCurrent)
    {
        try
        {
            if (!Owns() || !profileCurrent() || document is null) return Result.Fail("Conversão recusada pelo owner ou primário.");
            if (!NeedsUpgrade)
            {
                // Alternate old profile may bind only to this already recognized authority.
                var old = profileBinding?.Copy(); if (old is not null) old.Version = 2;
                return Verify(old);
            }
            using var lease = Lease(); var valid = VerifyCurrent(profileBinding ?? document.Binding); if (!valid.Success) return valid;
            if (!profileCurrent()) return Result.Fail("Primário mudou antes da conversão do ledger.");
            var next = document.Copy(); next.Schema = 2; next.Binding.Version = 2;
            foreach (var request in next.Requests) request.Context = new();
            AtomicWrite(next, true);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next.Binding, LedgerJson.Default.AuthorityBinding);
            var temporary = AnchorPath + ".tmp";
            try
            {
                if (!Owns() || !profileCurrent() || Hash(Bounded(AnchorPath)) != anchorFingerprint || Hash(Bounded(FilePath)) != fingerprint) throw new IOException("Primário/owner mudou entre as escritas da autoridade. Estado parcial preservado.");
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                File.Replace(temporary, AnchorPath, AnchorPath + ".bak"); anchorFingerprint = Hash(bytes);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Result.Ok("Autoridade versão 2 persistida; pedidos antigos permanecem Legacy.");
        }
        catch (Exception e) when (Known(e)) { Block("Conversão parcial da autoridade. Efeitos bloqueados; arquivos preservados para manutenção. " + e.Message); return Result.Fail(Notice); }
    }
    internal LaunchRequest Prepare(ScriptEntry entry, DateTime utc)
    {
        var script = Paths.Normalize(entry.Path); var interpreter = fixture ? Paths.LexicalInterpreter(entry.Interpreter) : Paths.NormalizeInterpreter(entry.Interpreter);
        if (!Paths.Equal(script, entry.Path) || !Paths.Equal(interpreter, entry.Interpreter)) throw new IOException("Caminhos do pedido devem ser canônicos.");
        return new() { Nonce = Guid.NewGuid(), ScriptPath = script, InterpreterPath = interpreter, Sid = owner.Sid, ManagerPid = managerPid, ManagerCreated = managerCreated, LoginKey = owner.LoginKey, SessionId = sessionId, RequestedUtcTicks = utc.Ticks };
    }
    internal ManualLaunchPermit Register(ScriptEntry entry, LaunchRequest prepared, BootContextService contextProof, Catalog catalog, CatalogStore profile, Func<Result> manualContext)
    {
        var context = manualContext(); if (!context.Success) throw new IOException(context.Message);
        var valid = Verify(catalog.Authority); if (!valid.Success) throw new IOException(valid.Message);
        if (!Owns() || HasPending || !entry.RunAsAdministrator || !profile.VerifyPrimary(catalog) || prepared.Nonce == Guid.Empty || prepared.ManagerPid != managerPid || prepared.ManagerCreated != managerCreated || !Paths.Equal(prepared.ScriptPath, entry.Path) || !Paths.Equal(prepared.InterpreterPath, entry.Interpreter)) throw new IOException(HasPending ? PendingReason : "Registro manual recusado.");
        valid = contextProof.Verify(prepared, document!.Binding); if (!valid.Success) throw new IOException(valid.Message);
        var next = document.Copy(); next.Requests.Add(prepared.Copy()); Save(next);
        string expectedLedger = fingerprint, expectedProfile = profile.LoadedFingerprint;
        return new(fixture, next.Binding.LedgerId, prepared.Nonce, prepared.ScriptPath, prepared.InterpreterPath, () =>
        {
            var gate = manualContext(); if (!gate.Success) return gate;
            if (!Owns() || profile.LoadedFingerprint != expectedProfile || !profile.VerifyPrimary(catalog) || fingerprint != expectedLedger) return Result.Fail("Owner, perfil ou fingerprint do permit mudou.");
            var current = Verify(catalog.Authority); if (!current.Success) return current;
            var own = Find(prepared.ScriptPath);
            if (own is null || own.Nonce != prepared.Nonce || own.ManagerPid != managerPid || own.ManagerCreated != managerCreated || own.Sid != owner.Sid || document!.Requests.Count != 1) return Result.Fail("Pedido/nonce do permit mudou.");
            return contextProof.BeforeDispatch(own, next.Binding);
        });
    }
    internal Result ClearAfterReset(LaunchRequest expected, string expectedFingerprint)
    {
        try
        {
            var current = Find(expected.ScriptPath);
            if (!Owns() || fingerprint != expectedFingerprint || current is null || current.Nonce != expected.Nonce || !JsonSerializer.SerializeToUtf8Bytes(current, LedgerJson.Default.LaunchRequest).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, LedgerJson.Default.LaunchRequest))) return Result.Fail("Pedido ou fingerprint mudou; recuperação recusada.");
            var next = document!.Copy(); next.Requests.RemoveAll(r => r.Nonce == expected.Nonce); Save(next); return Result.Ok("Pedido removido duravelmente após prova de reinicialização e ausência. Nenhum script iniciado. A supervisão configurada poderá conferir novamente no próximo ciclo ordinário.");
        }
        catch (Exception e) when (Known(e)) { return Result.Fail("Remoção não foi persistida; pedido permanece barreira após reabrir. " + e.Message); }
    }
    internal Result ClearOwn(ManualLaunchPermit permit, ElevationOutcome outcome)
    {
        if (permit.Fixture != fixture || permit.LedgerId != document?.Binding.LedgerId || permit.DispatchStarted && outcome.Disposition != ElevationDisposition.Cancelled) return Result.Fail("Pedido permanece registrado: despacho não pôde ser descartado.");
        try
        {
            var own = Find(permit.ScriptPath);
            if (!Owns() || own is null || own.Nonce != permit.Nonce || own.ManagerPid != managerPid || own.ManagerCreated != managerCreated || own.Sid != owner.Sid) return Result.Fail("Pedido/owner mudou; nenhuma pendência removida.");
            var next = document!.Copy(); next.Requests.RemoveAll(r => r.Nonce == permit.Nonce); Save(next); return Result.Ok("Cancelamento confirmado; próprio pedido removido duravelmente.");
        }
        catch (Exception e) when (Known(e)) { return Result.Fail("Cancelamento sem limpeza durável. Pedido continua bloqueando repetição. " + e.Message); }
    }
    internal Result SetCandidate(ManualLaunchPermit permit, LaunchCandidate candidate)
    {
        try
        {
            var next = document!.Copy(); var own = next.Requests.SingleOrDefault(r => r.Nonce == permit.Nonce && Paths.Equal(r.ScriptPath, permit.ScriptPath));
            if (own is null || !Paths.Equal(candidate.Executable, own.InterpreterPath)) return Result.Fail("Candidato não corresponde ao pedido.");
            own.Candidate = candidate; Save(next); return Result.Ok("Candidato registrado; ainda não é prova de execução/privilégio.");
        }
        catch (Exception e) when (Known(e)) { return Result.Fail("Candidato não pôde ser persistido. Pedido continua registrado. " + e.Message); }
    }
    internal Result ClearPositive(LaunchRequest request, ProcessIdentity positivelyVerified)
    {
        try
        {
            var current = Find(request.ScriptPath);
            if (request.Candidate is null || positivelyVerified.Pid != request.Candidate.Pid || positivelyVerified.Created != request.Candidate.Created || !Paths.Equal(positivelyVerified.Script, request.ScriptPath) || !Paths.Equal(positivelyVerified.Executable, request.InterpreterPath) || current is null || current.Nonce != request.Nonce || current.Candidate != request.Candidate || current.Sid != owner.Sid) return Result.Fail("Pedido/candidato mudou. Nenhuma pendência removida.");
            var next = document!.Copy(); next.Requests.RemoveAll(r => r.Nonce == request.Nonce); Save(next); return Result.Ok("Identidade positiva correlacionada; pedido removido duravelmente, sem afirmar privilégio.");
        }
        catch (Exception e) when (Known(e)) { return Result.Fail("Verificação positiva sem limpeza durável. Pedido permanece. " + e.Message); }
    }
}
