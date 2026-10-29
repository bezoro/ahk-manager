namespace Bezoro.AhkManager.UpdateHost;

internal sealed record SnapshotDirectory(string RelativePath, string Dacl);

internal sealed record SnapshotFile(string RelativePath, long Bytes, string Sha256, string Dacl);

internal sealed record SnapshotManifest(
    string RootDacl,
    string ParentDacl,
    SnapshotDirectory[] Directories,
    SnapshotFile[] Files
);
