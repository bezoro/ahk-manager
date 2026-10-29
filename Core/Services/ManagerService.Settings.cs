using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    public Task<OperationResult> SaveSettingsAsync(
        ManagerSettings settings,
        bool startupConsent = false,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                bool registrationChange =
                    settings.LaunchManagerAtLogin != document.Settings.LaunchManagerAtLogin
                    || settings.LaunchManagerAtLogin
                        && settings.LaunchManagerAsAdministrator
                            != document.Settings.LaunchManagerAsAdministrator;
                bool registrationNeeded =
                    registrationChange
                    || startup.RegistrationStatus(
                        settings.LaunchManagerAtLogin,
                        settings.LaunchManagerAsAdministrator
                    ) != ReasonCode.None;
                if (registrationNeeded && (!owner.Held || !owner.Trusted))
                    return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
                if (registrationChange && settings.LaunchManagerAtLogin && !startupConsent)
                    return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
                if (settings.InterpreterPath is { Length: > 0 } path)
                {
                    try
                    {
                        settings = settings with
                        {
                            InterpreterPath = AutoHotkeyDiscovery.Validate(path).Path,
                        };
                    }
                    catch (Exception ex)
                        when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception)
                    {
                        return new(Outcome.Failed, ReasonCode.UnsupportedInterpreter, ex.Message);
                    }
                }
                var saved = await Commit(
                        document with
                        {
                            Settings = settings,
                        },
                        null,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (!saved.Succeeded || !registrationNeeded)
                    return saved;
                // A saved enabled preference is durable intent. Reconcile the actual Windows
                // registration on a later save too, including after a previous partial result.
                var registered =
                    settings.LaunchManagerAtLogin
                    && settings.LaunchManagerAsAdministrator
                    && !processes.IsAdministrator
                        ? new OperationResult(
                            Outcome.Failed,
                            ReasonCode.ManualElevationRequired,
                            "The preferences were saved. Confirm Windows permission to finish configuring administrator autostart."
                        )
                        : await startup
                            .SetEnabledAsync(
                                settings.LaunchManagerAtLogin,
                                startupConsent || document.Settings.LaunchManagerAtLogin,
                                settings.LaunchManagerAsAdministrator,
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                await Log(null, null, registered, CancellationToken.None).ConfigureAwait(false);
                await Publish(false, CancellationToken.None).ConfigureAwait(false);
                return registered.Succeeded ? saved : registered with { Outcome = Outcome.Partial };
            },
            cancellationToken
        );

    public Task<OperationResult> RestoreBackupAsync(
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                var restored = await store
                    .RestoreBackupAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!restored.Succeeded)
                    return restored;
                var result = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                document = result.Document;
                storageIssues = result.Issues;
                await Publish(true, cancellationToken).ConfigureAwait(false);
                return restored;
            },
            cancellationToken
        );
}
