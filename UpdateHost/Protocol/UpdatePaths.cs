using System.Text.RegularExpressions;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.UpdateHost;

internal static class UpdatePaths
{
    internal static string UpdatesRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bezoro",
            "AhkManagerElectron",
            "Updates"
        );

    internal static string Local(string? path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || path.Length > 32760
            || !Path.IsPathFullyQualified(path)
            || path.Any(c => c < 32 || "\"<>|*?".Contains(c))
        )
            throw new InvalidDataException("Absolute local update path required.");
        string full = Path.GetFullPath(path);
        if (
            full.StartsWith(@"\\", StringComparison.Ordinal)
            || full.Length < 3
            || !char.IsAsciiLetter(full[0])
            || full[1] != ':'
            || full[2] != Path.DirectorySeparatorChar
            || full.AsSpan(2).Contains(':')
        )
            throw new InvalidDataException("Local drive update path required.");
        full = Path.TrimEndingDirectorySeparator(full);
        if (
            full.Length > 3
            && full[3..]
                .Split(Path.DirectorySeparatorChar)
                .Any(segment =>
                    segment.Length == 0
                    || segment.EndsWith('.')
                    || segment.EndsWith(' ')
                    || Regex.IsMatch(
                        segment.Split('.')[0],
                        @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    )
                )
        )
            throw new InvalidDataException("Ambiguous Windows update path refused.");
        return full;
    }

    internal static bool Equal(string left, string right) =>
        string.Equals(Local(left), Local(right), StringComparison.OrdinalIgnoreCase);

    internal static void NoRedirects(string path) => WindowsStoragePath.NoRedirects(Local(path));

    internal static string OperationDirectory(string updatesRoot, Guid operationId) =>
        Path.Combine(Local(updatesRoot), "operations", operationId.ToString("D"));

    internal static void ValidatePlanLocation(string planPath, UpdatePlan plan, string updatesRoot)
    {
        if (
            !Equal(
                planPath,
                Path.Combine(OperationDirectory(updatesRoot, plan.OperationId), "plan.json")
            )
        )
            throw new InvalidDataException("Plan is outside its operation directory.");
        ValidateInstallerLocation(
            plan.InstallerPath,
            plan.Version,
            updatesRoot,
            UpdateProtocol.Portable(plan)
        );
        if (plan.Fallback is { } fallback)
            ValidateInstallerLocation(
                fallback.InstallerPath,
                fallback.Version,
                updatesRoot,
                UpdateProtocol.Portable(plan)
            );
    }

    internal static void ValidateInstallerLocation(
        string path,
        string version,
        string updatesRoot,
        bool portable = false
    )
    {
        if (
            !UpdateProtocol.ReleaseVersion(version)
            || !Equal(
                path,
                Path.Combine(
                    Local(updatesRoot),
                    "installers",
                    version,
                    portable ? "archive.zip" : "installer.exe"
                )
            )
        )
            throw new InvalidDataException("Installer is outside its version cache.");
    }

    internal static string ValidateHostLocation(string path, string updatesRoot)
    {
        string full = Local(path);
        string directory = Path.GetDirectoryName(full)!;
        string hash = Path.GetFileName(directory);
        if (
            !UpdateProtocol.Hash(hash)
            || !Equal(
                full,
                Path.Combine(Local(updatesRoot), "host", hash, "AhkManager.UpdateHost.exe")
            )
        )
            throw new InvalidDataException("Update host is outside its immutable cache.");
        return hash;
    }

    internal static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(Local(root), Local(path));
        if (
            Path.IsPathFullyQualified(relative)
            || relative
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "." or ".." || segment.Length == 0)
        )
            throw new IOException("Snapshot path escapes its installation.");
        return relative;
    }
}
