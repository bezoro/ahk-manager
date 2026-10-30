using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bezoro.AhkManager.UpdateHost;

internal static class UpdateProtocol
{
    internal const int MaximumPlanBytes = 64 * 1024;
    internal const string RegistryPath = @"Software\62d1c352-a9c4-556f-981c-2237ce4d0b68";
    internal const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\62d1c352-a9c4-556f-981c-2237ce4d0b68";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = false,
        MaxDepth = 24,
    };

    internal static bool Hash(string? value, bool lowercase = true) =>
        value is { Length: 64 }
        && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' || !lowercase && c is >= 'A' and <= 'F'
        );

    internal static bool NumericVersion(string? value) =>
        value is { Length: > 0 and <= 64 }
        && Regex.IsMatch(
            value,
            @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){0,2}\z",
            RegexOptions.CultureInvariant
        )
        && System.Version.TryParse(value, out _);

    internal static bool ReleaseVersion(string? value)
    {
        if (value is not { Length: > 0 and <= 64 })
            return false;
        var match = Regex.Match(
            value,
            @"\A(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})(?:-test\.([1-9][0-9]{0,9})\.([1-9][0-9]{0,9})\.([1-9][0-9]{0,9}))?\z",
            RegexOptions.CultureInvariant
        );
        return match.Success
            && match
                .Groups.Cast<Group>()
                .Skip(1)
                .Where(group => group.Success)
                .All(group => int.TryParse(group.Value, out _));
    }

    // Windows stores historical stable versions as two to four numeric parts.
    // Test releases always retain their complete SemVer identity.
    internal static bool InstalledVersion(string? value) =>
        NumericVersion(value) || ReleaseVersion(value);

    internal static bool SameVersion(string first, string second)
    {
        if (!InstalledVersion(first) || !InstalledVersion(second))
            return false;
        if (first.Contains('-') || second.Contains('-'))
            return string.Equals(first, second, StringComparison.Ordinal);
        var a = System.Version.Parse(first);
        var b = System.Version.Parse(second);
        return a.Major == b.Major
            && a.Minor == b.Minor
            && Math.Max(a.Build, 0) == Math.Max(b.Build, 0)
            && Math.Max(a.Revision, 0) == Math.Max(b.Revision, 0);
    }

    internal static bool InstalledImageVersionMatches(
        string expected,
        string? productVersion,
        string? fileVersion
    )
    {
        if (productVersion is null || !InstalledVersion(expected))
            return false;
        int suffix = expected.IndexOf('-');
        if (suffix < 0)
            return fileVersion is not null
                && SameVersion(expected, productVersion)
                && SameVersion(expected, fileVersion);
        // Electron's resource editor preserves the full SemVer in FileVersion,
        // while its ProductVersion is the numeric Windows version.
        return string.Equals(expected, fileVersion, StringComparison.Ordinal)
            && SameVersion(expected[..suffix], productVersion);
    }

    internal static T Read<T>(byte[] bytes, int maximumBytes = MaximumPlanBytes)
    {
        if (bytes.Length < 2 || bytes.Length > maximumBytes)
            throw new InvalidDataException("Update document exceeds its size limit.");
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        RejectDuplicates(parsed.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json)
            ?? throw new InvalidDataException("Update document is absent.");
    }

    static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Duplicate update document property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                RejectDuplicates(item);
    }

    internal static void Validate(UpdatePlan plan)
    {
        if (
            !(
                plan.SchemaVersion == 1 && plan.Deployment is null
                || plan.SchemaVersion == 2 && plan.Deployment == "portable"
            )
            || plan.OperationId == Guid.Empty
            || plan.ParentPid <= 0
            || plan.ParentStartedAtUtc.Offset != TimeSpan.Zero
            || plan.ParentStartedAtUtc.ToUnixTimeMilliseconds() <= 0
            || !ReleaseVersion(plan.Version)
            || !Hash(plan.InstallerSha256)
        )
            throw new InvalidDataException("Invalid update protocol or identity.");
        _ = UpdatePaths.Local(plan.InstallDirectory);
        _ = UpdatePaths.Local(plan.InstallerPath);
        if (plan.Fallback is { } fallback)
        {
            if (
                !ReleaseVersion(fallback.Version)
                || !Hash(fallback.InstallerSha256)
                || SameVersion(fallback.Version, plan.Version)
            )
                throw new InvalidDataException("Invalid fallback identity.");
            _ = UpdatePaths.Local(fallback.InstallerPath);
        }
    }

    internal static bool Portable(UpdatePlan plan) =>
        plan.SchemaVersion == 2 && plan.Deployment == "portable";

    internal static void ValidateCommit(UpdatePlan plan, UpdateCommit commit)
    {
        if (
            commit.SchemaVersion != 1
            || commit.OperationId != plan.OperationId
            || commit.ParentPid != plan.ParentPid
            || commit.ParentStartedAtUtc.Offset != TimeSpan.Zero
            || commit.ParentStartedAtUtc != plan.ParentStartedAtUtc
        )
            throw new InvalidDataException(
                "Shutdown commit does not identify this operation and Manager session."
            );
    }

    internal static bool Known(Exception exception) =>
        exception
            is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or JsonException
                or System.Security.SecurityException
                or TimeoutException;
}
