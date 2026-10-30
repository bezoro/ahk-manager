using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckRetainedStorageHandles(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "retained-metadata");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "entry.txt");
        string moved = Path.Combine(directory, "moved.txt");
        File.WriteAllText(source, "Owned metadata binding fixture.");
        using (var metadata = WindowsStoragePath.OpenVerifiedMetadata(source, retainName: true))
        using (var data = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            check(
                Native.GetFileInformationByHandle(metadata, out var entry)
                    && Native.GetFileInformationByHandle(data.SafeFileHandle, out var opened)
                    && entry.Volume == opened.Volume
                    && entry.IndexHigh == opened.IndexHigh
                    && entry.IndexLow == opened.IndexLow,
                "retained no-follow metadata and normal data open bind the same actual file"
            );
            bool blocked = false;
            try
            {
                File.Move(source, moved);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) == 32)
            {
                blocked = true;
            }
            check(
                blocked && File.Exists(source) && !File.Exists(moved),
                "a retained entry cannot be renamed to introduce a same-object link swap"
            );
        }
        File.Move(source, moved);
        check(File.Exists(moved), "closing the retained handles releases their rename protection");

        string longDirectory = Path.Combine(directory, new string('a', 110), new string('b', 110));
        Directory.CreateDirectory(longDirectory);
        string longFile = Path.Combine(longDirectory, new string('c', 40) + ".txt");
        File.WriteAllText(longFile, "Owned long path fixture.");
        WindowsStoragePath.VerifyEntry(longFile);
        check(
            longFile.Length > 260,
            "metadata verification supports validated extended-length paths"
        );
    }

    // Tag and attribute decisions are pure so the Windows storage policy can
    // also be checked on Linux without a cloud provider or filesystem changes.
    static void CheckStoragePathPolicy(Action<bool, string> check)
    {
        const uint directory = 0x10;
        const uint reparse = 0x400;
        const uint cloud = 0x9000001A;
        const uint pinned = 0x80000;
        const uint unpinned = 0x100000;
        const uint unavailable = 0x1000 | 0x400000;

        WindowsStoragePath.ValidateEntry(0x80, 0);
        WindowsStoragePath.ValidateEntry(directory, 0);
        check(true, "ordinary files and directories remain valid storage entries");
        WindowsStoragePath.ValidateEntry(0x40000, 0);
        WindowsStoragePath.ValidateEntry(reparse | 0x40000, cloud);
        check(true, "extended attributes in handle metadata do not imply cloud recall");

        for (uint variant = 0; variant < 16; variant++)
        {
            uint tag = cloud | (variant << 12);
            WindowsStoragePath.ValidateEntry(reparse | pinned, tag);
            WindowsStoragePath.ValidateEntry(reparse | unpinned, tag);
            WindowsStoragePath.ValidateEntry(reparse | directory | unavailable, tag);
            check(true, "hydrated cloud files and cloud directories accept family tag " + variant);

            foreach (uint attribute in new uint[] { 0x1000, 0x400000 })
                StorageRefused(
                    () => WindowsStoragePath.ValidateEntry(reparse | pinned | attribute, tag),
                    StoragePathFailure.CloudFileUnavailable,
                    check,
                    "a pin flag cannot make an unavailable cloud file readable"
                );
        }

        foreach (
            uint tag in new uint[]
            {
                0,
                0xA0000003, // Mount points and junctions.
                0xA000000C, // Symbolic links.
                0x8000001A, // A cloud-like value outside the actual cloud family.
                0x9001001A,
                0xB000001A, // Name-surrogate flag on a cloud-like value.
                0x9000001C, // Projected filesystem entries are not cloud placeholders.
                uint.MaxValue,
            }
        )
        {
            foreach (uint attributes in new[] { reparse, reparse | directory })
                StorageRefused(
                    () => WindowsStoragePath.ValidateEntry(attributes, tag),
                    StoragePathFailure.RedirectedPath,
                    check,
                    "unknown, redirected, and name-surrogate reparse entries remain refused"
                );
        }

        foreach (uint attributes in new[] { 0x40u, 0x40u | reparse | directory })
            StorageRefused(
                () => WindowsStoragePath.ValidateEntry(attributes, cloud),
                StoragePathFailure.RedirectedPath,
                check,
                "device entries remain refused independently of their tag"
            );

        WindowsStoragePath.ValidateFileSystem("NTFS");
        WindowsStoragePath.ValidateFileSystem("ntfs");
        check(true, "the portable volume policy accepts NTFS case-insensitively");
        foreach (string filesystem in new[] { "", "FAT32", "exFAT", "ReFS", "NTFS ", "NTFS\0" })
            StorageRefused(
                () => WindowsStoragePath.ValidateFileSystem(filesystem),
                StoragePathFailure.UnsupportedFileSystem,
                check,
                "unsupported filesystems have a reason distinct from access and package errors"
            );

        var entry = new Native.FileStamp
        {
            Attributes = reparse,
            Volume = 42,
            IndexHigh = 1,
            IndexLow = 2,
            Links = 1,
        };
        FileLease.VerifyIdentity(entry, entry);
        check(true, "metadata and data handles may refer to the same hydrated cloud file");
        foreach (int changedField in Enumerable.Range(0, 6))
        {
            var changed = entry;
            switch (changedField)
            {
                case 0:
                    changed.Volume++;
                    break;
                case 1:
                    changed.IndexHigh++;
                    break;
                case 2:
                    changed.IndexLow++;
                    break;
                case 3:
                    changed.Links++;
                    break;
                case 4:
                    changed.Attributes |= directory;
                    break;
                case 5:
                    changed.Attributes |= 0x40;
                    break;
            }
            PortableRefused(
                () => FileLease.VerifyIdentity(entry, changed),
                check,
                "the data open must retain the verified entry identity and regular file type"
            );
            PortableRefused(
                () => FileLease.VerifyIdentity(changed, entry),
                check,
                "uncertain metadata identity cannot authorize a regular data handle"
            );
        }
    }

    static void StorageRefused(
        Action operation,
        StoragePathFailure expected,
        Action<bool, string> check,
        string description
    )
    {
        bool refused = false;
        try
        {
            operation();
        }
        catch (StoragePathException failure)
        {
            refused = failure.Failure == expected;
        }
        check(refused, description);
    }
}
