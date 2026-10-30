using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed record PortableFile(string Path, long Bytes, string Sha256);

internal sealed record PortableDescriptor(
    int SchemaVersion,
    string ApplicationId,
    int UpdateProtocol,
    string Version,
    string Commit,
    PortableFile[] Files
);

internal sealed class PortableInventory : IDisposable
{
    internal readonly PortableDescriptor Descriptor;
    internal readonly FileLease Manifest;
    readonly DirectoryLease directories;
    internal readonly Dictionary<string, FileLease> Files = new(StringComparer.OrdinalIgnoreCase);

    internal PortableInventory(string root)
    {
        Manifest = FileLease.Open(
            Path.Combine(root, PortableLayout.Descriptor),
            PortablePayload.MaximumDescriptorBytes
        );
        try
        {
            Descriptor = PortablePayload.ReadDescriptor(
                Manifest.Read(PortablePayload.MaximumDescriptorBytes)
            );
            var declaredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "App",
            };
            foreach (var file in Descriptor.Files)
            {
                int slash = file.Path.LastIndexOf('/');
                while (slash >= 0)
                {
                    declaredDirectories.Add(file.Path[..slash]);
                    slash = file.Path.LastIndexOf('/', slash - 1);
                }
            }
            directories = new DirectoryLease(
                declaredDirectories
                    .Select(path =>
                        Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))
                    )
                    .Append(root)
            );
            foreach (var file in Descriptor.Files)
            {
                var lease = FileLease.Open(
                    Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)),
                    PortablePayload.MaximumFileBytes
                );
                Files.Add(file.Path, lease);
                if (lease.Length != file.Bytes || lease.Hash() != file.Sha256)
                    throw new InvalidDataException("A portable application file changed.");
            }
            var actual = PortablePayload.EnumerateApplication(root, declaredDirectories);
            if (!actual.SetEquals(Files.Keys))
                throw new InvalidDataException(
                    "Portable application contents do not match their inventory."
                );
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var lease in Files.Values)
            lease.Dispose();
        Manifest.Dispose();
        directories?.Dispose();
    }
}

internal static class PortablePayload
{
    internal const long MaximumFileBytes = 512L * 1024 * 1024;
    internal const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumDescriptorBytes = 4 * 1024 * 1024;
    internal const int MaximumFiles = 10000;
    internal static readonly string[] RequiredFiles =
    [
        PortableLayout.Executable,
        "App/" + PortableLayout.Executable,
        "App/backend/AhkManager.Backend.exe",
        "App/backend/update/AhkManager.UpdateHost.exe",
        "App/resources/app.asar",
        "App/resources/app-update.yml",
    ];

    internal static string Relative(string value)
    {
        if (value.Length is < 1 or > 1024 || value.Contains('\\') || value.StartsWith('/'))
            throw new InvalidDataException("A portable package path is invalid.");
        foreach (string part in value.Split('/'))
            if (
                part.Length == 0
                || part is "." or ".."
                || part.EndsWith('.')
                || part.EndsWith(' ')
                || !Regex.IsMatch(part, @"\A[A-Za-z0-9._ ()-]+\z", RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                    part.Split('.')[0],
                    @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
                throw new InvalidDataException("A portable package path is ambiguous.");
        return value;
    }

    internal static PortableDescriptor ReadDescriptor(byte[] bytes)
    {
        var descriptor = UpdateProtocol.Read<PortableDescriptor>(bytes, MaximumDescriptorBytes);
        if (
            descriptor.SchemaVersion != 1
            || descriptor.ApplicationId != PortableLayout.ApplicationId
            || descriptor.UpdateProtocol != 2
            || !UpdateProtocol.ReleaseVersion(descriptor.Version)
            || descriptor.Commit is not { Length: 40 }
            || descriptor.Commit.Any(c => !char.IsAsciiHexDigitLower(c))
            || descriptor.Files is not { Length: > 0 and <= MaximumFiles }
        )
            throw new InvalidDataException("The portable inventory identity is invalid.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        string? previous = null;
        foreach (var file in descriptor.Files)
        {
            if (file is null || file.Path is null)
                throw new InvalidDataException("The portable inventory contains an invalid file.");
            string path = Relative(file.Path);
            if (
                !(
                    path == PortableLayout.Executable
                    || path.StartsWith("App/", StringComparison.Ordinal)
                )
                || file.Bytes < 0
                || file.Bytes > MaximumFileBytes
                || !UpdateProtocol.Hash(file.Sha256)
                || !paths.Add(path)
                || previous is not null && StringComparer.Ordinal.Compare(previous, path) >= 0
            )
                throw new InvalidDataException("The portable inventory contains an invalid file.");
            previous = path;
            total = checked(total + file.Bytes);
            if (total > MaximumTotalBytes)
                throw new InvalidDataException("The portable inventory exceeds its size limit.");
        }
        foreach (string path in paths)
        {
            int slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                if (paths.Contains(path[..slash]))
                    throw new InvalidDataException("Portable files and directories collide.");
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        foreach (string required in RequiredFiles)
            if (!descriptor.Files.Any(file => file.Path == required && file.Bytes > 0))
                throw new InvalidDataException("A required portable application file is missing.");
        return descriptor;
    }

    internal static HashSet<string> EnumerateApplication(
        string root,
        HashSet<string>? declaredDirectories = null
    )
    {
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PortableLayout.Executable,
        };
        var pending = new Stack<string>();
        pending.Push(Path.Combine(root, "App"));
        int count = 0;
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            UpdatePaths.NoRedirects(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > MaximumFiles * 2)
                    throw new InvalidDataException(
                        "The portable application has too many entries."
                    );
                WindowsStoragePath.VerifyEntry(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (
                        declaredDirectories is not null
                        && !declaredDirectories.Contains(
                            Path.GetRelativePath(root, entry).Replace('\\', '/')
                        )
                    )
                        throw new InvalidDataException(
                            "The portable application has an undeclared directory."
                        );
                    pending.Push(entry);
                }
                else if (!actual.Add(Path.GetRelativePath(root, entry).Replace('\\', '/')))
                    throw new InvalidDataException("The portable application has duplicate paths.");
            }
        }
        return actual;
    }

    internal static void VerifyVersion(string application, string version)
    {
        using var file = FileLease.Open(
            Path.Combine(application, PortableLayout.Executable),
            MaximumFileBytes
        );
        var information = FileVersionInfo.GetVersionInfo(file.Path);
        if (
            !UpdateProtocol.InstalledImageVersionMatches(
                version,
                information.ProductVersion,
                information.FileVersion
            )
        )
            throw new InvalidDataException(
                "The portable image version does not match the package."
            );
    }

    internal static async Task<PortableDescriptor> Extract(
        PackageLease package,
        string staging,
        string version,
        Action<string, string>? verifyImage = null
    )
    {
        package.Installer.Stream.Position = 0;
        using var archive = new ZipArchive(
            package.Installer.Stream,
            ZipArchiveMode.Read,
            leaveOpen: true
        );
        var (entries, descriptor, manifest) = ValidateArchive(archive, version);
        long required = descriptor.Files.Sum(file => file.Bytes) + 64L * 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(staging)!).AvailableFreeSpace < required)
            throw new IOException("Not enough space to stage the portable update.");
        SnapshotSecurity.CreatePrivateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, PortableLayout.Descriptor), manifest)
            .ConfigureAwait(false);
        foreach (var file in descriptor.Files)
        {
            string destination = Path.Combine(
                staging,
                file.Path.Replace('/', Path.DirectorySeparatorChar)
            );
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = entries[file.Path].Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.Asynchronous | FileOptions.WriteThrough
            );
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                copied = checked(copied + read);
                if (copied > file.Bytes)
                    throw new InvalidDataException(
                        "A portable archive entry exceeds its declared size."
                    );
                digest.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
            await output.FlushAsync().ConfigureAwait(false);
            output.Flush(true);
            if (
                copied != file.Bytes
                || Convert.ToHexStringLower(digest.GetHashAndReset()) != file.Sha256
            )
                throw new InvalidDataException("A portable archive entry checksum changed.");
        }
        using var verified = new PortableInventory(staging);
        (verifyImage ?? VerifyVersion)(Path.Combine(staging, "App"), version);
        SnapshotSecurity.ProtectOwnedPath(Path.Combine(staging, "App"), true);
        foreach (
            string relative in new[]
            {
                "App/" + PortableLayout.Executable,
                "App/backend/AhkManager.Backend.exe",
                "App/backend/update/AhkManager.UpdateHost.exe",
            }
        )
            SnapshotSecurity.ProtectOwnedPath(
                Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)),
                false
            );
        return descriptor;
    }

    internal static PortableDescriptor ValidateArchive(PackageLease package, string version)
    {
        package.Installer.Stream.Position = 0;
        using var archive = new ZipArchive(
            package.Installer.Stream,
            ZipArchiveMode.Read,
            leaveOpen: true
        );
        return ValidateArchive(archive, version).Descriptor;
    }

    static (
        Dictionary<string, ZipArchiveEntry> Entries,
        PortableDescriptor Descriptor,
        byte[] Manifest
    ) ValidateArchive(ZipArchive archive, string version)
    {
        var entries = ReadEntries(archive);
        if (
            !entries.TryGetValue(PortableLayout.Descriptor, out var manifestEntry)
            || !entries.TryGetValue("portable.json", out var markerEntry)
        )
            throw new InvalidDataException("The portable archive is incomplete.");
        byte[] manifest = ReadEntry(manifestEntry, MaximumDescriptorBytes);
        var descriptor = ReadDescriptor(manifest);
        if (descriptor.Version != version)
            throw new InvalidDataException("The portable archive identifies another version.");
        var marker = UpdateProtocol.Read<PortableConfiguration>(ReadEntry(markerEntry, 4096));
        if (marker != new PortableConfiguration(1, PortableLayout.ApplicationId, "Data"))
            throw new InvalidDataException(
                "Portable packages must contain the default data configuration."
            );
        if (entries.Count != descriptor.Files.Length + 2)
            throw new InvalidDataException("The portable archive contains undeclared files.");
        foreach (var file in descriptor.Files)
            if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Bytes)
                throw new InvalidDataException(
                    "The portable archive inventory does not match its contents."
                );
        return (entries, descriptor, manifest);
    }

    internal static Dictionary<string, ZipArchiveEntry> ReadEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumFiles * 2 + 2)
            throw new InvalidDataException("The portable archive has too many entries.");
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            bool directory = entry.FullName.EndsWith('/');
            string path = Relative(directory ? entry.FullName[..^1] : entry.FullName);
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            uint kind = attributes >> 16 & 0xF000;
            if (
                (attributes & 0x400) != 0
                || kind is not (0 or 0x8000 or 0x4000)
                || directory && kind == 0x8000
                || !directory && kind == 0x4000
                || !all.Add(path)
                || entry.Length < 0
                || entry.Length > MaximumFileBytes
                || directory && entry.Length != 0
            )
                throw new InvalidDataException("A portable archive entry is unsafe.");
            if (directory)
            {
                if (!(path == "App" || path.StartsWith("App/", StringComparison.Ordinal)))
                    throw new InvalidDataException(
                        "The portable archive contains an unexpected directory."
                    );
                continue;
            }
            total = checked(total + entry.Length);
            if (total > MaximumTotalBytes || !files.TryAdd(path, entry))
                throw new InvalidDataException("The portable archive exceeds its size limit.");
        }
        foreach (string path in all)
        {
            int slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                if (files.ContainsKey(path[..slash]))
                    throw new InvalidDataException(
                        "Portable archive files and directories collide."
                    );
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        return files;
    }

    static byte[] ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length is < 2 || entry.Length > maximum)
            throw new InvalidDataException("Portable metadata exceeds its limit.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > maximum)
                throw new InvalidDataException("Portable metadata exceeds its declared size.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length)
            throw new InvalidDataException("Portable metadata is incomplete.");
        return output.ToArray();
    }
}
