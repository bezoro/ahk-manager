using Microsoft.Win32;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed record RegistryValueSnapshot(
    string Name,
    RegistryValueKind Kind,
    string? Text = null,
    string[]? Strings = null,
    string? Binary = null,
    int? Dword = null,
    long? Qword = null
);

internal sealed record RegistryNodeSnapshot(string RelativePath, RegistryValueSnapshot[] Values);

internal sealed record RegistryTreeSnapshot(string KeyPath, RegistryNodeSnapshot[] Nodes);

internal sealed record RegistrySnapshot(
    RegistryTreeSnapshot Installation,
    RegistryTreeSnapshot Uninstall,
    string Version
);

internal static class InstallationRegistration
{
    static RegistryKey CurrentUser() =>
        RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    internal static string Verify(string installDirectory, string? expectedVersion = null)
    {
        using var user = CurrentUser();
        using var installation = user.OpenSubKey(UpdateProtocol.RegistryPath);
        using var uninstall = user.OpenSubKey(UpdateProtocol.UninstallRegistryPath);
        if (
            installation?.GetValue(
                "InstallLocation",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames
            )
                is not string path
            || !UpdatePaths.Equal(path, installDirectory)
            || installation.GetValue("UpdateProtocol") is not int protocol
            || protocol != 1
            || uninstall?.GetValue("DisplayVersion") is not string version
            || !UpdateProtocol.InstalledVersion(version)
            || expectedVersion is not null && !UpdateProtocol.SameVersion(version, expectedVersion)
        )
            throw new InvalidDataException(
                "Registered installation does not support this update protocol."
            );
        if (
            uninstall.GetValue(
                "UninstallString",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames
            )
                is not string command
            || !command.StartsWith(
                "\"" + installDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidDataException("Uninstaller is outside the registered installation.");
        return version;
    }

    internal static RegistrySnapshot Capture(string installDirectory)
    {
        string version = Verify(installDirectory);
        using var user = CurrentUser();
        return new(
            CaptureTree(user, UpdateProtocol.RegistryPath),
            CaptureTree(user, UpdateProtocol.UninstallRegistryPath),
            version
        );
    }

    static RegistryTreeSnapshot CaptureTree(RegistryKey user, string path)
    {
        var nodes = new List<RegistryNodeSnapshot>();
        var pending = new Stack<string>();
        pending.Push("");
        int values = 0;
        while (pending.Count > 0)
        {
            string relative = pending.Pop();
            using var key =
                user.OpenSubKey(path + (relative.Length == 0 ? "" : "\\" + relative))
                ?? throw new IOException("Installation registry changed.");
            var entries = new List<RegistryValueSnapshot>();
            foreach (string name in key.GetValueNames())
            {
                if (++values > 500)
                    throw new IOException("Installation registry snapshot exceeds limit.");
                RegistryValueKind kind = key.GetValueKind(name);
                object? value = key.GetValue(
                    name,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames
                );
                entries.Add(
                    kind switch
                    {
                        RegistryValueKind.String
                        or RegistryValueKind.ExpandString
                            when value is string text && text.Length <= 32760 => new(
                            name,
                            kind,
                            Text: text
                        ),
                        RegistryValueKind.MultiString
                            when value is string[] strings && strings.Sum(s => s.Length) <= 32760 =>
                            new(name, kind, Strings: strings),
                        RegistryValueKind.DWord when value is int number => new(
                            name,
                            kind,
                            Dword: number
                        ),
                        RegistryValueKind.QWord when value is long number => new(
                            name,
                            kind,
                            Qword: number
                        ),
                        RegistryValueKind.Binary
                        or RegistryValueKind.None
                            when value is byte[] binary && binary.Length <= 65536 => new(
                            name,
                            kind,
                            Binary: Convert.ToBase64String(binary)
                        ),
                        _ => throw new IOException("Unsupported installation registry value."),
                    }
                );
            }

            nodes.Add(new(relative, entries.ToArray()));
            if (nodes.Count > 50)
                throw new IOException("Installation registry snapshot exceeds limit.");
            foreach (string child in key.GetSubKeyNames())
                pending.Push(relative.Length == 0 ? child : relative + "\\" + child);
        }

        return new(path, nodes.ToArray());
    }

    internal static void Restore(RegistrySnapshot snapshot, string installDirectory)
    {
        if (
            snapshot.Installation.KeyPath != UpdateProtocol.RegistryPath
            || snapshot.Uninstall.KeyPath != UpdateProtocol.UninstallRegistryPath
        )
            throw new InvalidDataException("Unexpected installation registry snapshot.");
        using var user = CurrentUser();
        using (var current = user.OpenSubKey(UpdateProtocol.RegistryPath))
            if (
                current?.GetValue(
                    "InstallLocation",
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames
                )
                    is string location
                && !UpdatePaths.Equal(location, installDirectory)
            )
                throw new InvalidDataException("Installation ownership changed before recovery.");
        using (var current = user.OpenSubKey(UpdateProtocol.UninstallRegistryPath))
            if (
                current?.GetValue(
                    "UninstallString",
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames
                )
                    is string command
                && !command.StartsWith(
                    "\"" + installDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidDataException("Uninstall ownership changed before recovery.");
        foreach (var tree in new[] { snapshot.Installation, snapshot.Uninstall })
        {
            user.DeleteSubKeyTree(tree.KeyPath, false);
            foreach (var node in tree.Nodes)
            {
                using var key = user.CreateSubKey(
                    tree.KeyPath + (node.RelativePath.Length == 0 ? "" : "\\" + node.RelativePath),
                    true
                );
                foreach (var entry in node.Values)
                {
                    object value = entry.Kind switch
                    {
                        RegistryValueKind.String or RegistryValueKind.ExpandString => entry.Text!,
                        RegistryValueKind.MultiString => entry.Strings!,
                        RegistryValueKind.DWord => entry.Dword!.Value,
                        RegistryValueKind.QWord => entry.Qword!.Value,
                        RegistryValueKind.Binary or RegistryValueKind.None =>
                            Convert.FromBase64String(entry.Binary!),
                        _ => throw new InvalidDataException(),
                    };
                    key.SetValue(entry.Name, value, entry.Kind);
                }
            }
        }

        Verify(installDirectory, snapshot.Version);
    }
}
