using System.Text.RegularExpressions;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

/// <summary>Imports the existing profile once and prepares explicitly selected data moves.</summary>
internal sealed class PortableProfileSession(
    RuntimeProfileLayout layout,
    Func<bool> effectsAllowed,
    Func<ManagerSnapshot> snapshot
)
{
    bool initialized;

    internal async Task<OperationResult> InitializeAsync(CancellationToken ct)
    {
        if (initialized || !layout.Portable)
            return new(Outcome.Confirmed);
        try
        {
            ct.ThrowIfCancellationRequested();
            TrustedProfileDirectory.Ensure(layout.DataRoot);
            TrustedProfileDirectory.Ensure(layout.ProfileRoot);
            string settings = Path.Combine(layout.ProfileRoot, "settings.json");
            if (SettingsPresent(settings))
            {
                initialized = true;
                return new(Outcome.Confirmed);
            }
            string source = layout.LegacyProfileRoot;
            RuntimeProfileLayout.NoRedirects(source);
            if (!SettingsPresent(Path.Combine(source, "settings.json")))
            {
                if (Directory.EnumerateFileSystemEntries(layout.ProfileRoot).Any())
                    return Refused("The portable profile is not empty. Its files were preserved.");
                initialized = true;
                return new(Outcome.Confirmed);
            }
            if (!effectsAllowed())
                return new(
                    Outcome.Failed,
                    ReasonCode.EffectOwnerUnavailable,
                    "Exit the other Manager with 'Exit and keep scripts running', then reopen the portable copy to import your profile. No settings were replaced."
                );
            TrustedProfileDirectory.Verify(source);
            ValidateContinuationInventory(source);
            if (
                (
                    await new StartupContinuationStore(source).PendingAsync().ConfigureAwait(false)
                ).Length > 0
            )
                return new(
                    Outcome.Failed,
                    ReasonCode.PendingOperation,
                    "The previous profile has pending startup results. Open the previous Manager and use Check pending results before importing it. Its files were preserved."
                );
            var migration = new ProfileMigrationService(layout.ProfileRoot, effectsAllowed);
            var preview = await migration.PreviewAsync(source, ct).ConfigureAwait(false);
            if (!preview.CanMigrate)
                return Refused(
                    "The existing profile could not be imported safely. Its files were preserved. "
                        + string.Join(
                            " ",
                            preview
                                .Issues.Select(issue => issue.Detail)
                                .Where(detail => !string.IsNullOrWhiteSpace(detail))
                        )
                );
            var result = await migration
                .CommitAsync(preview.PreviewId, true, ct)
                .ConfigureAwait(false);
            if (!result.Succeeded)
                return result with
                {
                    Detail =
                        "The existing profile changed or could not be copied. No empty replacement profile was created. "
                        + result.Detail,
                };
            TrustedProfileDirectory.Verify(layout.ProfileRoot);
            initialized = true;
            return new(Outcome.Confirmed);
        }
        catch (OperationCanceledException)
        {
            return new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled);
        }
        catch (Exception error) when (Known(error))
        {
            return Refused(
                "Portable profile initialization could not be completed. Your existing settings were preserved. "
                    + error.Message
            );
        }
    }

    internal OperationResult PrepareDataDirectory(string path)
    {
        if (!layout.Portable)
            return new(
                Outcome.Failed,
                ReasonCode.InvalidPath,
                "Data relocation is available in the portable edition."
            );
        var state = snapshot();
        if (!effectsAllowed() || !state.EffectsAvailable)
            return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
        if (
            state.ReadOnly
            || state.Issues.Any(issue => issue.Code == ReasonCode.PendingOperation)
            || state.Scripts.Any(script =>
                script.Issues.Any(issue => issue.Code == ReasonCode.PendingOperation)
            )
        )
            return new(
                Outcome.Failed,
                ReasonCode.PendingOperation,
                "Resolve pending results before moving portable data."
            );
        try
        {
            string stage = RuntimeProfileLayout.Local(path);
            if (!ScriptPaths.Equal(stage, path))
                throw new InvalidDataException("The selected staging path must be normalized.");
            RuntimeProfileLayout.ValidateDataLocation(layout.PortableRoot!, stage);
            if (
                RuntimeProfileLayout.Within(stage, layout.DataRoot)
                || RuntimeProfileLayout.Within(layout.DataRoot, stage)
                || !Regex.IsMatch(
                    Path.GetFileName(stage),
                    @"\A\..+\.ahk-manager-move-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z",
                    RegexOptions.CultureInvariant
                )
            )
                throw new InvalidDataException(
                    "A fresh sibling staging folder is required for the selected data location."
                );
            TrustedProfileDirectory.CreateNew(stage);
            return new(Outcome.Confirmed);
        }
        catch (Exception error) when (Known(error))
        {
            return new(
                Outcome.Failed,
                ReasonCode.InvalidPath,
                "The data staging folder could not be prepared. " + error.Message
            );
        }
    }

    static OperationResult Refused(string detail) =>
        new(Outcome.Failed, ReasonCode.ImportInvalid, detail);

    static void ValidateContinuationInventory(string source)
    {
        string directory = Path.Combine(source, "startup-continuations");
        RuntimeProfileLayout.NoRedirects(directory);
        if (File.Exists(directory))
            throw new InvalidDataException(
                "The previous startup recovery path is not a directory."
            );
        if (!Directory.Exists(directory))
            return;
        int count = 0;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            RuntimeProfileLayout.NoRedirects(file);
            long length = new FileInfo(file).Length;
            if (
                ++count > 1000
                || length > StartupContinuationStore.MaximumBytes
                || (bytes += length) > 64L * 1024 * 1024
            )
                throw new InvalidDataException(
                    "The previous startup recovery inventory exceeds its import limit."
                );
        }
    }

    static bool SettingsPresent(string path)
    {
        RuntimeProfileLayout.NoRedirects(path);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new InvalidDataException("The settings path is not a regular file.");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    static bool Known(Exception error) =>
        SafeJsonFiles.Known(error)
        || error is InvalidOperationException or System.ComponentModel.Win32Exception;
}
