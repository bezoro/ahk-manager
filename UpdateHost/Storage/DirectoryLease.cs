using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.UpdateHost;

// Retain namespace identity while validating and protecting a freshly extracted
// portable container. Ancestors are opened first and cannot be renamed while
// its initially inherited shared-directory permissions are being replaced.
internal sealed class DirectoryLease : IDisposable
{
    readonly List<SafeFileHandle> handles = [];
    readonly Dictionary<string, Native.FileStamp> identities = new(
        StringComparer.OrdinalIgnoreCase
    );

    internal DirectoryLease(IEnumerable<string> directories)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
            for (
                string? path = UpdatePaths.Local(directory);
                path is not null;
                path = Path.GetDirectoryName(path)
            )
                paths.Add(path);
        try
        {
            foreach (
                string path in paths
                    .OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            )
            {
                // FILE_LIST_DIRECTORY participates in Windows share accounting.
                // A metadata-only FILE_READ_ATTRIBUTES handle does not prevent
                // renaming its own directory when FILE_SHARE_DELETE is absent.
                var handle = OpenDirectory(@"\\?\" + path, 0x81, 3, 0, 3, 0x02200000, 0);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, "A portable directory could not be retained.");
                }
                handles.Add(handle);
                if (
                    !Native.GetFileInformationByHandle(handle, out var information)
                    || (information.Attributes & 0x410) != 0x10
                )
                    throw new IOException(
                        "A retained portable directory is redirected or changed type."
                    );
                identities.Add(path, information);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal Native.FileStamp Identity(string directory) =>
        identities.TryGetValue(UpdatePaths.Local(directory), out var identity)
            ? identity
            : throw new InvalidOperationException("The portable directory is not retained.");

    public void Dispose()
    {
        foreach (var handle in handles)
            handle.Dispose();
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    static extern SafeFileHandle OpenDirectory(
        string name,
        uint access,
        uint sharing,
        nint security,
        uint disposition,
        uint flags,
        nint template
    );
}
