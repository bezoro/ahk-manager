using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ScriptEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionSource { get; set; } = "Inferida";
    public bool LaunchBlocked { get; set; }
    public bool RunAsAdministrator { get; set; }
    public string Interpreter { get; set; } = "";
    public bool? StartWithWindows { get; set; }
    [JsonIgnore] public bool? RequestedEnabled { get => StartWithWindows; set => StartWithWindows = value; }
    public ManagementMode ManagementMode { get; set; }
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    public ScriptEntry Copy() => new() { Id = Id, Path = Path, Description = Description, DescriptionSource = DescriptionSource, LaunchBlocked = LaunchBlocked, RunAsAdministrator = RunAsAdministrator, Interpreter = Interpreter, StartWithWindows = StartWithWindows, ManagementMode = ManagementMode };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AuthorityBinding
{
    public Guid LedgerId { get; set; }
    public int Version { get; set; }
    public string Sid { get; set; } = "";
    public bool Fixture { get; set; }
    public AuthorityBinding Copy() => new() { LedgerId = LedgerId, Version = Version, Sid = Sid, Fixture = Fixture };
    public static bool Valid(AuthorityBinding? b) => b is not null && b.LedgerId != Guid.Empty && b.Version is 1 or 2 && b.Sid is { Length: > 0 and <= 200 };
    public static bool Same(AuthorityBinding? a, AuthorityBinding? b) => Valid(a) && Valid(b) && a!.LedgerId == b!.LedgerId && a.Version == b.Version && a.Sid == b.Sid && a.Fixture == b.Fixture;
}
public enum ManagementMode { Unmanaged, Normal, ExternalPending, Retired }
public enum MigrationState { NotPrepared, Prepared, Incomplete, Completed }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MigrationRecord
{
    public MigrationState State { get; set; }
    public string ManifestHash { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public string JournalDigest { get; set; } = "";
    public long CompletedUtcTicks { get; set; }
    public MigrationRecord Copy() => new() { State = State, ManifestHash = ManifestHash, OwnerSid = OwnerSid, JournalDigest = JournalDigest, CompletedUtcTicks = CompletedUtcTicks };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RecoveryRecord
{
    public Guid ScriptId { get; set; }
    public string LoginKey { get; set; } = "";
    public List<long> AttemptsUtcTicks { get; set; } = [];
    public int Failures { get; set; }
    public long NextTryUtcTicks { get; set; }
    public bool Suspended { get; set; }
    public RecoveryRecord Copy() => new() { ScriptId = ScriptId, LoginKey = LoginKey, AttemptsUtcTicks = [.. AttemptsUtcTicks], Failures = Failures, NextTryUtcTicks = NextTryUtcTicks, Suspended = Suspended };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MigrationJournal
{
    public int Schema { get; set; } = 1;
    public bool Fixture { get; set; }
    public string LoginKey { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public MigrationState State { get; set; }
    public List<string> AppliedSteps { get; set; } = [];
    public string BackupDirectory { get; set; } = "";
    public string BackupDigest { get; set; } = "";
    public string CatalogDigest { get; set; } = "";
    public string PendingStep { get; set; } = "";
    public ProcessIdentity? Supervisor { get; set; }
    public List<ProcessIdentity> Children { get; set; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Catalog
{
    public int Schema { get; set; } = 5;
    public AuthorityBinding? Authority { get; set; }
    public List<ScriptEntry> Scripts { get; set; } = [];
    public MigrationRecord Migration { get; set; } = new();
    public List<RecoveryRecord> Recovery { get; set; } = [];
    public LoginRuntime Runtime { get; set; } = new();
    public Catalog Copy() => new() { Schema = Schema, Authority = Authority?.Copy(), Scripts = Scripts.Select(e => e.Copy()).ToList(), Migration = Migration.Copy(), Recovery = Recovery.Select(r => r.Copy()).ToList(), Runtime = Runtime.Copy() };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RuntimeIntent
{
    public Guid ScriptId { get; set; }
    public bool DesiredRunning { get; set; }
    public RuntimeIntent Copy() => new() { ScriptId = ScriptId, DesiredRunning = DesiredRunning };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LoginRuntime
{
    public string LoginKey { get; set; } = "";
    public bool Decided { get; set; }
    public bool AutomaticPaused { get; set; }
    public List<RuntimeIntent> Intents { get; set; } = [];
    public LoginRuntime Copy() => new() { LoginKey = LoginKey, Decided = Decided, AutomaticPaused = AutomaticPaused, Intents = Intents.Select(i => i.Copy()).ToList() };
    public void Set(Guid id, bool desired) { var item = Intents.SingleOrDefault(i => i.ScriptId == id); if (item is null) Intents.Add(new() { ScriptId = id, DesiredRunning = desired }); else item.DesiredRunning = desired; }
    public bool Wants(Guid id) => Intents.Any(i => i.ScriptId == id && i.DesiredRunning);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LegacyScriptEntry
{
    public Guid Id { get; set; }
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionSource { get; set; } = "Inferida";
    public bool LaunchBlocked { get; set; }
    public bool RunAsAdministrator { get; set; }
    public string Interpreter { get; set; } = "";
    public bool? RequestedEnabled { get; set; }
    public ManagementMode ManagementMode { get; set; }
    public ScriptEntry Convert(int schema) => new() { Id = Id, Path = Path, Description = Description, DescriptionSource = DescriptionSource, LaunchBlocked = LaunchBlocked, RunAsAdministrator = RunAsAdministrator, Interpreter = Interpreter, StartWithWindows = schema == 1 ? null : RequestedEnabled, ManagementMode = schema == 1 ? ManagementMode.Unmanaged : ManagementMode };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LegacyCatalog
{
    public int Schema { get; set; }
    public AuthorityBinding? Authority { get; set; }
    public List<LegacyScriptEntry> Scripts { get; set; } = [];
    public MigrationRecord Migration { get; set; } = new();
    public List<RecoveryRecord> Recovery { get; set; } = [];
    public Catalog Convert() => new() { Schema = Schema, Authority = Authority, Scripts = Scripts.Select(e => e.Convert(Schema)).ToList(), Migration = Migration, Recovery = Recovery };
}
public sealed class ProbeProcess { public int Id { get; set; } public long Created { get; set; } public string? Executable { get; set; } public string? Command { get; set; } }
public sealed class ProbeStartup { public string Source { get; set; } = ""; public string Command { get; set; } = ""; public bool Elevated { get; set; } }
public sealed class StartupProbe { public List<ProbeStartup> Items { get; set; } = []; public List<string> Limits { get; set; } = []; }
public sealed record ProcessIdentity(int Pid, long Created, string Executable, string? Script, bool? Elevated = null)
{
    public static bool SameCore(ProcessIdentity? a, ProcessIdentity? b) => a is not null && b is not null && a.Pid == b.Pid && a.Created == b.Created && Paths.Equal(a.Executable, b.Executable) && Paths.Equal(a.Script, b.Script);
}
public sealed record Snapshot(List<ProcessIdentity> Processes, bool Complete, string Note, string? SupervisorPath = null)
{
    public List<ProcessIdentity> For(string path) => Processes.Where(p => Paths.Equal(p.Script, path)).ToList();
    public bool Unknown => !Complete || Processes.Any(p => p.Script is null);
}
public sealed record StartupSource(string Path, string Source, bool Elevated);
public sealed record DiscoveryResult(List<string> Paths, List<StartupSource> Sources, List<string> Limits);
public sealed record Result(bool Success, string Message) { public static Result Ok(string m) => new(true, m); public static Result Fail(string m) => new(false, m); }
public enum ScriptAction { Start, Stop, Restart, Recycle }

public static class Paths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetLongPathName(string path, StringBuilder output, uint size);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct FileIdentity { public uint Attributes; public long Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity identity);
    public static string Lexical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.Contains('"')) throw new ArgumentException("Caminho absoluto inválido.");
        var full = System.IO.Path.GetFullPath(path);
        if (full.StartsWith(@"\\") || full[2..].Contains(':') || full.Any(c => c < 32 || "<>|*?".Contains(c)) || !full.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Selecione um arquivo .ahk em disco local, sem links ou caminhos especiais.");
        return full;
    }
    public static string LexicalInterpreter(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.Contains('"')) throw new ArgumentException("Interpretador absoluto inválido.");
        var full = System.IO.Path.GetFullPath(path);
        if (full.StartsWith(@"\\") || full[2..].Contains(':') || full.Any(c => c < 32 || "<>|*?".Contains(c)) || !Commands.IsInterpreter(full)) throw new ArgumentException("Interpretador AutoHotkey local necessário.");
        return full;
    }
    public static string NormalizeInterpreter(string path)
    {
        var full = LexicalInterpreter(path);
        if (!File.Exists(full)) throw new ArgumentException("Interpretador não encontrado.");
        for (string? current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Interpretador com link ou redirecionamento recusado.");
        StringBuilder buffer = new(32768); if (GetLongPathName(full, buffer, (uint)buffer.Capacity) == 0) throw new ArgumentException("Identidade do interpretador incerta.");
        full = buffer.ToString(); using var file = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(file, out var identity) || identity.Links != 1) throw new ArgumentException("Interpretador com identidade incerta ou vários links.");
        return full;
    }
    public static string Normalize(string path)
    {
        var full = Lexical(path);
        for (string? current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Links e pastas redirecionadas não são aceitos.");
        }
        if (File.Exists(full))
        {
            StringBuilder buffer = new(32768); if (GetLongPathName(full, buffer, (uint)buffer.Capacity) == 0) throw new ArgumentException("Não foi possível normalizar o caminho local."); full = buffer.ToString();
            using var file = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(file, out var identity) || identity.Links != 1) throw new ArgumentException("Arquivo com identidade incerta ou vários links não é aceito.");
        }
        return full;
    }
    public static bool Equal(string? a, string? b) => a is not null && b is not null && StringComparer.OrdinalIgnoreCase.Equals(a, b);
    public static bool Matches(ScriptEntry e, string term) => (e.Name + " " + e.Description + " " + e.Path).Contains(term, StringComparison.OrdinalIgnoreCase);
    public static bool Supervisor(string path) => System.IO.Path.GetFileName(path).Equals("startup.ahk", StringComparison.OrdinalIgnoreCase);
    public static bool ChildOf(string child, string supervisor) => Paths.Equal(System.IO.Path.GetDirectoryName(child), System.IO.Path.Combine(System.IO.Path.GetDirectoryName(supervisor)!, "scripts", "startup"));
}

public static class Policy
{
    public static Result Check(ScriptEntry e, Snapshot s, ScriptAction action, bool exists)
    {
        var active = s.For(e.Path);
        if (e.RunAsAdministrator && action == ScriptAction.Restart) return Result.Fail("Reinício bloqueado: não interromper a instância antes de uma solicitação UAC que pode ser cancelada.");
        if (action is ScriptAction.Start or ScriptAction.Restart)
        {
            if (e.LaunchBlocked) return Result.Fail("Início bloqueado somente neste gerenciador.");
            if (!exists) return Result.Fail("Arquivo não encontrado.");
            if (string.IsNullOrEmpty(e.Interpreter)) return Result.Fail("Escolha um interpretador AutoHotkey.");
            if (s.Unknown) return Result.Fail("Há AutoHotkey inacessível ou inspeção parcial. Não é seguro iniciar uma possível duplicata.");
            if (Paths.Equal(e.Path, s.SupervisorPath) && s.Processes.Any(p => p.Script is not null && Paths.ChildOf(p.Script, e.Path))) return Result.Fail("Há filhos em execução. Iniciar o supervisor pode substituir instâncias com #SingleInstance Force.");
            if (s.SupervisorPath is not null && s.Processes.Any(p => Paths.Equal(p.Script, s.SupervisorPath)) && Paths.ChildOf(e.Path, s.SupervisorPath)) return Result.Fail("O supervisor está ativo. Ele pode relançar este filho; início e reinício bloqueados.");
        }
        if (action == ScriptAction.Start && active.Count > 0) return Result.Fail("O script já está em execução.");
        if (action is ScriptAction.Stop or ScriptAction.Restart)
        {
            if (active.Count != 1) return Result.Fail(active.Count == 0 ? "Nenhuma instância verificável selecionada." : "Há várias instâncias. A ação exige uma única instância verificável.");
            if (active[0].Created == 0 || string.IsNullOrEmpty(active[0].Executable)) return Result.Fail("Identidade de processo insuficiente.");
        }
        if (action == ScriptAction.Recycle)
        {
            if (!exists) return Result.Fail("Arquivo não encontrado.");
            if (s.Unknown) return Result.Fail("Inspeção parcial: não é possível assegurar que o script está parado.");
            if (active.Count > 0) return Result.Fail("Pare o script antes de enviá-lo à lixeira.");
        }
        return Result.Ok("Permitido");
    }
}

[JsonSerializable(typeof(Catalog))]
[JsonSerializable(typeof(LegacyCatalog))]
[JsonSerializable(typeof(MigrationJournal))]
[JsonSerializable(typeof(List<ProbeProcess>))]
[JsonSerializable(typeof(StartupProbe))]
internal partial class AppJson : JsonSerializerContext { }
