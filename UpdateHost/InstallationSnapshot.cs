namespace Bezoro.AhkManager.UpdateHost;

internal sealed record SnapshotFile(string RelativePath, long Bytes, string Sha256);
internal sealed record SnapshotManifest(string[] Directories, SnapshotFile[] Files);

// Every source file stays read-locked until the entire copy has been verified.
// Recovery uses this in-memory manifest, never permissions reconstructed from a
// partially written evidence file after a crash.
internal static class InstallationSnapshot
{
    const int MaximumEntries = 4096;
    const long MaximumFileBytes = 512L * 1024 * 1024;
    const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    sealed record Source(string RelativePath, FileLease Lease);
    sealed class Sources : IDisposable
    {
        internal readonly List<string> Directories = [];
        internal readonly List<Source> Files = [];
        public void Dispose() { foreach (var file in Files) file.Lease.Dispose(); }
    }
    static Sources Pin(string root)
    {
        root = UpdatePaths.Local(root); UpdatePaths.NoRedirects(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        var sources = new Sources(); long total = 0; int count = 0;
        var pending = new Stack<string>(); pending.Push(root);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (pending.Count > 0)
                foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (++count > MaximumEntries) throw new IOException("Installation snapshot exceeds its entry limit.");
                    string relative = UpdatePaths.Relative(root, entry);
                    if (!paths.Add(relative)) throw new IOException("Ambiguous installation path.");
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new IOException("Redirected installation entry refused.");
                    if ((attributes & FileAttributes.Directory) != 0) { sources.Directories.Add(relative); pending.Push(entry); }
                    else
                    {
                        var lease = FileLease.Open(entry, MaximumFileBytes);
                        sources.Files.Add(new(relative, lease)); total = checked(total + lease.Length);
                        if (total > MaximumTotalBytes) throw new IOException("Installation snapshot exceeds its byte limit.");
                    }
                }
            return sources;
        }
        catch { sources.Dispose(); throw; }
    }
    internal static async Task<SnapshotManifest> Copy(string sourceRoot, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Snapshot destination must be absent.");
        UpdatePaths.NoRedirects(destination);
        using var sources = Pin(sourceRoot);
        long required = sources.Files.Sum(file => file.Lease.Length) + 64L * 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < required) throw new IOException("Not enough space to preserve the installation.");
        Directory.CreateDirectory(destination);
        foreach (string directory in sources.Directories.OrderBy(path => path.Length)) Directory.CreateDirectory(Path.Combine(destination, directory));
        var files = new List<SnapshotFile>();
        foreach (var source in sources.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            string hash = source.Lease.Hash(); string outputPath = Path.Combine(destination, source.RelativePath);
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                source.Lease.Stream.Position = 0; await source.Lease.Stream.CopyToAsync(output).ConfigureAwait(false); await output.FlushAsync().ConfigureAwait(false); output.Flush(true);
            }
            File.SetLastWriteTimeUtc(outputPath, File.GetLastWriteTimeUtc(source.Lease.Path));
            File.SetCreationTimeUtc(outputPath, File.GetCreationTimeUtc(source.Lease.Path));
            File.SetAttributes(outputPath, File.GetAttributes(source.Lease.Path) & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed));
            files.Add(new(source.RelativePath, source.Lease.Length, hash));
        }
        var manifest = new SnapshotManifest(sources.Directories.Order(StringComparer.OrdinalIgnoreCase).ToArray(), files.ToArray());
        Verify(destination, manifest); return manifest;
    }
    internal static void Verify(string root, SnapshotManifest manifest)
    {
        using var sources = Pin(root);
        if (!sources.Directories.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(manifest.Directories, StringComparer.OrdinalIgnoreCase) || sources.Files.Count != manifest.Files.Length) throw new IOException("Installation snapshot structure changed.");
        var expected = manifest.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Files)
            if (!expected.TryGetValue(source.RelativePath, out var file) || source.Lease.Length != file.Bytes || source.Lease.Hash() != file.Sha256) throw new IOException("Installation snapshot bytes changed.");
    }
}
