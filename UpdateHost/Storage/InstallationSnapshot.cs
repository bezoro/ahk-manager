namespace Bezoro.AhkManager.UpdateHost;

// Every source file stays read-locked until the entire copy has been verified.
// Recovery uses this in-memory manifest, never permissions reconstructed from a
// partially written evidence file after a crash.
internal static class InstallationSnapshot
{
    const int MaximumEntries = 4096;
    const long MaximumFileBytes = 512L * 1024 * 1024;
    const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;

    sealed record Source(string RelativePath, FileLease Lease, string Dacl);

    sealed class Sources : IDisposable
    {
        internal string RootDacl = "",
            ParentDacl = "";
        internal readonly List<SnapshotDirectory> Directories = [];
        internal readonly List<Source> Files = [];

        public void Dispose()
        {
            foreach (var file in Files)
                file.Lease.Dispose();
        }
    }

    static Sources Pin(string root, int maximumEntries = MaximumEntries)
    {
        root = UpdatePaths.Local(root);
        UpdatePaths.NoRedirects(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException();
        var sources = new Sources
        {
            RootDacl = SnapshotSecurity.Read(root, true),
            ParentDacl = SnapshotSecurity.Read(
                Path.GetDirectoryName(root)
                    ?? throw new IOException("Installation requires a parent directory."),
                true
            ),
        };
        long total = 0;
        int count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (pending.Count > 0)
                foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (++count > maximumEntries)
                        throw new IOException("Installation snapshot exceeds its entry limit.");
                    string relative = UpdatePaths.Relative(root, entry);
                    if (!paths.Add(relative))
                        throw new IOException("Ambiguous installation path.");
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new IOException("Redirected installation entry refused.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        sources.Directories.Add(new(relative, SnapshotSecurity.Read(entry, true)));
                        pending.Push(entry);
                    }
                    else
                    {
                        string dacl = SnapshotSecurity.Read(entry, false);
                        var lease = FileLease.Open(entry, MaximumFileBytes);
                        sources.Files.Add(new(relative, lease, dacl));
                        total = checked(total + lease.Length);
                        if (total > MaximumTotalBytes)
                            throw new IOException("Installation snapshot exceeds its byte limit.");
                    }
                }

            return sources;
        }
        catch
        {
            sources.Dispose();
            throw;
        }
    }

    internal static async Task<SnapshotManifest> Copy(
        string sourceRoot,
        string destination,
        int maximumEntries = MaximumEntries
    )
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Snapshot destination must be absent.");
        UpdatePaths.NoRedirects(destination);
        using var sources = Pin(sourceRoot, maximumEntries);
        long required = sources.Files.Sum(file => file.Lease.Length) + 64L * 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < required)
            throw new IOException("Not enough space to preserve the installation.");
        SnapshotSecurity.CreatePrivateDirectory(destination);
        foreach (
            var directory in sources.Directories.OrderBy(directory => directory.RelativePath.Length)
        )
            Directory.CreateDirectory(Path.Combine(destination, directory.RelativePath));
        var files = new List<SnapshotFile>();
        foreach (
            var source in sources.Files.OrderBy(
                file => file.RelativePath,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            string hash = source.Lease.Hash();
            string outputPath = Path.Combine(destination, source.RelativePath);
            await using (
                var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.Asynchronous | FileOptions.WriteThrough
                )
            )
            {
                source.Lease.Stream.Position = 0;
                await source.Lease.Stream.CopyToAsync(output).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(true);
            }

            File.SetLastWriteTimeUtc(outputPath, File.GetLastWriteTimeUtc(source.Lease.Path));
            File.SetCreationTimeUtc(outputPath, File.GetCreationTimeUtc(source.Lease.Path));
            File.SetAttributes(
                outputPath,
                File.GetAttributes(source.Lease.Path)
                    & (
                        FileAttributes.ReadOnly
                        | FileAttributes.Hidden
                        | FileAttributes.System
                        | FileAttributes.Archive
                        | FileAttributes.NotContentIndexed
                    )
            );
            files.Add(new(source.RelativePath, source.Lease.Length, hash, source.Dacl));
        }

        var manifest = new SnapshotManifest(
            sources.RootDacl,
            sources.ParentDacl,
            sources
                .Directories.OrderBy(
                    directory => directory.RelativePath,
                    StringComparer.OrdinalIgnoreCase
                )
                .ToArray(),
            files.ToArray()
        );
        Verify(destination, manifest, maximumEntries);
        VerifySecurity(sourceRoot, manifest);
        VerifyPrivate(destination, manifest);
        return manifest;
    }

    internal static void Verify(
        string root,
        SnapshotManifest manifest,
        int maximumEntries = MaximumEntries
    )
    {
        using var sources = Pin(root, maximumEntries);
        if (
            !sources
                .Directories.Select(directory => directory.RelativePath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(
                    manifest.Directories.Select(directory => directory.RelativePath),
                    StringComparer.OrdinalIgnoreCase
                )
            || sources.Files.Count != manifest.Files.Length
        )
            throw new IOException("Installation snapshot structure changed.");
        var expected = manifest.Files.ToDictionary(
            file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var source in sources.Files)
            if (
                !expected.TryGetValue(source.RelativePath, out var file)
                || source.Lease.Length != file.Bytes
                || source.Lease.Hash() != file.Sha256
            )
                throw new IOException("Installation snapshot bytes changed.");
    }

    internal static void VerifyPrivate(string root, SnapshotManifest manifest)
    {
        SnapshotSecurity.VerifyPrivate(root, true, true);
        foreach (var directory in manifest.Directories)
            SnapshotSecurity.VerifyPrivate(Path.Combine(root, directory.RelativePath), true);
        foreach (var file in manifest.Files)
            SnapshotSecurity.VerifyPrivate(Path.Combine(root, file.RelativePath), false);
    }

    internal static void VerifySecurity(string root, SnapshotManifest manifest)
    {
        if (
            !SnapshotSecurity.Equivalent(
                manifest.ParentDacl,
                SnapshotSecurity.Read(Path.GetDirectoryName(root)!, true)
            )
        )
            throw new IOException("Installation parent permissions changed.");
        if (!SnapshotSecurity.Equivalent(manifest.RootDacl, SnapshotSecurity.Read(root, true)))
            throw new IOException("Installation root permissions changed.");
        foreach (var directory in manifest.Directories)
            if (
                !SnapshotSecurity.Equivalent(
                    directory.Dacl,
                    SnapshotSecurity.Read(Path.Combine(root, directory.RelativePath), true)
                )
            )
                throw new IOException("Installation directory permissions changed.");
        foreach (var file in manifest.Files)
            if (
                !SnapshotSecurity.Equivalent(
                    file.Dacl,
                    SnapshotSecurity.Read(Path.Combine(root, file.RelativePath), false)
                )
            )
                throw new IOException("Installation file permissions changed.");
    }

    internal static void RestoreSecurity(
        string staging,
        SnapshotManifest manifest,
        int maximumEntries = MaximumEntries
    )
    {
        UpdatePaths.NoRedirects(staging);
        if (
            !SnapshotSecurity.Equivalent(
                manifest.ParentDacl,
                SnapshotSecurity.Read(Path.GetDirectoryName(staging)!, true)
            )
        )
            throw new IOException("Installation parent permissions changed before recovery.");
        Verify(staging, manifest, maximumEntries);
        VerifyPrivate(staging, manifest);
        // Protect each original DACL while the root is still private. Opening
        // the root cannot propagate its permissions into a formerly protected
        // child. Then restore the original inheritance flags top to bottom.
        foreach (var file in manifest.Files)
            SnapshotSecurity.Write(
                Path.Combine(staging, file.RelativePath),
                false,
                file.Dacl,
                true
            );
        foreach (
            var directory in manifest.Directories.OrderByDescending(directory =>
                directory.RelativePath.Length
            )
        )
            SnapshotSecurity.Write(
                Path.Combine(staging, directory.RelativePath),
                true,
                directory.Dacl,
                true
            );
        SnapshotSecurity.Write(staging, true, manifest.RootDacl, true);
        SnapshotSecurity.Write(staging, true, manifest.RootDacl);
        foreach (
            var directory in manifest.Directories.OrderBy(directory =>
                directory.RelativePath.Length
            )
        )
            SnapshotSecurity.Write(
                Path.Combine(staging, directory.RelativePath),
                true,
                directory.Dacl
            );
        foreach (var file in manifest.Files)
            SnapshotSecurity.Write(Path.Combine(staging, file.RelativePath), false, file.Dacl);
        VerifySecurity(staging, manifest);
    }
}
