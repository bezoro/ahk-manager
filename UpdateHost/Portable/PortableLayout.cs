using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed record PortableConfiguration(
    int SchemaVersion,
    string ApplicationId,
    string DataDirectory
);

internal sealed record PortableLayout(
    string Root,
    string Application,
    string Data,
    string MarkerHash
)
{
    internal const string ApplicationId = "Bezoro.AhkManager.Electron";
    internal const string Executable = "Bezoro.AhkManager.exe";
    internal const string Descriptor = "portable-package.json";
    internal string Updates => Path.Combine(Data, "Updates");
    internal string Launcher => Path.Combine(Root, Executable);
    internal string Marker => Path.Combine(Root, "portable.json");

    internal static string ParentImage()
    {
        using var self = Process.GetCurrentProcess();
        if (
            Native.NtQueryInformationProcess(
                self.SafeHandle,
                0,
                out var information,
                Marshal.SizeOf<Native.BasicInformation>(),
                out _
            ) != 0
            || information.ParentPid.ToInt64() is <= 0 or > int.MaxValue
        )
            throw new InvalidDataException("The native Manager parent is unavailable.");
        using var parent = Process.GetProcessById((int)information.ParentPid);
        if (
            parent.SessionId != self.SessionId
            || !Native.OpenProcessToken(parent.SafeHandle, 8, out var token)
        )
            throw new InvalidDataException("The portable Manager session is not authenticated.");
        using (token)
        using (var parentUser = new WindowsIdentity(token.DangerousGetHandle()))
        using (var currentUser = WindowsIdentity.GetCurrent())
            if (
                parentUser.User is null
                || currentUser.User is null
                || parentUser.User.Value != currentUser.User.Value
            )
                throw new InvalidDataException("The portable Manager belongs to another user.");
        var image = new StringBuilder(32768);
        int length = image.Capacity;
        if (!Native.QueryFullProcessImageName(parent.SafeHandle, 0, image, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return UpdatePaths.Local(image.ToString());
    }

    internal static PortableLayout? FromParent()
    {
        string image = ParentImage();
        string application = Path.GetDirectoryName(image)!;
        if (
            !Path.GetFileName(image).Equals(Executable, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(application).Equals("App", StringComparison.OrdinalIgnoreCase)
        )
            return null;
        string root = Path.GetDirectoryName(application)!;
        if (!File.Exists(Path.Combine(root, "portable.json")))
            return null;
        return Read(root);
    }

    internal static PortableLayout Read(string root)
    {
        root = UpdatePaths.Local(root);
        if (UpdatePaths.Equal(root, Path.GetPathRoot(root)!))
            throw new InvalidDataException("A portable container cannot be a drive root.");
        UpdatePaths.NoRedirects(root);
        using var marker = FileLease.Open(Path.Combine(root, "portable.json"), 4096);
        var configuration = UpdateProtocol.Read<PortableConfiguration>(marker.Read(4096));
        if (
            configuration.SchemaVersion != 1
            || configuration.ApplicationId != ApplicationId
            || string.IsNullOrWhiteSpace(configuration.DataDirectory)
            || configuration.DataDirectory.Contains('/')
        )
            throw new InvalidDataException("The portable configuration is invalid.");
        string data = ResolveDataDirectory(root, configuration.DataDirectory);
        return new(root, Path.Combine(root, "App"), data, marker.Hash());
    }

    internal static string ResolveDataDirectory(string root, string selected)
    {
        if (string.IsNullOrWhiteSpace(selected) || selected.Contains('/'))
            throw new InvalidDataException("The portable data path is invalid.");
        string data;
        if (Path.IsPathFullyQualified(selected))
        {
            data = UpdatePaths.Local(selected);
            if (!data.Equals(selected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The portable data path must be normalized.");
        }
        else
        {
            if (
                Path.IsPathRooted(selected)
                || selected.Contains(':')
                || selected
                    .Split(Path.DirectorySeparatorChar)
                    .Any(part => part.Length == 0 || part is "." or "..")
            )
                throw new InvalidDataException(
                    "The portable data path must be normalized and local."
                );
            data = UpdatePaths.Local(Path.Combine(root, selected));
        }
        string application = Path.Combine(root, "App");
        if (SameOrChild(root, data) || SameOrChild(data, application))
            throw new InvalidDataException("Portable application and data paths overlap.");
        foreach (
            string reserved in new[]
            {
                Executable,
                "portable.json",
                Descriptor,
                "portable-data-move.json",
            }
        )
            if (SameOrChild(data, Path.Combine(root, reserved)))
                throw new InvalidDataException("Portable data overlaps an application file.");
        if (SameOrChild(data, root))
        {
            string first = Path.GetRelativePath(root, data).Split(Path.DirectorySeparatorChar)[0];
            if (
                first.StartsWith(".app-stage-", StringComparison.OrdinalIgnoreCase)
                || first.StartsWith(".app-previous-", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(
                    first,
                    @"\A\.portable-data-(config|move)-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.tmp\z",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                )
            )
                throw new InvalidDataException("Portable data overlaps update recovery files.");
        }
        UpdatePaths.NoRedirects(data);
        return data;
    }

    internal static bool SameOrChild(string candidate, string ancestor) =>
        UpdatePaths.Equal(candidate, ancestor)
        || candidate.StartsWith(
            Path.EndsInDirectorySeparator(ancestor)
                ? ancestor
                : ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );

    internal void Revalidate(bool requireApplication = true)
    {
        var current = Read(Root);
        if (current != this)
            throw new InvalidDataException("Portable configuration changed during the update.");
        ProfileOwnership.Verify(Root);
        if (requireApplication)
            ProfileOwnership.Verify(Application);
        ProfileOwnership.Verify(Data);
        ProfileOwnership.Verify(Updates);
    }
}
