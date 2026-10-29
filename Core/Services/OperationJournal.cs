using System.Text.Json;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed class OperationJournal(string path)
{
    JournalDocument document = new();
    string? fingerprint;
    public bool Ready { get; private set; }
    public IReadOnlyList<OperationRecord> Records => document.Operations.AsReadOnly();
    public bool HasPending => document.Operations.Any(IsPending);

    public static bool IsPending(OperationRecord record) =>
        (
            record.Outcome is Outcome.Accepted or Outcome.Pending or Outcome.Unknown
            || record.Operation == ScriptOperation.RecycleFile
                && record.RecycleReceipt
                && record.Outcome == Outcome.Partial
        )
        && !(
            record.Operation is ScriptOperation.TogglePause or ScriptOperation.ToggleSuspend
            && record.Code == ReasonCode.StateNotObservable
        );

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Ready = false;
        var bytes = await SafeJsonFiles.ReadAsync(path, ct).ConfigureAwait(false);
        fingerprint = bytes is null ? null : SafeJsonFiles.Hash(bytes);
        if (bytes is not null)
        {
            using var json = JsonDocument.Parse(bytes);
            SafeJsonFiles.UniqueProperties(json.RootElement);
            if (
                !json.RootElement.TryGetProperty("Schema", out var schema)
                || schema.GetInt32() != 1
            )
                throw new UnsupportedSchemaException();
            document =
                JsonSerializer.Deserialize(bytes, ManagerJson.Default.JournalDocument)
                ?? throw new InvalidDataException("Invalid operation journal.");
            if (
                document.Operations is null
                || document.Operations.Count > 1000
                || document.Operations.Any(r =>
                    r is null
                    || r.Id == Guid.Empty
                    || r.ScriptId == Guid.Empty
                    || !Enum.IsDefined(r.Outcome)
                    || !Enum.IsDefined(r.Operation)
                    || !Enum.IsDefined(r.Code)
                    || r.ScriptPath is null
                    || r.InterpreterPath is null
                    || r.ScriptHash is null
                    || r.InterpreterHash is null
                    || r.UserSid is null
                    || r.LoginKey is null
                    || r.ScriptPath.Length > 32760
                    || r.InterpreterPath.Length > 32760
                    || r.UserSid.Length is 0 or > 200
                    || r.LoginKey.Length is 0 or > 300
                )
                || document.Operations.Select(r => r.Id).Distinct().Count()
                    != document.Operations.Count
            )
                throw new InvalidDataException("Invalid operation journal entries.");
        }
        Ready = true;
    }

    public async Task<OperationResult> PutAsync(
        OperationRecord record,
        CancellationToken ct = default
    )
    {
        if (!Ready)
            return new(Outcome.Failed, ReasonCode.PendingOperation);
        var records = document.Operations.Where(r => r.Id != record.Id).ToList();
        records.Add(record);
        var pending = records.Where(IsPending).ToList();
        if (pending.Count > 500)
            return new(Outcome.Failed, ReasonCode.PendingOperation);
        records = pending
            .Concat(
                records.Where(r => !IsPending(r)).OrderByDescending(r => r.RequestedAt).Take(500)
            )
            .ToList();
        var next = document with { Operations = records };
        try
        {
            fingerprint = await SafeJsonFiles
                .ReplaceAsync(
                    path,
                    JsonSerializer.SerializeToUtf8Bytes(next, ManagerJson.Default.JournalDocument),
                    fingerprint,
                    ct
                )
                .ConfigureAwait(false);
            document = next;
            return new(Outcome.Confirmed);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex))
        {
            Ready = false;
            return new(Outcome.Unknown, ReasonCode.StorageFailure, ex.Message);
        }
    }
}
