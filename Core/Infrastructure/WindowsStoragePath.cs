using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Core.Infrastructure;

internal enum StoragePathFailure
{
    UnsupportedFileSystem,
    RedirectedPath,
    CloudFileUnavailable,
}

internal sealed class StoragePathException(StoragePathFailure failure, string? message = null)
    : IOException(message ?? "The storage path is not supported.")
{
    internal StoragePathFailure Failure { get; } = failure;
}

/// <summary>
/// Accepts local Cloud Files metadata without accepting namespace redirection.
/// Source-linked into UpdateHost so profile and update safety use the same policy.
/// </summary>
internal static class WindowsStoragePath
{
    const uint Directory = 0x10;
    const uint Device = 0x40;
    const uint ReparsePoint = 0x400;

    // 0x40000 is FILE_ATTRIBUTE_EA in handle metadata. Its RECALL_ON_OPEN
    // alias belongs to directory-enumeration information classes only.
    const uint Unavailable = 0x1000 | 0x400000;
    const uint CloudTag = 0x9000001A;
    const uint CloudVariantMask = 0xF000;

    internal static bool IsCloudTag(uint tag) => (tag & ~CloudVariantMask) == CloudTag;

    // A cloud tag is not a name surrogate. All mount points, symbolic links,
    // legacy placeholders and unknown filter tags remain outside this policy.
    internal static void ValidateEntry(uint attributes, uint reparseTag)
    {
        if (
            (attributes & Device) != 0
            || ((attributes & ReparsePoint) != 0 && !IsCloudTag(reparseTag))
        )
            throw new StoragePathException(
                StoragePathFailure.RedirectedPath,
                "Linked, redirected or unsupported storage entries are not supported."
            );
        // An ancestor directory can have online-only siblings. Only the files
        // actually used by Manager must be available without a recall operation.
        if ((attributes & Directory) == 0 && (attributes & Unavailable) != 0)
            throw new StoragePathException(
                StoragePathFailure.CloudFileUnavailable,
                "An application or data file is not available on this device."
            );
    }

    internal static void ValidateFileSystem(string fileSystem)
    {
        if (!fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new StoragePathException(
                StoragePathFailure.UnsupportedFileSystem,
                "Portable application and data folders require NTFS."
            );
    }

    internal static void NoRedirects(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            if (OperatingSystem.IsWindows() && (attributes & FileAttributes.ReparsePoint) != 0)
                VerifyEntry(current);
            else
                ValidateEntry((uint)attributes, 0);
        }
    }

    internal static void VerifyEntry(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            ValidateEntry((uint)File.GetAttributes(path), 0);
            return;
        }
        using var handle = OpenVerifiedMetadata(path);
    }

    internal static SafeFileHandle OpenVerifiedMetadata(string path, bool retainName = false)
    {
        var handle = OpenMetadata(path, retainName);
        try
        {
            VerifyHandle(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void VerifyHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var information, 8))
            throw NativeFailure("Storage metadata could not be verified.");
        ValidateEntry(information.Attributes, information.ReparseTag);
    }

    internal static void RequireNtfs(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        NoRedirects(path);
        string? existing = path;
        while (existing is not null)
        {
            try
            {
                _ = File.GetAttributes(existing);
                break;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            existing = Path.GetDirectoryName(existing);
        }
        if (existing is null)
            throw new DirectoryNotFoundException("The portable storage volume is unavailable.");
        using var handle = OpenVerifiedMetadata(existing);
        var fileSystem = new StringBuilder(64);
        if (
            !GetVolumeInformationByHandle(
                handle,
                null,
                0,
                out _,
                out _,
                out _,
                fileSystem,
                fileSystem.Capacity
            )
        )
            throw NativeFailure("The portable storage filesystem could not be verified.");
        ValidateFileSystem(fileSystem.ToString());
    }

    static SafeFileHandle OpenMetadata(string path, bool retainName)
    {
        // Inspect the reparse entry itself without following it or recalling data.
        // Lifetime namespace protection is provided separately by directory leases.
        string full = Path.GetFullPath(path);
        if (
            full.Length > 32760
            || full.Length < 3
            || !char.IsAsciiLetter(full[0])
            || full[1] != ':'
            || full[2] != '\\'
        )
            throw new StoragePathException(
                StoragePathFailure.RedirectedPath,
                "Storage metadata requires a local drive path."
            );
        // FILE_READ_DATA makes the retained handle participate in Windows share
        // accounting. FILE_READ_ATTRIBUTES alone does not prevent a rename.
        var handle = CreateFile(
            @"\\?\" + full,
            retainName ? 0x81u : 0x80u,
            retainName ? 1u : 7u,
            0,
            3,
            0x02200000,
            0
        );
        if (!handle.IsInvalid)
            return handle;
        var error = NativeFailure("Storage metadata could not be opened.");
        handle.Dispose();
        throw error;
    }

    static IOException NativeFailure(string message) =>
        new(message, Marshal.GetHRForLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    struct AttributeTagInformation
    {
        internal uint Attributes;
        internal uint ReparseTag;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    static extern SafeFileHandle CreateFile(
        string name,
        uint access,
        uint sharing,
        nint security,
        uint disposition,
        uint flags,
        nint template
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,
        int kind,
        out AttributeTagInformation information,
        uint size
    );

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetVolumeInformationByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeInformationByHandle(
        SafeFileHandle handle,
        StringBuilder? volumeName,
        int volumeNameSize,
        out uint serial,
        out uint maximumComponent,
        out uint flags,
        StringBuilder fileSystem,
        int fileSystemSize
    );
}
