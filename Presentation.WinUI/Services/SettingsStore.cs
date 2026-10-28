using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Infrastructure;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed record SettingsLoad(SettingsDocument Document, bool ReadOnly, ImmutableArray<Issue> Issues);

public sealed class SettingsStore(string path)
{
    readonly SemaphoreSlim gate = new(1, 1);
    string? fingerprint;
    bool loaded;
    public string FilePath { get; } = System.IO.Path.GetFullPath(path);
    public bool ReadOnly { get; private set; }
    public async Task<bool> VerifyLoadedAsync(CancellationToken ct = default)
    {
        if (!loaded || ReadOnly) return false;
        var bytes = await SafeJsonFiles.ReadAsync(FilePath, ct).ConfigureAwait(false);
        return (bytes is null ? null : SafeJsonFiles.Hash(bytes)) == fingerprint;
    }

    public Task<SettingsLoad> LoadAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ImmutableArray<Issue>.Builder issues = ImmutableArray.CreateBuilder<Issue>();
            try
            {
                var bytes = await SafeJsonFiles.ReadAsync(FilePath, ct).ConfigureAwait(false);
                fingerprint = bytes is null ? null : SafeJsonFiles.Hash(bytes);
                var document = bytes is null ? new() : Parse(bytes);
                loaded = true; ReadOnly = false;
                return new SettingsLoad(document, false, []);
            }
            catch (Exception ex) when (SafeJsonFiles.Known(ex))
            {
                ReadOnly = true; loaded = true;
                issues.Add(new(ex is UnsupportedSchemaException ? ReasonCode.UnsupportedSchema : ReasonCode.InvalidSettings, ex.Message));
                try
                {
                    var backup = await SafeJsonFiles.ReadAsync(FilePath + ".bak", ct).ConfigureAwait(false);
                    if (backup is not null)
                    {
                        var recovered = Parse(backup);
                        issues.Add(new(ReasonCode.RecoveredBackup));
                        return new SettingsLoad(recovered, true, issues.ToImmutable());
                    }
                }
                catch (Exception backupError) when (SafeJsonFiles.Known(backupError)) { issues.Add(new(ReasonCode.InvalidSettings, backupError.Message)); }
                return new SettingsLoad(new(), true, issues.ToImmutable());
            }
        }
        finally { gate.Release(); }
    }, ct);

    public Task<OperationResult> SaveAsync(SettingsDocument document, CancellationToken ct = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!loaded || ReadOnly) return new OperationResult(Outcome.Failed, ReasonCode.ReadOnlySettings);
            Validate(document);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ManagerJson.Default.SettingsDocument);
            fingerprint = await SafeJsonFiles.ReplaceAsync(FilePath, bytes, fingerprint, ct).ConfigureAwait(false);
            return new OperationResult(Outcome.Confirmed);
        }
        catch (ConcurrentWriteException ex) { ReadOnly = true; return new(Outcome.Failed, ReasonCode.ConcurrentSettingsChange, ex.Message); }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { return new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message); }
        finally { gate.Release(); }
    }, ct);

    public Task<OperationResult> RestoreBackupAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var backup = await SafeJsonFiles.ReadAsync(FilePath + ".bak", ct).ConfigureAwait(false) ?? throw new InvalidDataException("Backup absent.");
            _ = Parse(backup);
            var primary = await SafeJsonFiles.ReadAsync(FilePath, ct).ConfigureAwait(false);
            // Preserve the invalid/old primary separately; never replace the valid recovery backup with it.
            if (primary is not null)
                await SafeJsonFiles.WriteNewAsync(FilePath + ".recovery-" + Guid.NewGuid().ToString("N"), primary, ct).ConfigureAwait(false);
            fingerprint = await SafeJsonFiles.ReplaceAsync(FilePath, backup, primary is null ? null : SafeJsonFiles.Hash(primary), ct, false).ConfigureAwait(false);
            loaded = true; ReadOnly = false;
            return new OperationResult(Outcome.Confirmed);
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex)) { return new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message); }
        finally { gate.Release(); }
    }, ct);

    static SettingsDocument Parse(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Settings root must be an object.");
        SafeJsonFiles.UniqueProperties(json.RootElement);
        if (!json.RootElement.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1) throw new UnsupportedSchemaException();
        var document = JsonSerializer.Deserialize(bytes, ManagerJson.Default.SettingsDocument) ?? throw new InvalidDataException("Empty document.");
        // Source-generated construction supplies default(bool) for absent init-only
        // properties. Apply version-compatible defaults explicitly, preserving any
        // false value the user has already saved.
        if (document.Settings is not null && json.RootElement.TryGetProperty("Settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
            document = document with { Settings = document.Settings with
            {
                LaunchManagerAtLogin = settings.TryGetProperty(nameof(ManagerSettings.LaunchManagerAtLogin), out _) ? document.Settings.LaunchManagerAtLogin : true,
                LaunchManagerAsAdministrator = settings.TryGetProperty(nameof(ManagerSettings.LaunchManagerAsAdministrator), out _) ? document.Settings.LaunchManagerAsAdministrator : true
            } };
        Validate(document);
        return document;
    }
    public static void Validate(SettingsDocument document)
    {
        if (document.Schema != 1 || document.Scripts is null || document.Collections is null || document.Settings is null || document.Scripts.Count > 2000 || document.Collections.Count > 200 || document.Scripts.Any(s => s is null) || document.Collections.Any(c => c is null) || document.LastLoginKey is null || document.LastLoginKey.Length > 300) throw new InvalidDataException("Invalid schema or limits.");
        HashSet<Guid> ids = []; HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        var collections = document.Collections.Select(c => c.Id).ToHashSet();
        if (collections.Count != document.Collections.Count || collections.Contains(Guid.Empty) || document.Collections.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 120) || document.Collections.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Collections.Count) throw new InvalidDataException("Invalid collections.");
        foreach (var script in document.Scripts)
            if (script.Id == Guid.Empty || !ids.Add(script.Id) || !paths.Add(ScriptPaths.Lexical(script.Path)) || string.IsNullOrWhiteSpace(script.FriendlyName) || script.FriendlyName.Length > 200 || script.Description is null || script.Description.Length > 4000 || script.ManualHotkeys.IsDefault || script.ManualHotkeys.Length > 100 || script.ManualHotkeys.Any(h => string.IsNullOrWhiteSpace(h) || h.Length > 200) || script.CollectionId.HasValue && !collections.Contains(script.CollectionId.Value)) throw new InvalidDataException("Invalid script metadata.");
        var settings = document.Settings;
        if (!Enum.IsDefined(settings.Theme) || !Enum.IsDefined(settings.CloseBehavior) || settings.EditorArguments.IsDefault || settings.EditorArguments.Length > 30 || settings.EditorArguments.Any(a => a is null || a.Length > 4000 || a.Contains('\0')) || settings.Window is null || settings.Window.Width < 100 || settings.Window.Height < 100 || settings.Window.Width > 32768 || settings.Window.Height > 32768 || !double.IsFinite(settings.Window.Dpi) || settings.Window.Dpi is < 48 or > 960) throw new InvalidDataException("Invalid settings.");
        foreach (var executable in new[] { settings.InterpreterPath, settings.EditorPath })
            if (executable is not null && (!System.IO.Path.IsPathFullyQualified(executable) || executable.Length > 32760 || executable.Contains('"') || executable.Contains('\0'))) throw new InvalidDataException("Invalid executable path.");
    }
}

public sealed class UnsupportedSchemaException() : IOException("Unsupported settings schema.");
public sealed class ConcurrentWriteException() : IOException("Loaded file fingerprint changed.");

internal static class SafeJsonFiles
{
    public const int MaximumBytes = 2_000_000;
    public static bool Known(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException;
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static void SafePath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.AsSpan(2).Contains(':')) throw new IOException("Local absolute path required.");
        for (string? current = path; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected path refused.");
    }
    public static async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        SafePath(path);
        if (!File.Exists(path)) return null;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        if (input.Length > MaximumBytes) throw new InvalidDataException("File exceeds limit.");
        byte[] bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (input.Position != input.Length) throw new InvalidDataException("File changed during read.");
        return bytes;
    }
    public static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
    {
        SafePath(path);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("File exceeds limit.");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(true);
    }
    public static async Task<string> ReplaceAsync(string path, byte[] bytes, string? expectedHash, CancellationToken ct, bool backup = true)
    {
        SafePath(path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await using var lease = new FileStream(path + ".write.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = await ReadAsync(path, ct).ConfigureAwait(false);
        if ((current is null ? null : Hash(current)) != expectedHash) throw new ConcurrentWriteException();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool owned = false;
        try
        {
            await WriteNewAsync(temporary, bytes, ct).ConfigureAwait(false); owned = true;
            ct.ThrowIfCancellationRequested();
            if (current is not null) File.Replace(temporary, path, backup ? path + ".bak" : null);
            else File.Move(temporary, path);
            return Hash(bytes);
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = [];
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }
}
