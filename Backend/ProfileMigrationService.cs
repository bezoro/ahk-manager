using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

public sealed record MigrationFile(string Name, string? Hash, int Bytes);

public sealed record MigrationReceipt(
    string SourceRoot,
    string SourceFingerprint,
    DateTimeOffset CreatedAt,
    ImmutableArray<MigrationFile> Files
);

internal sealed class ProfileMigrationService(string destination, Func<bool> effectsAllowed)
{
    static readonly string[] Names =
    [
        "settings.json",
        "settings.json.bak",
        "operations.json",
        "operations.json.bak",
        "activity.jsonl",
        "activity.jsonl.1",
        "activity.jsonl.2",
        "activity.jsonl.3",
    ];
    readonly Dictionary<Guid, (MigrationPreview Preview, DateTimeOffset Created)> previews = [];

    public async Task<MigrationPreview> PreviewAsync(string source, CancellationToken ct)
    {
        Prune();
        Guid id = Guid.NewGuid();
        string root = ScriptPaths.LocalPath(source);
        if (previews.Count >= Protocol.MaximumPending)
            throw new ProtocolException("QueueFull");
        try
        {
            SafeJsonFiles.SafePath(root);
            SafeJsonFiles.SafePath(destination);
            if (
                !Directory.Exists(root)
                || ScriptPaths.Equal(
                    root.TrimEnd(Path.DirectorySeparatorChar),
                    destination.TrimEnd(Path.DirectorySeparatorChar)
                )
            )
                throw new InvalidDataException("Different source profile required.");
            var files = await ReadFiles(root, ct).ConfigureAwait(false);
            if (files[0].Hash is null)
                throw new InvalidDataException("Source settings absent.");
            var state = await new SettingsStore(Path.Combine(root, "settings.json"))
                .LoadAsync(ct)
                .ConfigureAwait(false);
            if (state.ReadOnly)
                throw new InvalidDataException("Source settings require explicit recovery.");
            var journal = new OperationJournal(Path.Combine(root, "operations.json"));
            await journal.LoadAsync(ct).ConfigureAwait(false);
            foreach (
                var file in files.Where(f =>
                    f.Hash is not null
                    && f.Name.StartsWith("activity.jsonl", StringComparison.Ordinal)
                )
            )
            {
                var bytes =
                    await SafeJsonFiles
                        .ReadAsync(Path.Combine(root, file.Name), ct)
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException();
                foreach (
                    string line in AutoHotkeyDiscovery
                        .Decode(bytes)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                )
                {
                    using var json = JsonDocument.Parse(line);
                    SafeJsonFiles.UniqueProperties(json.RootElement);
                    _ =
                        JsonSerializer.Deserialize(line, ManagerJson.Default.ActivitySnapshot)
                        ?? throw new InvalidDataException("Invalid activity.");
                }
            }
            string fingerprint = Fingerprint(files);
            bool empty = DestinationEmpty(),
                allowed = effectsAllowed(),
                eligible = empty && allowed;
            var preview = new MigrationPreview(
                id,
                root,
                fingerprint,
                files.Count(f => f.Hash is not null),
                journal.Records.Count(OperationJournal.IsPending),
                eligible,
                eligible
                    ? []
                    :
                    [
                        new(
                            !allowed ? ReasonCode.EffectOwnerUnavailable : ReasonCode.ImportInvalid,
                            !allowed
                                ? "Effect owner unavailable."
                                : "Destination profile is not empty."
                        ),
                    ]
            );
            previews.Add(id, (preview, DateTimeOffset.UtcNow));
            return preview;
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException)
        {
            return new(id, root, "", 0, 0, false, [new(ReasonCode.ImportInvalid, ex.Message)]);
        }
    }

    public async Task<OperationResult> CommitAsync(Guid id, bool confirmed, CancellationToken ct)
    {
        Prune();
        if (!previews.Remove(id, out var stored))
            return new(Outcome.Failed, ReasonCode.ImportSourceChanged);
        if (!confirmed)
            return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
        if (!effectsAllowed())
            return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (!stored.Preview.CanMigrate || !DestinationEmpty())
            return new(Outcome.Failed, ReasonCode.ImportInvalid);
        string stage = destination + ".migration-" + Guid.NewGuid().ToString("N");
        bool removedEmpty = false;
        try
        {
            var files = await ReadFiles(stored.Preview.SourceRoot, ct).ConfigureAwait(false);
            if (Fingerprint(files) != stored.Preview.SourceFingerprint)
                return new(Outcome.Failed, ReasonCode.ImportSourceChanged);
            SafeJsonFiles.SafePath(stage);
            Directory.CreateDirectory(stage);
            string backup = Path.Combine(stage, "migration-backup", id.ToString("N"));
            Directory.CreateDirectory(backup);
            foreach (var file in files.Where(f => f.Hash is not null))
            {
                ct.ThrowIfCancellationRequested();
                var bytes =
                    await SafeJsonFiles
                        .ReadAsync(Path.Combine(stored.Preview.SourceRoot, file.Name), ct)
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException("Source changed.");
                if (SafeJsonFiles.Hash(bytes) != file.Hash)
                    return new(Outcome.Failed, ReasonCode.ImportSourceChanged);
                await SafeJsonFiles
                    .WriteNewAsync(Path.Combine(backup, file.Name), bytes, ct)
                    .ConfigureAwait(false);
                await SafeJsonFiles
                    .WriteNewAsync(Path.Combine(stage, file.Name), bytes, ct)
                    .ConfigureAwait(false);
            }
            var receipt = new MigrationReceipt(
                stored.Preview.SourceRoot,
                stored.Preview.SourceFingerprint,
                DateTimeOffset.UtcNow,
                files
            );
            await SafeJsonFiles
                .WriteNewAsync(
                    Path.Combine(backup, "manifest.json"),
                    JsonSerializer.SerializeToUtf8Bytes(receipt, Protocol.Json.MigrationReceipt),
                    ct
                )
                .ConfigureAwait(false);
            if (
                Fingerprint(await ReadFiles(stored.Preview.SourceRoot, ct).ConfigureAwait(false))
                != stored.Preview.SourceFingerprint
            )
                return new(Outcome.Failed, ReasonCode.ImportSourceChanged);
            if (!effectsAllowed())
                return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
            if (!DestinationEmpty())
                return new(Outcome.Failed, ReasonCode.ConcurrentSettingsChange);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, false);
                removedEmpty = true;
            }
            Directory.Move(stage, destination);
            return new(Outcome.Confirmed);
        }
        catch (OperationCanceledException)
        {
            return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException)
        {
            return new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message);
        }
        finally
        {
            if (removedEmpty && !Directory.Exists(destination))
                Directory.CreateDirectory(destination);
        }
    }

    bool DestinationEmpty()
    {
        SafeJsonFiles.SafePath(destination);
        return !Directory.Exists(destination)
            || !Directory.EnumerateFileSystemEntries(destination).Any();
    }

    static async Task<ImmutableArray<MigrationFile>> ReadFiles(string root, CancellationToken ct)
    {
        var result = ImmutableArray.CreateBuilder<MigrationFile>();
        foreach (string name in Names)
        {
            var bytes = await SafeJsonFiles
                .ReadAsync(Path.Combine(root, name), ct)
                .ConfigureAwait(false);
            result.Add(
                new(name, bytes is null ? null : SafeJsonFiles.Hash(bytes), bytes?.Length ?? 0)
            );
        }
        return result.ToImmutable();
    }

    static string Fingerprint(ImmutableArray<MigrationFile> files) =>
        Convert
            .ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join('\n', files.Select(f => f.Name + ":" + f.Hash + ":" + f.Bytes))
                    )
                )
            )
            .ToLowerInvariant();

    void Prune()
    {
        foreach (
            var id in previews
                .Where(p => DateTimeOffset.UtcNow - p.Value.Created > Protocol.TokenLifetime)
                .Select(p => p.Key)
                .ToArray()
        )
            previews.Remove(id);
    }
}
