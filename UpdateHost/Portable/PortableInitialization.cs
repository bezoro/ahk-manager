namespace Bezoro.AhkManager.UpdateHost;

internal static class PortableInitialization
{
    internal static PortableLayout Run()
    {
        var layout =
            PortableLayout.FromParent()
            ?? throw new InvalidDataException("The parent is not a portable Manager.");
        using var directories = new DirectoryLease([
            layout.Root,
            Directory.Exists(layout.Data) ? layout.Data : Path.GetDirectoryName(layout.Data)!,
        ]);
        using var marker = FileLease.Open(layout.Marker, 4096);
        if (marker.Hash() != layout.MarkerHash)
            throw new InvalidDataException("Portable configuration changed before initialization.");
        VerifySourceHelper(layout);
        Initialize(layout);
        InitializeData(layout);
        return layout;
    }

    internal static void VerifySourceHelper(PortableLayout layout)
    {
        string expected = Path.Combine(
            layout.Application,
            "backend",
            "update",
            "AhkManager.UpdateHost.exe"
        );
        if (Environment.ProcessPath is not { } own || !UpdatePaths.Equal(own, expected))
            throw new InvalidDataException("Portable initialization requires the packaged helper.");
    }

    internal static void Initialize(PortableLayout layout)
    {
        if (AlreadyPrivate(layout))
            return;
        VerifyRootMembers(layout);
        using var inventory = new PortableInventory(layout.Root);
        PortablePayload.VerifyVersion(layout.Application, inventory.Descriptor.Version);
        // Every inventoried file remains read-locked while only this dedicated
        // product container is protected. Repository and ancestor ACLs are untouched.
        SnapshotSecurity.ProtectOwnedPath(layout.Root, true);
        SnapshotSecurity.ProtectOwnedPath(layout.Application, true);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in inventory.Files.Values)
        {
            string? parent = Path.GetDirectoryName(file.Path);
            while (parent is not null && PortableLayout.SameOrChild(parent, layout.Application))
            {
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        foreach (string directory in directories.OrderBy(path => path.Length))
            SnapshotSecurity.ProtectOwnedPath(directory, true);
        foreach (var file in inventory.Files.Values)
            SnapshotSecurity.ProtectOwnedPath(file.Path, false);
        SnapshotSecurity.ProtectOwnedPath(
            Path.Combine(layout.Root, PortableLayout.Descriptor),
            false
        );
        SnapshotSecurity.ProtectOwnedPath(layout.Marker, false);
        ProfileOwnership.Verify(layout.Root);
        ProfileOwnership.Verify(layout.Application);
        VerifyRootMembers(layout);
    }

    static bool AlreadyPrivate(PortableLayout layout)
    {
        try
        {
            SnapshotSecurity.VerifyPrivate(layout.Root, true, true);
            SnapshotSecurity.VerifyPrivate(layout.Application, true, true);
            foreach (
                string relative in new[]
                {
                    PortableLayout.Executable,
                    PortableLayout.Descriptor,
                    "portable.json",
                    "App/" + PortableLayout.Executable,
                    "App/backend/update/AhkManager.UpdateHost.exe",
                }
            )
                SnapshotSecurity.VerifyPrivate(
                    Path.Combine(layout.Root, relative.Replace('/', Path.DirectorySeparatorChar)),
                    false,
                    true
                );
            return true;
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex))
        {
            return false;
        }
    }

    internal static void InitializeData(PortableLayout layout)
    {
        UpdatePaths.NoRedirects(layout.Data);
        if (!Directory.Exists(layout.Data))
        {
            if (File.Exists(Path.Combine(layout.Root, "portable-data-move.json")))
                throw new InvalidDataException(
                    "A pending data move has no original data directory."
                );
            SnapshotSecurity.CreatePrivateDirectory(layout.Data);
        }
        ProfileOwnership.Verify(layout.Data);
    }

    internal static void VerifyRootMembers(PortableLayout layout)
    {
        var allowed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [PortableLayout.Executable] = false,
            [PortableLayout.Descriptor] = false,
            ["portable.json"] = false,
            ["portable-data-move.json"] = false,
            ["App"] = true,
            ["Data"] = true,
        };
        if (PortableLayout.SameOrChild(layout.Data, layout.Root))
            allowed[
                Path.GetRelativePath(layout.Root, layout.Data).Split(Path.DirectorySeparatorChar)[0]
            ] = true;
        foreach (string entry in Directory.EnumerateFileSystemEntries(layout.Root))
        {
            string name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if (
                name.StartsWith(".app-stage-", StringComparison.Ordinal)
                || name.StartsWith(".app-previous-", StringComparison.Ordinal)
            )
            {
                VerifyWorkspace(layout, entry);
                continue;
            }
            if (
                System.Text.RegularExpressions.Regex.IsMatch(
                    name,
                    @"\A\.portable-data-(config|move)-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.tmp\z"
                )
            )
            {
                using var temporary = FileLease.Open(
                    entry,
                    name.StartsWith(".portable-data-config-", StringComparison.Ordinal)
                        ? 4096
                        : 16384
                );
                SnapshotSecurity.VerifyPrivate(entry, false);
                continue;
            }
            if (
                !allowed.TryGetValue(name, out bool directory)
                || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || ((attributes & FileAttributes.Directory) != 0) != directory
            )
                throw new InvalidDataException(
                    "The portable root contains unrelated or redirected files."
                );
            if (name.Equals("portable-data-move.json", StringComparison.OrdinalIgnoreCase))
            {
                using var pending = FileLease.Open(entry, 16384);
            }
        }
    }

    static void VerifyWorkspace(PortableLayout layout, string directory)
    {
        string name = Path.GetFileName(directory);
        string id = name[(name.StartsWith(".app-stage-", StringComparison.Ordinal) ? 11 : 14)..];
        if (!Guid.TryParseExact(id, "N", out var operationId) || id != operationId.ToString("N"))
            throw new InvalidDataException(
                "An unrelated directory resembles portable recovery state."
            );
        SnapshotSecurity.VerifyPrivate(directory, true, true);
        string operation = UpdatePaths.OperationDirectory(layout.Updates, operationId);
        ProfileOwnership.Verify(operation);
        using var file = FileLease.Open(
            Path.Combine(operation, "plan.json"),
            UpdateProtocol.MaximumPlanBytes
        );
        var plan = UpdateProtocol.Read<UpdatePlan>(file.Read(UpdateProtocol.MaximumPlanBytes));
        UpdateProtocol.Validate(plan);
        UpdatePaths.ValidatePlanLocation(file.Path, plan, layout.Updates);
        if (
            !UpdateProtocol.Portable(plan)
            || plan.OperationId != operationId
            || !UpdatePaths.Equal(plan.InstallDirectory, layout.Application)
        )
            throw new InvalidDataException(
                "Portable recovery state belongs to another application."
            );
    }
}
