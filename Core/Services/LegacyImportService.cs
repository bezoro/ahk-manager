using System.Collections.Immutable;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed record LegacyImport(
    ImportPreview Preview,
    ImmutableArray<ScriptRecord> Scripts,
    byte[] SourceBytes
);

public sealed class LegacyImportService(
    string destinationRoot,
    IEffectOwner owner,
    string? legacyAuthorityDirectory = null
)
{
    public Task<LegacyImport> ReadAsync(string sourcePath, CancellationToken ct = default) =>
        Task.Run(
            async () =>
            {
                sourcePath = ScriptPaths.LocalPath(sourcePath);
                var bytes =
                    await SafeJsonFiles.ReadAsync(sourcePath, ct).ConfigureAwait(false)
                    ?? throw new FileNotFoundException();
                using var json = JsonDocument.Parse(bytes);
                var root = json.RootElement;
                SafeJsonFiles.UniqueProperties(root);
                if (
                    root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("Schema", out var schema)
                    || !schema.TryGetInt32(out int version)
                    || version is < 1 or > 5
                    || !root.TryGetProperty("Scripts", out var rows)
                    || rows.ValueKind != JsonValueKind.Array
                    || rows.GetArrayLength() > 2000
                )
                    throw new InvalidDataException("Unsupported legacy catalog.");
                if (
                    version >= 4
                    && (
                        !root.TryGetProperty("Authority", out var requiredAuthority)
                        || requiredAuthority.ValueKind != JsonValueKind.Object
                    )
                )
                    throw new InvalidDataException("Explicit legacy authority required.");
                string hash = SafeJsonFiles.Hash(bytes);
                bool authoritySafe = await AuthoritySafeAsync(sourcePath, root, ct)
                    .ConfigureAwait(false);
                bool migrationPending =
                    root.TryGetProperty("Migration", out var migration)
                    && migration.TryGetProperty("State", out var state)
                    && state.ValueKind == JsonValueKind.Number
                    && state.GetInt32() is 1 or 2;
                HashSet<Guid> ids = [];
                HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
                var scripts = ImmutableArray.CreateBuilder<ScriptRecord>();
                var issues = ImmutableArray.CreateBuilder<Issue>();
                foreach (var row in rows.EnumerateArray())
                {
                    if (
                        row.ValueKind != JsonValueKind.Object
                        || !row.TryGetProperty("Id", out var identifier)
                        || !identifier.TryGetGuid(out Guid id)
                        || id == Guid.Empty
                        || !ids.Add(id)
                    )
                        throw new InvalidDataException("Invalid legacy identity.");
                    string path = ScriptPaths.Lexical(RequiredString(row, "Path"));
                    if (!paths.Add(path))
                        throw new InvalidDataException("Duplicate legacy path.");
                    int mode =
                        row.TryGetProperty("ManagementMode", out var modeValue)
                        && modeValue.TryGetInt32(out int parsedMode)
                            ? parsedMode
                            : 0;
                    if (mode is < 0 or > 3)
                        throw new InvalidDataException("Invalid management mode.");
                    bool guard =
                        !authoritySafe
                        || migrationPending
                        || mode is 2 or 3
                        || Path.GetFileName(path)
                            .Equals("startup.ahk", StringComparison.OrdinalIgnoreCase);
                    bool? startup =
                        version == 1
                            ? null
                            : NullableBool(
                                row,
                                version == 5 ? "StartWithWindows" : "RequestedEnabled"
                            );
                    if (version < 3 && row.TryGetProperty("RunAsAdministrator", out _))
                        throw new InvalidDataException(
                            "Administrator preference is unsupported by this legacy schema."
                        );
                    if (
                        version >= 3
                        && (
                            !row.TryGetProperty("RunAsAdministrator", out var adminValue)
                            || adminValue.ValueKind
                                is not (JsonValueKind.True or JsonValueKind.False)
                        )
                    )
                        throw new InvalidDataException(
                            "Explicit boolean administrator preference required."
                        );
                    bool admin = version >= 3 && NullableBool(row, "RunAsAdministrator") == true;
                    string description =
                        row.TryGetProperty("Description", out var desc)
                        && desc.ValueKind == JsonValueKind.String
                            ? desc.GetString()!
                            : "";
                    if (description.Length > 4000)
                        throw new InvalidDataException("Legacy description exceeds limit.");
                    string name =
                        row.TryGetProperty("FriendlyName", out var friendly)
                        && friendly.ValueKind == JsonValueKind.String
                            ? friendly.GetString()!
                            : Path.GetFileNameWithoutExtension(path);
                    var record = new ScriptRecord
                    {
                        Id = id,
                        Path = path,
                        FriendlyName = name,
                        Description = description,
                        RunAtLogin = startup,
                        RunAsAdministrator = admin,
                        LaunchBlocked = NullableBool(row, "LaunchBlocked") == true,
                        LegacyGuard = guard,
                        LegacySourcePath = sourcePath,
                        LegacySourceHash = hash,
                        InterpreterPath =
                            row.TryGetProperty("Interpreter", out var executable)
                            && executable.ValueKind == JsonValueKind.String
                            && executable.GetString()?.Length > 0
                                ? executable.GetString()
                                : null,
                    };
                    scripts.Add(record);
                    if (guard)
                        issues.Add(new(ReasonCode.LegacyGuard, null, id));
                }
                var preview = new ImportPreview(
                    sourcePath,
                    hash,
                    scripts.Count,
                    issues.ToImmutable(),
                    owner.Held && owner.Trusted
                );
                return new LegacyImport(preview, scripts.ToImmutable(), bytes);
            },
            ct
        );

    public async Task<OperationResult> PreserveSourceAsync(
        LegacyImport import,
        CancellationToken ct
    )
    {
        if (!owner.Held || !owner.Trusted)
            return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        var current = await SafeJsonFiles
            .ReadAsync(import.Preview.SourcePath, ct)
            .ConfigureAwait(false);
        if (current is null || SafeJsonFiles.Hash(current) != import.Preview.SourceHash)
            return new(Outcome.Failed, ReasonCode.ImportSourceChanged);
        string folder = Path.Combine(destinationRoot, "imports");
        SafeJsonFiles.SafePath(folder);
        Directory.CreateDirectory(folder);
        string backup = Path.Combine(folder, import.Preview.SourceHash + ".json");
        if (!File.Exists(backup))
            await SafeJsonFiles.WriteNewAsync(backup, import.SourceBytes, ct).ConfigureAwait(false);
        else if (
            SafeJsonFiles.Hash((await SafeJsonFiles.ReadAsync(backup, ct).ConfigureAwait(false))!)
            != import.Preview.SourceHash
        )
            return new(Outcome.Failed, ReasonCode.StorageFailure);
        return new(Outcome.Confirmed);
    }

    public async Task<bool> GuardClearedAsync(ScriptRecord script, CancellationToken ct)
    {
        if (
            !owner.Held
            || !owner.Trusted
            || script.LegacySourcePath is null
            || script.LegacySourceHash is null
        )
            return false;
        var import = await ReadAsync(script.LegacySourcePath, ct).ConfigureAwait(false);
        return import.Preview.SourceHash == script.LegacySourceHash
            && import.Scripts.Any(s => ScriptPaths.Equal(s.Path, script.Path) && !s.LegacyGuard);
    }

    async Task<bool> AuthoritySafeAsync(string source, JsonElement root, CancellationToken ct)
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetDirectoryName(source)!, "launch-requests.json"),
            legacyAuthorityDirectory is null
                ? null
                : Path.Combine(legacyAuthorityDirectory, "launch-requests.json"),
        }
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!root.TryGetProperty("Authority", out var authority))
        {
            foreach (var ledger in candidates)
            {
                var bytes = await SafeJsonFiles.ReadAsync(ledger, ct).ConfigureAwait(false);
                if (bytes is null)
                    continue;
                using var pending = JsonDocument.Parse(bytes);
                SafeJsonFiles.UniqueProperties(pending.RootElement);
                if (
                    !pending.RootElement.TryGetProperty("Requests", out var requests)
                    || requests.ValueKind != JsonValueKind.Array
                    || requests.GetArrayLength() != 0
                )
                    return false;
            }
            return true;
        }
        if (
            authority.ValueKind != JsonValueKind.Object
            || !authority.TryGetProperty("Sid", out var sid)
            || sid.GetString() != owner.Sid
            || !authority.TryGetProperty("Fixture", out var fixture)
            || fixture.ValueKind != JsonValueKind.False
            || !authority.TryGetProperty("LedgerId", out var ledgerId)
            || !ledgerId.TryGetGuid(out Guid id)
        )
            return false;
        bool found = false;
        foreach (var ledger in candidates)
        {
            var bytes = await SafeJsonFiles.ReadAsync(ledger, ct).ConfigureAwait(false);
            if (bytes is null)
                continue;
            found = true;
            using var parsed = JsonDocument.Parse(bytes);
            SafeJsonFiles.UniqueProperties(parsed.RootElement);
            var document = parsed.RootElement;
            if (
                !document.TryGetProperty("Schema", out var version)
                || version.GetInt32() != 2
                || !document.TryGetProperty("Binding", out var binding)
                || !binding.TryGetProperty("LedgerId", out var bindingId)
                || bindingId.GetGuid() != id
                || !binding.TryGetProperty("Sid", out var bindingSid)
                || bindingSid.GetString() != owner.Sid
                || !binding.TryGetProperty("Fixture", out var bindingFixture)
                || bindingFixture.ValueKind != JsonValueKind.False
                || !document.TryGetProperty("Requests", out var requests)
                || requests.ValueKind != JsonValueKind.Array
                || requests.GetArrayLength() != 0
            )
                return false;
            var anchorBytes = await SafeJsonFiles
                .ReadAsync(
                    Path.Combine(Path.GetDirectoryName(ledger)!, "launch-authority.anchor.json"),
                    ct
                )
                .ConfigureAwait(false);
            if (anchorBytes is null)
                return false;
            using var anchor = JsonDocument.Parse(anchorBytes);
            SafeJsonFiles.UniqueProperties(anchor.RootElement);
            if (
                !(
                    anchor.RootElement.TryGetProperty("LedgerId", out var anchorId)
                    && anchorId.GetGuid() == id
                    && anchor.RootElement.TryGetProperty("Sid", out var anchorSid)
                    && anchorSid.GetString() == owner.Sid
                    && anchor.RootElement.TryGetProperty("Fixture", out var anchorFixture)
                    && anchorFixture.ValueKind == JsonValueKind.False
                )
            )
                return false;
        }
        return found;
    }

    static string RequiredString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException("Required legacy field absent.");

    static bool? NullableBool(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Invalid legacy boolean."),
        };
    }
}
