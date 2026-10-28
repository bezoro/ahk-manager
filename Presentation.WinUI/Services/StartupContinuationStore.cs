using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public enum StartupContinuationPhase { Prepared, Committed, Accepted, Completed, Partial, Recovered }
public enum StartupContinuationItemState { NotDispatched, EnteringExecution, KnownRunning, KnownStarted, KnownFailed, RefusedBeforeExecution, Uncertain }
public sealed record StartupContinuationItem(Guid ScriptId, string Name, string ScriptPath, string InterpreterPath, string ScriptHash, string InterpreterHash);
public sealed record StartupContinuationTicket(int Version, Guid OperationId, string ProfileRoot, string ProfileHash, string UserSid, int SessionId, string LoginKey, DateTimeOffset ExpiresAt, ImmutableArray<StartupContinuationItem> Items);
public sealed record StartupContinuationRecord(StartupContinuationTicket Ticket, string TicketHash, StartupContinuationPhase Phase, ImmutableArray<StartupContinuationItemState> Items, OperationResult? Result = null);
public sealed record StartupContinuationOffer(Guid OperationId, ImmutableArray<string> ScriptNames, bool CanResume, ReasonCode Reason);
internal sealed record StartupRecoveryFile(string OriginalName, string PreservedName, string Hash, string FileIdentity);
internal sealed record StartupRecoveryReceipt(Guid OperationId, DateTimeOffset RequestedAt, ImmutableArray<StartupRecoveryFile> Files);

// Recovery evidence only. A record or checksum never grants permission to execute:
// acceptance also requires the separately authenticated, live bootstrap channel.
internal sealed class StartupContinuationStore(string root)
{
    internal const int MaximumItems = 2000;
    internal const int MaximumBytes = SafeJsonFiles.MaximumBytes;
    const long MaximumRecoveryBytes = 64L * 1024 * 1024;
    static readonly ContinuationJson Json = new(new JsonSerializerOptions
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 64
    });
    string DirectoryPath => Path.Combine(root, "startup-continuations");
    string FilePath(Guid id) => Path.Combine(DirectoryPath, id.ToString("D") + ".json");
    internal static string Hash(StartupContinuationTicket ticket) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ticket, Json.StartupContinuationTicket))).ToLowerInvariant();
    internal async Task<StartupContinuationRecord?> ReadAsync(Guid id)
    {
        if (id == Guid.Empty) return null;
        string path = FilePath(id);
        try
        {
            var bytes = await SafeJsonFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
            return bytes is null ? null : Parse(bytes, id);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { throw InvalidRecord(path, ex); }
    }
    static StartupContinuationRecord Parse(byte[] bytes, Guid id)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Continuation exceeds limit.");
        using var json = JsonDocument.Parse(bytes); SafeJsonFiles.UniqueProperties(json.RootElement);
        var record = JsonSerializer.Deserialize(bytes, Json.StartupContinuationRecord) ?? throw new InvalidDataException("Continuation absent.");
        Validate(record, id);
        return record;
    }
    static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Contains('\0');
    static bool Hex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    static void Validate(StartupContinuationRecord record, Guid id)
    {
        var ticket = record.Ticket;
        if (ticket is null || ticket.OperationId == Guid.Empty || ticket.OperationId != id || ticket.Version != 1 || !Text(ticket.ProfileRoot, 32760) || !Hex(ticket.ProfileHash) || !Text(ticket.UserSid, 256) || ticket.SessionId < 0 || !Text(ticket.LoginKey, 512) || ticket.ExpiresAt == default || ticket.Items.IsDefaultOrEmpty || ticket.Items.Length > MaximumItems || ticket.Items.Any(item => item is null || item.ScriptId == Guid.Empty || !Text(item.Name, 200) || !Text(item.ScriptPath, 32760) || !Text(item.InterpreterPath, 32760) || !Hex(item.ScriptHash) || !Hex(item.InterpreterHash)) || ticket.Items.Select(item => item.ScriptId).Distinct().Count() != ticket.Items.Length || record.Items.IsDefault || record.Items.Length != ticket.Items.Length || record.Items.Any(item => !Enum.IsDefined(item)) || !Enum.IsDefined(record.Phase) || record.TicketHash != Hash(ticket)) throw new InvalidDataException("Continuation identity changed.");
        if (record.Result is { } result && (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Code) || result.Detail is { } detail && (detail.Length > 16000 || detail.Contains('\0')))) throw new InvalidDataException("Continuation result invalid.");
    }
    static InvalidDataException InvalidRecord(string path, Exception? inner = null) => new($"Não foi possível validar o registro de continuação em {path}. O arquivo foi preservado. Use Recuperar pendências para conferir os processos e preservar o registro inválido em quarentena, sem repetir execuções.", inner);
    internal async Task WriteAsync(StartupContinuationRecord record, StartupContinuationRecord? expected)
    {
        if (record is null) throw new InvalidDataException("Continuation absent.");
        Validate(record, record.Ticket?.OperationId ?? Guid.Empty);
        string path = FilePath(record.Ticket.OperationId); SafeJsonFiles.SafePath(path);
        byte[]? existing = await SafeJsonFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (expected is null && existing is not null || expected is not null && (existing is null || !JsonSerializer.SerializeToUtf8Bytes(Parse(existing, record.Ticket.OperationId), Json.StartupContinuationRecord).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, Json.StartupContinuationRecord)))) throw new ConcurrentWriteException();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json.StartupContinuationRecord);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Continuation exceeds limit.");
        await SafeJsonFiles.ReplaceAsync(path, bytes, existing is null ? null : SafeJsonFiles.Hash(existing), CancellationToken.None, false).ConfigureAwait(false);
    }
    internal async Task<StartupContinuationRecord?> FindAsync(string loginKey)
    {
        SafeJsonFiles.SafePath(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return null;
        StartupContinuationRecord? latest = null;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id)) throw InvalidRecord(path);
            if (await ReadAsync(id).ConfigureAwait(false) is { } record && record.Ticket.LoginKey == loginKey && (latest is null || record.Ticket.ExpiresAt > latest.Ticket.ExpiresAt)) latest = record;
        }
        return latest;
    }
    internal async Task<ImmutableArray<StartupContinuationRecord>> PendingAsync()
    {
        SafeJsonFiles.SafePath(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return [];
        var records = ImmutableArray.CreateBuilder<StartupContinuationRecord>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id)) throw InvalidRecord(path);
            if (await ReadAsync(id).ConfigureAwait(false) is { } record && record.Phase != StartupContinuationPhase.Recovered && (record.Phase is StartupContinuationPhase.Prepared or StartupContinuationPhase.Committed or StartupContinuationPhase.Accepted || record.Items.Any(i => i is StartupContinuationItemState.EnteringExecution or StartupContinuationItemState.Uncertain))) records.Add(record);
        }
        return records.ToImmutable();
    }

    // Called only by explicit recovery after complete process observation, while
    // the manager's operation lane excludes acceptance or execution. Evidence
    // never grants permission to dispatch and no source file is deleted.
    internal async Task<ImmutableArray<string>> QuarantineInvalidAsync(Func<Task>? beforeRevalidation = null)
    {
        SafeJsonFiles.SafePath(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return [];
        var entries = new List<RecoveryEntry>();
        try
        {
            foreach (string path in ActivePaths()) entries.Add(RecoveryEntry.Open(path));
            var invalid = entries.Where(entry => !entry.Valid).ToArray();
            if (invalid.Length == 0) return [];
            if (beforeRevalidation is not null) await beforeRevalidation().ConfigureAwait(false);
            RequirePaths(entries.Select(entry => entry.Path));
            foreach (var entry in entries) entry.Verify();

            Guid operation = Guid.NewGuid();
            string quarantine = Path.Combine(DirectoryPath, "quarantine", operation.ToString("D"));
            SafeJsonFiles.SafePath(quarantine);
            if (Directory.Exists(quarantine) || File.Exists(quarantine)) throw new ConcurrentWriteException();
            var files = invalid.Select((entry, index) => new StartupRecoveryFile(Path.GetFileName(entry.Path), $"{index:D4}-{entry.Hash}.json", entry.Hash, entry.Identity)).ToImmutableArray();
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new StartupRecoveryReceipt(operation, DateTimeOffset.UtcNow, files), Json.StartupRecoveryReceipt);
            string marker = FilePath(operation);
            // A crash or identity change after this marker leaves startup blocked
            // until another explicit recovery. The marker itself is preserved.
            await SafeJsonFiles.WriteNewAsync(marker, receipt, CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(quarantine);
            await SafeJsonFiles.WriteNewAsync(Path.Combine(quarantine, "manifest.json"), receipt, CancellationToken.None).ConfigureAwait(false);
            var preserved = ImmutableArray.CreateBuilder<string>();
            for (int index = 0; index < invalid.Length; index++)
            {
                var entry = invalid[index];
                entry.Close();
                // Permit this rename, but deny writes to the retained source.
                using var source = RecoveryEntry.Open(entry.Path, allowRename: true);
                if (source.Identity != entry.Identity || source.Hash != entry.Hash) throw new ConcurrentWriteException();
                string target = Path.Combine(quarantine, files[index].PreservedName);
                SafeJsonFiles.SafePath(target);
                File.Move(entry.Path, target, false);
                using var moved = RecoveryEntry.Open(target);
                if (moved.Identity != entry.Identity || moved.Hash != entry.Hash) throw new ConcurrentWriteException();
                preserved.Add(target);
            }
            RequirePaths(entries.Where(entry => entry.Valid).Select(entry => entry.Path).Append(marker));
            foreach (var entry in entries.Where(entry => entry.Valid)) entry.Verify();
            File.Move(marker, Path.Combine(quarantine, "completed.json"), false);
            return preserved.ToImmutable();
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }
    string[] ActivePaths()
    {
        SafeJsonFiles.SafePath(DirectoryPath);
        return Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    void RequirePaths(IEnumerable<string> expected)
    {
        if (!ActivePaths().SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new ConcurrentWriteException();
    }
    sealed class RecoveryEntry : IDisposable
    {
        FileStream? lease;
        internal readonly string Path, Hash, Identity;
        internal readonly bool Valid;
        RecoveryEntry(string path, bool allowRename)
        {
            SafeJsonFiles.SafePath(path);
            Path = path;
            lease = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | (allowRename ? FileShare.Delete : FileShare.None));
            try
            {
                if (lease.Length > MaximumRecoveryBytes) throw InvalidRecord(path);
                Identity = ReadIdentity(lease);
                Hash = Convert.ToHexString(SHA256.HashData(lease)).ToLowerInvariant();
                if (lease.Length <= MaximumBytes && Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(path), "D", out var id))
                {
                    lease.Position = 0;
                    byte[] bytes = new byte[checked((int)lease.Length)];
                    lease.ReadExactly(bytes);
                    try { _ = Parse(bytes, id); Valid = true; }
                    catch (Exception ex) when (SafeJsonFiles.Known(ex)) { Valid = false; }
                }
            }
            catch { lease.Dispose(); lease = null; throw; }
        }
        static string ReadIdentity(FileStream file)
        {
            if (!ProcessNative.GetFileInformationByHandle(file.SafeFileHandle, out var stamp) || stamp.Links != 1 || (stamp.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("Uncertain continuation file identity.");
            return $"{stamp.Volume:X8}:{stamp.IndexHigh:X8}{stamp.IndexLow:X8}";
        }
        internal static RecoveryEntry Open(string path, bool allowRename = false) => new(path, allowRename);
        internal void Verify()
        {
            using var current = Open(Path);
            if (current.Identity != Identity || current.Hash != Hash) throw new ConcurrentWriteException();
        }
        internal void Close() { lease?.Dispose(); lease = null; }
        public void Dispose() => Close();
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StartupContinuationTicket))]
[JsonSerializable(typeof(StartupContinuationRecord))]
[JsonSerializable(typeof(StartupRecoveryReceipt))]
internal partial class ContinuationJson : JsonSerializerContext;
