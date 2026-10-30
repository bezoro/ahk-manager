using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bezoro.AhkManager.Core.Infrastructure;

/// <summary>Resolves storage from the authenticated host and its fixed portable marker.</summary>
public sealed record RuntimeProfileLayout
{
    public const string ApplicationId = "Bezoro.AhkManager.Electron";
    public const int MaximumMarkerBytes = 4096;
    public bool Portable { get; private init; }
    public string HostExecutable { get; private init; } = "";
    public string StartupHostExecutable { get; private init; } = "";
    public string? PortableRoot { get; private init; }
    public string DataRoot { get; private init; } = "";
    public string ProfileRoot { get; private init; } = "";
    public string ChromiumRoot { get; private init; } = "";
    public string UpdatesRoot { get; private init; } = "";
    public string LegacyProfileRoot { get; private init; } = "";

    public static RuntimeProfileLayout ForHost(string hostExecutable) =>
        ForHost(
            hostExecutable,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        );

    internal static RuntimeProfileLayout ForHost(
        string hostExecutable,
        string localAppData,
        string roamingAppData
    )
    {
        string host = Local(hostExecutable);
        string directory = Path.GetDirectoryName(host)!;
        string legacy = Path.Combine(Local(localAppData), "Bezoro", "AhkManagerElectron");
        var installed = new RuntimeProfileLayout
        {
            HostExecutable = host,
            StartupHostExecutable = host,
            DataRoot = legacy,
            ProfileRoot = legacy,
            ChromiumRoot = Path.Combine(
                Local(roamingAppData),
                "Bezoro",
                "AhkManagerElectron",
                "Chromium"
            ),
            UpdatesRoot = Path.Combine(legacy, "Updates"),
            LegacyProfileRoot = legacy,
        };
        if (!Path.GetFileName(directory).Equals("App", StringComparison.OrdinalIgnoreCase))
            return installed;
        string root = Path.GetDirectoryName(directory)!;
        string marker = Path.Combine(root, "portable.json");
        if (!Exists(marker))
        {
            if (Exists(Path.Combine(root, "portable-package.json")))
                throw new InvalidDataException("The portable data settings are missing.");
            return installed;
        }
        NoRedirects(marker);
        WindowsStoragePath.RequireNtfs(root);
        if ((File.GetAttributes(marker) & FileAttributes.Directory) != 0)
            throw new InvalidDataException("The portable configuration is not a regular file.");
        using var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (OperatingSystem.IsWindows())
            WindowsStoragePath.VerifyHandle(input.SafeFileHandle);
        if (
            OperatingSystem.IsWindows()
            && (
                !ProcessNative.GetFileInformationByHandle(input.SafeFileHandle, out var identity)
                || identity.Links != 1
                || (identity.Attributes & (uint)(FileAttributes.Directory | FileAttributes.Device))
                    != 0
            )
        )
            throw new InvalidDataException(
                "The portable configuration has an uncertain file identity."
            );
        if (input.Length is < 2 or > MaximumMarkerBytes)
            throw new InvalidDataException("The portable configuration exceeds its size limit.");
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        using var json = ReadMarker(bytes);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The portable configuration must be an object.");
        foreach (var property in json.RootElement.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException(
                    "The portable configuration contains duplicate fields."
                );
        if (
            !names.SetEquals(["schemaVersion", "applicationId", "dataDirectory"])
            || json.RootElement.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || !json.RootElement.GetProperty("schemaVersion").TryGetInt32(out int schema)
            || schema != 1
            || json.RootElement.GetProperty("applicationId").ValueKind != JsonValueKind.String
            || json.RootElement.GetProperty("applicationId").GetString() != ApplicationId
            || json.RootElement.GetProperty("dataDirectory").ValueKind != JsonValueKind.String
        )
            throw new InvalidDataException("The portable configuration is not supported.");
        string data = ResolveDataDirectory(
            root,
            json.RootElement.GetProperty("dataDirectory").GetString()!
        );
        return installed with
        {
            Portable = true,
            PortableRoot = root,
            StartupHostExecutable = Path.Combine(root, "Bezoro.AhkManager.exe"),
            DataRoot = data,
            ProfileRoot = Path.Combine(data, "Profile"),
            ChromiumRoot = Path.Combine(data, "Chromium"),
            UpdatesRoot = Path.Combine(data, "Updates"),
        };
    }

    static JsonDocument ReadMarker(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The portable configuration could not be read.", error);
        }
    }

    public static string ResolveDataDirectory(string portableRoot, string configured)
    {
        string root = Local(portableRoot);
        if (
            string.IsNullOrWhiteSpace(configured)
            || configured.Length > 32760
            || configured.Contains('/')
            || configured.Any(c => c < 32 || "\"<>|*?".Contains(c))
        )
            throw new InvalidDataException("The portable data location is invalid.");
        string data;
        if (Path.IsPathFullyQualified(configured))
        {
            data = Local(configured);
            if (!ScriptPaths.Equal(configured, data))
                throw new InvalidDataException("The portable data location is not normalized.");
        }
        else
        {
            if (Path.IsPathRooted(configured) || configured.Contains(':'))
                throw new InvalidDataException("A normalized local data location is required.");
            string[] segments = configured.Split('\\');
            ValidateSegments(segments);
            data = Local(Path.Combine([root, .. segments]));
        }
        ValidateDataLocation(root, data);
        return data;
    }

    public static void ValidateDataLocation(string portableRoot, string dataDirectory)
    {
        string root = Local(portableRoot);
        string data = Local(dataDirectory);
        if (ScriptPaths.Equal(root, Path.GetPathRoot(root)))
            throw new InvalidDataException(
                "The portable application needs its own folder, not a drive root."
            );
        string app = Path.Combine(root, "App");
        if (Within(root, data) || Within(data, app))
            throw new InvalidDataException("Portable data cannot overlap the application files.");
        foreach (
            string name in new[]
            {
                "Bezoro.AhkManager.exe",
                "portable.json",
                "portable-package.json",
                "portable-data-move.json",
            }
        )
            if (Within(data, Path.Combine(root, name)))
                throw new InvalidDataException("Portable data cannot replace a package file.");
        if (Within(data, root))
        {
            string first = Path.GetRelativePath(root, data).Split(Path.DirectorySeparatorChar)[0];
            if (
                first.StartsWith(".app-stage-", StringComparison.OrdinalIgnoreCase)
                || first.StartsWith(".app-previous-", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(
                    first,
                    @"\A\.portable-data-(config|move)-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.tmp\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
                throw new InvalidDataException("Portable data cannot occupy a recovery workspace.");
        }
        NoRedirects(data);
        WindowsStoragePath.RequireNtfs(data);
        if (Exists(data) && (File.GetAttributes(data) & FileAttributes.Directory) == 0)
            throw new InvalidDataException("The portable data location is not a directory.");
    }

    internal static bool Within(string path, string root) =>
        ScriptPaths.Equal(path, root)
        || path.StartsWith(
            Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );

    internal static string Local(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(ScriptPaths.LocalPath(path));
        if (
            OperatingSystem.IsWindows()
            && (
                full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\'
            )
        )
            throw new InvalidDataException("A local drive path is required.");
        string remainder = full[(Path.GetPathRoot(full)?.Length ?? 0)..];
        if (remainder.Length > 0)
            ValidateSegments(remainder.Split(Path.DirectorySeparatorChar));
        return full;
    }

    static void ValidateSegments(IEnumerable<string> segments)
    {
        foreach (string segment in segments)
            if (
                segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || segment.Contains(':')
                || Regex.IsMatch(
                    segment.Split('.')[0],
                    @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
                throw new InvalidDataException(
                    "The portable data location contains an invalid name."
                );
    }

    internal static void NoRedirects(string path) => WindowsStoragePath.NoRedirects(path);

    static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
