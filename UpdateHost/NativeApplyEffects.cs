using System.ComponentModel;
using System.Diagnostics;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed class NativeApplyEffects(UpdatePlan plan, string operationDirectory, ParentLease parent, PackageLease package, RegistrySnapshot registry, Action<string>? log = null) : IApplyEffects
{
    SnapshotManifest? snapshot;
    public BackupState? RecoveryState { get; private set; }
    internal static ProcessStartInfo InstallerStartInfo(string installer, string installDirectory)
    {
        installer = UpdatePaths.Local(installer); installDirectory = UpdatePaths.Local(installDirectory);
        // NSIS consumes everything after its final /D= as the destination and
        // requires it without surrounding quotes, including paths with spaces.
        // Local() rejects quotes, NUL, CR/LF and all other control characters.
        return new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)!, WindowStyle = ProcessWindowStyle.Hidden, Arguments = "/S --updated /D=" + installDirectory };
    }
    public void CheckCancellation()
    {
        string path = Path.Combine(operationDirectory, "cancel.json");
        if (!File.Exists(path)) return;
        // A malformed cancellation is a refusal, never a reason to keep applying.
        using var file = FileLease.Open(path, 1024);
        var cancel = UpdateProtocol.Read<UpdateCancel>(file.Read(1024));
        if (cancel.SchemaVersion != 1 || cancel.OperationId != plan.OperationId) throw new InvalidDataException("Cancellation identity changed.");
        throw new OperationCanceledException();
    }
    public async Task<bool> WaitForParentExit()
    {
        var clock = Stopwatch.StartNew();
        while (!parent.Exited)
        {
            CheckCancellation();
            if (clock.Elapsed >= TimeSpan.FromSeconds(60)) return false;
            await Task.Delay(100).ConfigureAwait(false);
        }
        CheckCancellation(); return true;
    }
    public async Task<BackupState> Backup()
    {
        log?.Invoke("Preparing a complete read-locked installation snapshot.");
        InstallationRegistration.Verify(plan.InstallDirectory, registry.Version);
        ProfileOwnership.Verify(Path.GetDirectoryName(plan.InstallDirectory)!);
        string backup = Path.Combine(operationDirectory, "backup-installation");
        Evidence.WriteNew(Path.Combine(operationDirectory, "registry-before.json"), registry);
        snapshot = await InstallationSnapshot.Copy(plan.InstallDirectory, backup).ConfigureAwait(false);
        Evidence.WriteNew(Path.Combine(operationDirectory, "backup-manifest.json"), snapshot);
        log?.Invoke("Backup verified. Files=" + snapshot.Files.Length + "; bytes=" + snapshot.Files.Sum(file => file.Bytes));
        return RecoveryState = new(backup, registry.Version);
    }
    public async Task<InstallerRun> Install()
    {
        CheckCancellation(); InstallationRegistration.Verify(plan.InstallDirectory, registry.Version);
        package.Verify(plan.InstallerSha256); UpdatePaths.NoRedirects(plan.InstallDirectory);
        log?.Invoke("Starting the pinned NSIS installer.");
        Process? installer;
        try { installer = Process.Start(InstallerStartInfo(plan.InstallerPath, plan.InstallDirectory)); }
        catch (Win32Exception) { return new(true, null, false); }
        if (installer is null) return new(true, null, false);
        using (installer)
        {
            try { await installer.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false); log?.Invoke("Installer exit=" + installer.ExitCode); return new(true, installer.ExitCode); }
            catch (TimeoutException) { return new(false, null); }
        }
    }
    public void VerifyInstalled(string version)
    {
        InstallationRegistration.Verify(plan.InstallDirectory, version);
        string host = Path.Combine(plan.InstallDirectory, "Bezoro.AhkManager.exe");
        using var lease = FileLease.Open(host, 512L * 1024 * 1024);
        string? actualVersion = FileVersionInfo.GetVersionInfo(host).ProductVersion;
        if (actualVersion is null || !UpdateProtocol.SameVersion(version, actualVersion)) throw new InvalidDataException("Installed Manager version does not match its receipt.");
    }
    public void Launch()
    {
        log?.Invoke("Reopening the verified Manager in the foreground.");
        string host = Path.Combine(plan.InstallDirectory, "Bezoro.AhkManager.exe"); using var lease = FileLease.Open(host, 512L * 1024 * 1024);
        var info = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = plan.InstallDirectory }; info.ArgumentList.Add("--foreground");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Manager launch was not confirmed.");
    }
    public async Task<BackupState> Restore(BackupState backup)
    {
        log?.Invoke("Restoring the verified installation snapshot without repeating an installer.");
        if (snapshot is null || backup.Directory != Path.Combine(operationDirectory, "backup-installation") || backup.Version != registry.Version) throw new InvalidDataException("No complete in-memory recovery snapshot.");
        InstallationSnapshot.Verify(backup.Directory, snapshot);
        // Stage the complete replacement on the installation volume before
        // moving its partial contents. This also supports installations on D:.
        string staging = plan.InstallDirectory + ".restore-" + plan.OperationId.ToString("N");
        UpdatePaths.NoRedirects(staging);
        ProfileOwnership.Verify(Path.GetDirectoryName(staging)!);
        _ = await InstallationSnapshot.Copy(backup.Directory, staging).ConfigureAwait(false);
        InstallationSnapshot.Verify(staging, snapshot);
        InstallationSnapshot.RestoreSecurity(staging, snapshot);
        string? failed = null;
        if (Directory.Exists(plan.InstallDirectory))
        {
            UpdatePaths.NoRedirects(plan.InstallDirectory);
            bool sameVolume = Path.GetPathRoot(plan.InstallDirectory)!.Equals(Path.GetPathRoot(operationDirectory), StringComparison.OrdinalIgnoreCase);
            failed = sameVolume ? Path.Combine(operationDirectory, "failed-installation") : plan.InstallDirectory + ".failed-" + plan.OperationId.ToString("N");
            if (Directory.Exists(failed) || File.Exists(failed)) throw new IOException("Failed installation preservation destination already exists.");
            Directory.Move(plan.InstallDirectory, failed);
            RecoveryState = backup with { FailedDirectory = failed };
        }
        Directory.Move(staging, plan.InstallDirectory);
        InstallationSnapshot.Verify(plan.InstallDirectory, snapshot);
        InstallationSnapshot.VerifySecurity(plan.InstallDirectory, snapshot);
        InstallationRegistration.Restore(registry, plan.InstallDirectory);
        log?.Invoke("Previous installation files and owned registry keys restored.");
        return RecoveryState = backup with { FailedDirectory = failed };
    }
}
