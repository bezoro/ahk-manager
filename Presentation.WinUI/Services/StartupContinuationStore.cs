using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public enum StartupContinuationPhase { Prepared, Committed, Accepted, Completed, Partial, Recovered }
public enum StartupContinuationItemState { NotDispatched, EnteringExecution, KnownRunning, KnownStarted, KnownFailed, RefusedBeforeExecution, Uncertain }
public sealed record StartupContinuationItem(Guid ScriptId, string Name, string ScriptPath, string InterpreterPath, string ScriptHash, string InterpreterHash);
public sealed record StartupContinuationTicket(int Version, Guid OperationId, string ProfileRoot, string ProfileHash, string UserSid, int SessionId, string LoginKey, DateTimeOffset ExpiresAt, ImmutableArray<StartupContinuationItem> Items);
public sealed record StartupContinuationRecord(StartupContinuationTicket Ticket, string TicketHash, StartupContinuationPhase Phase, ImmutableArray<StartupContinuationItemState> Items, OperationResult? Result = null);
public sealed record StartupContinuationOffer(Guid OperationId, ImmutableArray<string> ScriptNames, bool CanResume, ReasonCode Reason);

// Recovery evidence only. A record or checksum never grants permission to execute:
// acceptance also requires the separately authenticated, live bootstrap channel.
internal sealed class StartupContinuationStore(string root)
{
    internal const int MaximumItems = 2000;
    internal const int MaximumBytes = 2 * 1024 * 1024;
    string DirectoryPath => Path.Combine(root, "startup-continuations");
    string FilePath(Guid id) => Path.Combine(DirectoryPath, id.ToString("D") + ".json");
    internal static string Hash(StartupContinuationTicket ticket) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ticket, ContinuationJson.Default.StartupContinuationTicket))).ToLowerInvariant();
    internal async Task<StartupContinuationRecord?> ReadAsync(Guid id)
    {
        if (id == Guid.Empty) return null;
        var bytes = await SafeJsonFiles.ReadAsync(FilePath(id), CancellationToken.None).ConfigureAwait(false);
        if (bytes is null) return null;
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Continuation exceeds limit.");
        using var json = JsonDocument.Parse(bytes); SafeJsonFiles.UniqueProperties(json.RootElement);
        var record = JsonSerializer.Deserialize(bytes, ContinuationJson.Default.StartupContinuationRecord) ?? throw new InvalidDataException("Continuation absent.");
        if (record.Ticket is null || record.Ticket.OperationId != id || record.Ticket.Version != 1 || record.Ticket.Items.IsDefaultOrEmpty || record.Ticket.Items.Length > MaximumItems || record.Ticket.Items.Any(i => i is null || i.ScriptId == Guid.Empty || string.IsNullOrEmpty(i.ScriptPath) || string.IsNullOrEmpty(i.InterpreterPath) || i.ScriptHash is not { Length: 64 } || i.InterpreterHash is not { Length: 64 }) || record.Ticket.Items.Select(i => i.ScriptId).Distinct().Count() != record.Ticket.Items.Length || record.Items.Length != record.Ticket.Items.Length || record.Items.Any(i => !Enum.IsDefined(i)) || !Enum.IsDefined(record.Phase) || record.TicketHash != Hash(record.Ticket)) throw new InvalidDataException("Continuation identity changed.");
        return record;
    }
    internal async Task WriteAsync(StartupContinuationRecord record, StartupContinuationRecord? expected)
    {
        string path = FilePath(record.Ticket.OperationId); SafeJsonFiles.SafePath(path);
        byte[]? existing = await SafeJsonFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (expected is null && existing is not null || expected is not null && (existing is null || !existing.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, ContinuationJson.Default.StartupContinuationRecord)))) throw new ConcurrentWriteException();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, ContinuationJson.Default.StartupContinuationRecord);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Continuation exceeds limit.");
        await SafeJsonFiles.ReplaceAsync(path, bytes, existing is null ? null : SafeJsonFiles.Hash(existing), CancellationToken.None, false).ConfigureAwait(false);
    }
    internal async Task<StartupContinuationRecord?> FindAsync(string loginKey)
    {
        SafeJsonFiles.SafePath(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return null;
        StartupContinuationRecord? latest = null;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) && await ReadAsync(id).ConfigureAwait(false) is { } record && record.Ticket.LoginKey == loginKey && (latest is null || record.Ticket.ExpiresAt > latest.Ticket.ExpiresAt)) latest = record;
        return latest;
    }
    internal async Task<ImmutableArray<StartupContinuationRecord>> PendingAsync()
    {
        SafeJsonFiles.SafePath(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return [];
        var records = ImmutableArray.CreateBuilder<StartupContinuationRecord>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) && await ReadAsync(id).ConfigureAwait(false) is { } record && record.Phase != StartupContinuationPhase.Recovered && (record.Phase is StartupContinuationPhase.Prepared or StartupContinuationPhase.Committed or StartupContinuationPhase.Accepted || record.Items.Any(i => i is StartupContinuationItemState.EnteringExecution or StartupContinuationItemState.Uncertain))) records.Add(record);
        return records.ToImmutable();
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StartupContinuationTicket))]
[JsonSerializable(typeof(StartupContinuationRecord))]
internal partial class ContinuationJson : JsonSerializerContext;
