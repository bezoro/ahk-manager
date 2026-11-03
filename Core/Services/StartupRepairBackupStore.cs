using System.Globalization;
using System.Text.Json;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

internal sealed class StartupRepairBackupStore(string profileDirectory)
{
    internal const string DirectoryName = "startup-repairs";
    const int MaximumBackups = 32;
    const int MaximumBytes = 512 * 1024;

    internal async Task<OperationResult> SaveAsync(StartupRepairBackup backup, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string directory = Path.Combine(profileDirectory, DirectoryName);
            SafeJsonFiles.SafePath(directory);
            Directory.CreateDirectory(directory);
            SafeJsonFiles.SafePath(directory);
            if (
                Directory.EnumerateFileSystemEntries(directory).Take(MaximumBackups).Count()
                >= MaximumBackups
            )
                return new(
                    Outcome.Failed,
                    ReasonCode.StorageFailure,
                    "The startup repair backup folder is full. Existing entries were preserved."
                );

            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes, new() { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("applicationId", "Bezoro.AhkManager.Electron");
                writer.WriteString("kind", "startup-repair-backup");
                writer.WriteString("timestamp", backup.Timestamp.ToUniversalTime());
                writer.WriteString("userSid", backup.UserSid);
                writer.WriteString(
                    "registryKey",
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
                );
                writer.WriteString("registryValueName", "Bezoro.AhkManager.WinUI");
                writer.WriteString("previousRunEntry", backup.PreviousRunEntry);
                writer.WriteString("currentCommand", backup.CurrentCommand);
                writer.WriteString("taskFingerprint", backup.TaskFingerprint);
                writer.WriteBoolean("launchManagerAtLogin", backup.LaunchManagerAtLogin);
                writer.WriteBoolean(
                    "launchManagerAsAdministrator",
                    backup.LaunchManagerAsAdministrator
                );
                writer.WriteEndObject();
            }
            if (bytes.Length > MaximumBytes)
                return new(
                    Outcome.Failed,
                    ReasonCode.StorageFailure,
                    "The startup repair backup exceeds its size limit."
                );
            string name =
                "startup-repair-"
                + backup.Timestamp.UtcDateTime.ToString(
                    "yyyyMMdd'T'HHmmssfff'Z'",
                    CultureInfo.InvariantCulture
                )
                + "-"
                + Guid.NewGuid().ToString("D")
                + ".json";
            await SafeJsonFiles
                .WriteNewAsync(Path.Combine(directory, name), bytes.ToArray(), ct)
                .ConfigureAwait(false);
            return new(Outcome.Confirmed, Detail: name);
        }
        catch (OperationCanceledException)
        {
            return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
        }
        catch (Exception error) when (SafeJsonFiles.Known(error))
        {
            return new(
                Outcome.Failed,
                ReasonCode.StorageFailure,
                "The startup repair backup could not be saved. Existing entries were preserved."
            );
        }
    }
}
