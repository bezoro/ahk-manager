using System.Diagnostics;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class PortableApplyEffects(
    UpdatePlan plan,
    string operationDirectory,
    IParentProcess parent,
    PackageLease package,
    PortableLayout layout,
    PortableInventory original,
    Action<string>? log = null,
    Action<string, string>? verifyImage = null
) : IApplyEffects
{
    SnapshotManifest? snapshot;
    readonly string originalLauncherHash = original.Files[PortableLayout.Executable].Hash();
    readonly string originalDescriptorHash = original.Manifest.Hash();
    bool applicationMoved;
    string Stage => Path.Combine(layout.Root, ".app-stage-" + plan.OperationId.ToString("N"));
    string Previous => Path.Combine(layout.Root, ".app-previous-" + plan.OperationId.ToString("N"));
    string DescriptorPath => Path.Combine(layout.Root, PortableLayout.Descriptor);
    public BackupState? RecoveryState { get; private set; }

    public void CheckCancellation() =>
        UpdateShutdownGate.CheckCancellation(operationDirectory, plan.OperationId);

    public Task<bool> WaitForParentExit() =>
        UpdateShutdownGate.Wait(
            plan,
            operationDirectory,
            () => parent.Exited,
            () => parent.ExitCode,
            TimeSpan.FromSeconds(60)
        );

    public async Task<BackupState> Backup()
    {
        layout.Revalidate();
        log?.Invoke(
            "Preserving the portable payload and launcher; user data remains outside the payload."
        );
        string backup = Path.Combine(operationDirectory, "backup-installation");
        snapshot = await InstallationSnapshot
            .Copy(layout.Application, backup, PortablePayload.MaximumFiles * 2)
            .ConfigureAwait(false);
        await CopyLease(
                original.Files[PortableLayout.Executable],
                Path.Combine(operationDirectory, "launcher-before.exe")
            )
            .ConfigureAwait(false);
        await CopyLease(
                original.Manifest,
                Path.Combine(operationDirectory, "descriptor-before.json")
            )
            .ConfigureAwait(false);
        Evidence.WriteNew(
            Path.Combine(operationDirectory, "backup-manifest.json"),
            snapshot,
            16 * 1024 * 1024
        );
        original.Dispose();
        return RecoveryState = new(backup, original.Descriptor.Version);
    }

    public async Task<InstallerRun> Install()
    {
        try
        {
            CheckCancellation();
            layout.Revalidate();
            package.Verify(plan.InstallerSha256);
            await PortablePayload
                .Extract(package, Stage, plan.Version, verifyImage)
                .ConfigureAwait(false);
            if (snapshot is null)
                throw new InvalidDataException("A portable recovery snapshot is missing.");
            InstallationSnapshot.Verify(
                layout.Application,
                snapshot,
                PortablePayload.MaximumFiles * 2
            );
            InstallationSnapshot.VerifySecurity(layout.Application, snapshot);
            VerifyOriginalMetadata();
            CheckCancellation();
            layout.Revalidate();
            log?.Invoke("Promoting the verified portable payload on its original volume.");
            if (Directory.Exists(Previous) || File.Exists(Previous))
                throw new IOException("The portable preservation destination already exists.");
            Directory.Move(layout.Application, Previous);
            applicationMoved = true;
            log?.Invoke("Previous portable payload preserved.");
            Directory.Move(Path.Combine(Stage, "App"), layout.Application);
            log?.Invoke("New portable payload promoted.");
            File.Replace(
                Path.Combine(Stage, PortableLayout.Executable),
                layout.Launcher,
                Path.Combine(Stage, "launcher-before-replacement.exe")
            );
            log?.Invoke("Portable launcher replaced atomically.");
            File.Replace(
                Path.Combine(Stage, PortableLayout.Descriptor),
                DescriptorPath,
                Path.Combine(Stage, "descriptor-before-replacement.json")
            );
            log?.Invoke("Portable inventory replaced atomically.");
            return new(true, 0);
        }
        catch (OperationCanceledException) when (!applicationMoved)
        {
            throw;
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex))
        {
            // All effects are in this process; there is no unobserved installer
            // dispatch. A caught failure can enter verified snapshot recovery.
            log?.Invoke("Portable promotion failed: " + ex.GetType().Name);
            return new(true, 20);
        }
    }

    public void VerifyInstalled(string version)
    {
        layout.Revalidate();
        using var inventory = new PortableInventory(layout.Root);
        if (inventory.Descriptor.Version != version)
            throw new InvalidDataException("The portable inventory identifies another version.");
        (verifyImage ?? PortablePayload.VerifyVersion)(layout.Application, version);
    }

    public void Launch()
    {
        layout.Revalidate();
        string executable = Path.Combine(layout.Application, PortableLayout.Executable);
        using var lease = FileLease.Open(executable, PortablePayload.MaximumFileBytes);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = layout.Application,
        };
        start.ArgumentList.Add("--foreground");
        using var process =
            Process.Start(start)
            ?? throw new InvalidOperationException("Portable Manager launch was not confirmed.");
    }

    internal bool CleanupCompleted()
    {
        try
        {
            VerifyCompletedWorkspaces();
            // These are exclusively owned duplicates of the verified recovery
            // snapshot. The durable outcome and recovery cache remain intact.
            Directory.Delete(Previous, true);
            Directory.Delete(Stage, true);
            CleanupLog(
                "Removed completed portable staging duplicates; recovery history was preserved."
            );
            return true;
        }
        catch (Exception error) when (UpdateProtocol.Known(error))
        {
            // Cleanup cannot change a durably confirmed installation outcome.
            // Unexpected files and failed removals remain available for inspection.
            CleanupLog("Portable staging cleanup was deferred: " + error.GetType().Name);
            return false;
        }
    }

    void VerifyCompletedWorkspaces()
    {
        using var outcomeFile = FileLease.Open(
            Path.Combine(operationDirectory, "outcome.json"),
            UpdateProtocol.MaximumPlanBytes
        );
        var outcome = UpdateProtocol.Read<UpdateOutcome>(
            outcomeFile.Read(UpdateProtocol.MaximumPlanBytes)
        );
        if (
            outcome.SchemaVersion != 1
            || outcome.OperationId != plan.OperationId
            || outcome.Outcome != "Installed"
            || outcome.Version != plan.Version
            || outcome.InstallerExitCode != 0
            || outcome.BackupDirectory != Path.Combine(operationDirectory, "backup-installation")
            || snapshot is null
            || !applicationMoved
        )
            throw new InvalidDataException(
                "Only a durably completed portable update can clean staging."
            );
        layout.Revalidate();
        ProfileOwnership.Verify(operationDirectory);
        string backup = Path.Combine(operationDirectory, "backup-installation");
        InstallationSnapshot.Verify(backup, snapshot, PortablePayload.MaximumFiles * 2);
        InstallationSnapshot.VerifyPrivate(backup, snapshot);
        VerifyCleanupFile(
            Path.Combine(operationDirectory, "launcher-before.exe"),
            originalLauncherHash
        );
        VerifyCleanupFile(
            Path.Combine(operationDirectory, "descriptor-before.json"),
            originalDescriptorHash
        );
        InstallationSnapshot.Verify(Previous, snapshot, PortablePayload.MaximumFiles * 2);
        InstallationSnapshot.VerifySecurity(Previous, snapshot);
        InstallationSnapshot.VerifyPrivate(Previous, snapshot);
        UpdatePaths.NoRedirects(Stage);
        SnapshotSecurity.VerifyPrivate(Stage, true, true);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["launcher-before-replacement.exe"] = originalLauncherHash,
            ["descriptor-before-replacement.json"] = originalDescriptorHash,
        };
        string[] members = Directory.GetFileSystemEntries(Stage);
        if (members.Length != expected.Count)
            throw new InvalidDataException("The completed portable staging contents changed.");
        foreach (string member in members)
        {
            if (!expected.TryGetValue(Path.GetFileName(member), out string? hash))
                throw new InvalidDataException("The completed portable staging contents changed.");
            VerifyCleanupFile(member, hash);
        }
    }

    static void VerifyCleanupFile(string path, string hash)
    {
        using var file = FileLease.Open(path, PortablePayload.MaximumFileBytes);
        SnapshotSecurity.VerifyPrivate(path, false);
        if (file.Hash() != hash)
            throw new InvalidDataException("Portable cleanup metadata changed.");
    }

    void CleanupLog(string message)
    {
        try
        {
            log?.Invoke(message);
        }
        catch (Exception error) when (UpdateProtocol.Known(error)) { }
    }

    public async Task<BackupState> Restore(BackupState backup)
    {
        if (
            snapshot is null
            || backup.Directory != Path.Combine(operationDirectory, "backup-installation")
            || backup.Version != original.Descriptor.Version
        )
            throw new InvalidDataException("A complete portable recovery snapshot is unavailable.");
        layout.Revalidate(requireApplication: false);
        InstallationSnapshot.Verify(backup.Directory, snapshot, PortablePayload.MaximumFiles * 2);
        if (!applicationMoved)
        {
            // Preparation failed before the first rename. Never overwrite a
            // concurrently modified payload while claiming to restore it.
            InstallationSnapshot.Verify(
                layout.Application,
                snapshot,
                PortablePayload.MaximumFiles * 2
            );
            InstallationSnapshot.VerifySecurity(layout.Application, snapshot);
            VerifyOriginalMetadata();
            return backup;
        }
        string restore = Path.Combine(Stage, "restored-application");
        _ = await InstallationSnapshot
            .Copy(backup.Directory, restore, PortablePayload.MaximumFiles * 2)
            .ConfigureAwait(false);
        InstallationSnapshot.Verify(restore, snapshot, PortablePayload.MaximumFiles * 2);
        InstallationSnapshot.RestoreSecurity(restore, snapshot, PortablePayload.MaximumFiles * 2);
        string? failed = null;
        if (Directory.Exists(layout.Application))
        {
            UpdatePaths.NoRedirects(layout.Application);
            failed = Path.Combine(Stage, "failed-application");
            Directory.Move(layout.Application, failed);
            RecoveryState = backup with { FailedDirectory = failed };
        }
        Directory.Move(restore, layout.Application);
        await RestoreMetadata(
                "launcher-before.exe",
                originalLauncherHash,
                layout.Launcher,
                "restored-launcher.exe",
                "failed-launcher.exe"
            )
            .ConfigureAwait(false);
        await RestoreMetadata(
                "descriptor-before.json",
                originalDescriptorHash,
                DescriptorPath,
                "restored-descriptor.json",
                "failed-descriptor.json"
            )
            .ConfigureAwait(false);
        InstallationSnapshot.Verify(layout.Application, snapshot, PortablePayload.MaximumFiles * 2);
        InstallationSnapshot.VerifySecurity(layout.Application, snapshot);
        VerifyOriginalMetadata();
        log?.Invoke(
            "Previous portable payload, launcher and inventory restored; data was not moved."
        );
        return RecoveryState = backup with { FailedDirectory = failed };
    }

    void VerifyOriginalMetadata()
    {
        using var launcher = FileLease.Open(layout.Launcher, PortablePayload.MaximumFileBytes);
        using var descriptor = FileLease.Open(
            DescriptorPath,
            PortablePayload.MaximumDescriptorBytes
        );
        if (launcher.Hash() != originalLauncherHash || descriptor.Hash() != originalDescriptorHash)
            throw new InvalidDataException(
                "Portable launcher or inventory changed during the update."
            );
    }

    async Task RestoreMetadata(
        string backupName,
        string hash,
        string destination,
        string stagedName,
        string failedName
    )
    {
        using var source = FileLease.Open(
            Path.Combine(operationDirectory, backupName),
            PortablePayload.MaximumFileBytes
        );
        if (source.Hash() != hash)
            throw new InvalidDataException("Portable recovery metadata changed.");
        string staged = Path.Combine(Stage, stagedName);
        await CopyLease(source, staged).ConfigureAwait(false);
        UpdatePaths.NoRedirects(destination);
        if (File.Exists(destination))
            File.Replace(staged, destination, Path.Combine(Stage, failedName));
        else
            File.Move(staged, destination, false);
    }

    static async Task CopyLease(FileLease source, string destination)
    {
        source.Stream.Position = 0;
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        await source.Stream.CopyToAsync(output).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        output.Flush(true);
        source.Stream.Position = 0;
    }
}
