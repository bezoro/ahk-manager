using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed record CachedInstaller(string InstallerPath, string InstallerSha256, string Version);
internal sealed record UpdatePlan(int SchemaVersion, Guid OperationId, int ParentPid, DateTimeOffset ParentStartedAtUtc,
    string InstallDirectory, string InstallerPath, string InstallerSha256, string Version, CachedInstaller? Fallback = null);
internal sealed record UpdateReceipt(int SchemaVersion, int UpdateProtocol, string Version, string Sha256, long? Bytes = null);
internal sealed record UpdateReady(int SchemaVersion, Guid OperationId, int ParentPid, DateTimeOffset ParentStartedAtUtc);
internal sealed record UpdateCancel(int SchemaVersion, Guid OperationId);
internal sealed record UpdateCommit(int SchemaVersion, Guid OperationId, int ParentPid, DateTimeOffset ParentStartedAtUtc);
internal sealed record UpdateOutcome(int SchemaVersion, Guid OperationId, string Outcome, string Detail, string Version,
    int? InstallerExitCode, int? FallbackExitCode, DateTimeOffset CompletedAtUtc,
    string? BackupDirectory = null, string? FailedInstallationDirectory = null);

internal static class UpdateProtocol
{
    internal const int MaximumPlanBytes = 64 * 1024;
    internal const string RegistryPath = @"Software\62d1c352-a9c4-556f-981c-2237ce4d0b68";
    internal const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\62d1c352-a9c4-556f-981c-2237ce4d0b68";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = false,
        MaxDepth = 24
    };
    internal static bool Hash(string? value, bool lowercase = true) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' || !lowercase && c is >= 'A' and <= 'F');
    internal static bool NumericVersion(string? value) => value is { Length: > 0 and <= 64 } && Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){0,2}$", RegexOptions.CultureInvariant) && System.Version.TryParse(value, out _);
    internal static bool SameVersion(string first, string second)
    {
        if (!NumericVersion(first) || !NumericVersion(second)) return false;
        var a = System.Version.Parse(first); var b = System.Version.Parse(second);
        return a.Major == b.Major && a.Minor == b.Minor && Math.Max(a.Build, 0) == Math.Max(b.Build, 0) && Math.Max(a.Revision, 0) == Math.Max(b.Revision, 0);
    }
    internal static T Read<T>(byte[] bytes)
    {
        if (bytes.Length is < 2 or > MaximumPlanBytes) throw new InvalidDataException("Update document exceeds its size limit.");
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        RejectDuplicates(parsed.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Update document is absent.");
    }
    static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate update document property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    internal static void Validate(UpdatePlan plan)
    {
        if (plan.SchemaVersion != 1 || plan.OperationId == Guid.Empty || plan.ParentPid <= 0 || plan.ParentStartedAtUtc.Offset != TimeSpan.Zero || plan.ParentStartedAtUtc.ToUnixTimeMilliseconds() <= 0 || !NumericVersion(plan.Version) || !Hash(plan.InstallerSha256)) throw new InvalidDataException("Invalid update protocol or identity.");
        _ = UpdatePaths.Local(plan.InstallDirectory); _ = UpdatePaths.Local(plan.InstallerPath);
        if (plan.Fallback is { } fallback)
        {
            if (!NumericVersion(fallback.Version) || !Hash(fallback.InstallerSha256) || SameVersion(fallback.Version, plan.Version)) throw new InvalidDataException("Invalid fallback identity.");
            _ = UpdatePaths.Local(fallback.InstallerPath);
        }
    }
    internal static void ValidateCommit(UpdatePlan plan, UpdateCommit commit)
    {
        if (commit.SchemaVersion != 1 || commit.OperationId != plan.OperationId || commit.ParentPid != plan.ParentPid || commit.ParentStartedAtUtc.Offset != TimeSpan.Zero || commit.ParentStartedAtUtc != plan.ParentStartedAtUtc) throw new InvalidDataException("Shutdown commit does not identify this operation and Manager session.");
    }
    internal static bool Known(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or System.Security.SecurityException or TimeoutException;
}

internal static class UpdatePaths
{
    internal static string UpdatesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManagerElectron", "Updates");
    internal static string Local(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32760 || !Path.IsPathFullyQualified(path) || path.Any(c => c < 32 || "\"<>|*?".Contains(c))) throw new InvalidDataException("Absolute local update path required.");
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != Path.DirectorySeparatorChar || full.AsSpan(2).Contains(':')) throw new InvalidDataException("Local drive update path required.");
        full = Path.TrimEndingDirectorySeparator(full);
        if (full.Length > 3 && full[3..].Split(Path.DirectorySeparatorChar).Any(segment => segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || Regex.IsMatch(segment.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) throw new InvalidDataException("Ambiguous Windows update path refused.");
        return full;
    }
    internal static bool Equal(string left, string right) => string.Equals(Local(left), Local(right), StringComparison.OrdinalIgnoreCase);
    internal static void NoRedirects(string path)
    {
        for (string? current = Local(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected update path refused.");
        }
    }
    internal static string OperationDirectory(string updatesRoot, Guid operationId) => Path.Combine(Local(updatesRoot), "operations", operationId.ToString("D"));
    internal static void ValidatePlanLocation(string planPath, UpdatePlan plan, string updatesRoot)
    {
        if (!Equal(planPath, Path.Combine(OperationDirectory(updatesRoot, plan.OperationId), "plan.json"))) throw new InvalidDataException("Plan is outside its operation directory.");
        ValidateInstallerLocation(plan.InstallerPath, plan.Version, updatesRoot);
        if (plan.Fallback is { } fallback) ValidateInstallerLocation(fallback.InstallerPath, fallback.Version, updatesRoot);
    }
    internal static void ValidateInstallerLocation(string path, string version, string updatesRoot)
    {
        if (!UpdateProtocol.NumericVersion(version) || !Equal(path, Path.Combine(Local(updatesRoot), "installers", version, "installer.exe"))) throw new InvalidDataException("Installer is outside its version cache.");
    }
    internal static string ValidateHostLocation(string path, string updatesRoot)
    {
        string full = Local(path); string directory = Path.GetDirectoryName(full)!; string hash = Path.GetFileName(directory);
        if (!UpdateProtocol.Hash(hash) || !Equal(full, Path.Combine(Local(updatesRoot), "host", hash, "AhkManager.UpdateHost.exe"))) throw new InvalidDataException("Update host is outside its immutable cache.");
        return hash;
    }
    internal static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(Local(root), Local(path));
        if (Path.IsPathFullyQualified(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "." or ".." || segment.Length == 0)) throw new IOException("Snapshot path escapes its installation.");
        return relative;
    }
}
