using System.Security.Cryptography;

namespace Bezoro.AhkManager.UpdateHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test-portable-policy")
            return SelfTests.RunPortablePolicy();
        if (args.Length == 3 && args[0] == "--self-test" && args[1] == "--root")
        {
            try
            {
                return await SelfTests.Run(args[2]).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("update-host:self-test:" + ex);
                return 65;
            }
        }

        if (args.Length == 1 && args[0] == "--initialize-portable")
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    throw new InvalidOperationException(
                        "Portable initialization requires Windows."
                    );
                _ = PortableInitialization.Run();
                return 0;
            }
            catch (PortableDataInitializationException failure)
            {
                AdmissionDiagnostics.Write(UpdatePhase.PortableDataInitialization, failure.Failure);
                return 65;
            }
            catch (Exception ex) when (UpdateProtocol.Known(ex))
            {
                AdmissionDiagnostics.Write(UpdatePhase.PortableInitialization, ex);
                return 65;
            }
        }

        if (args.Length == 1 && args[0] == "--verify-portable-manifest")
            return PortableSignatureVerification.Run();

        if (args.Length == 1 && args[0] == "--lease-portable-data-move")
            return await PortableDataMoveLease.Run().ConfigureAwait(false);

        if (args.Length == 1 && args[0] == "--lease-portable-storage")
            return await PortableStorageLease.Run().ConfigureAwait(false);

        string? trustedOperation = null;
        UpdatePlan? trustedPlan = null;
        OperationLog? log = null;
        bool ready = false;
        UpdatePhase phase = UpdatePhase.Arguments;
        try
        {
            if (
                !OperatingSystem.IsWindows()
                || args.Length != 4
                || args[0] != "--plan"
                || args[2] != "--sha256"
                || !UpdateProtocol.Hash(args[3])
            )
                throw new InvalidDataException(
                    "Use --plan <absolute path> --sha256 <plan checksum>."
                );
            var portable = PortableLayout.FromParent();
            // App itself must be renameable during promotion, but retain the
            // dedicated container and every ancestor through the whole apply.
            // A permissive repository parent cannot swap the validated namespace.
            using var portableDirectories = portable is null
                ? null
                : new DirectoryLease([portable.Root, portable.Data, portable.Updates]);
            string root = portable?.Updates ?? UpdatePaths.UpdatesRoot;
            phase = UpdatePhase.HostLocation;
            string ownPath =
                Environment.ProcessPath
                ?? throw new InvalidDataException("Update host image is unavailable.");
            string ownHash = UpdatePaths.ValidateHostLocation(ownPath, root);
            phase = UpdatePhase.CacheOwnership;
            ProfileOwnership.Verify(root);
            phase = UpdatePhase.HostOwnership;
            ProfileOwnership.Verify(Path.GetDirectoryName(ownPath)!);
            phase = UpdatePhase.HostLease;
            using var host = FileLease.Open(ownPath, 256L * 1024 * 1024);
            phase = UpdatePhase.HostIntegrity;
            if (host.Hash() != ownHash)
                throw new InvalidDataException("Update host checksum changed.");
            phase = UpdatePhase.PlanLease;
            using var planFile = FileLease.Open(args[1], UpdateProtocol.MaximumPlanBytes);
            phase = UpdatePhase.PlanIntegrity;
            if (planFile.Hash() != args[3])
                throw new InvalidDataException("Update plan checksum changed.");
            phase = UpdatePhase.PlanRead;
            var plan = UpdateProtocol.Read<UpdatePlan>(
                planFile.Read(UpdateProtocol.MaximumPlanBytes)
            );
            phase = UpdatePhase.PlanValidation;
            UpdateProtocol.Validate(plan);
            if (UpdateProtocol.Portable(plan) != (portable is not null))
                throw new InvalidDataException(
                    "The update deployment does not match the authenticated Manager."
                );
            if (
                portable is not null
                && !UpdatePaths.Equal(plan.InstallDirectory, portable.Application)
            )
                throw new InvalidDataException(
                    "The portable update targets another application directory."
                );
            phase = UpdatePhase.PlanLocation;
            UpdatePaths.ValidatePlanLocation(args[1], plan, root);
            plan = plan with
            {
                InstallDirectory = UpdatePaths.Local(plan.InstallDirectory),
                InstallerPath = UpdatePaths.Local(plan.InstallerPath),
            };
            string operation = UpdatePaths.OperationDirectory(root, plan.OperationId);
            phase = UpdatePhase.OperationOwnership;
            ProfileOwnership.Verify(operation);
            // Record later refusals only inside this validated, exclusively claimed operation.
            phase = UpdatePhase.OperationLock;
            using var once = new FileStream(
                Path.Combine(operation, "apply.lock"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            );
            trustedOperation = operation;
            trustedPlan = plan;
            phase = UpdatePhase.OperationLog;
            log = new OperationLog(operation);
            UpdateWorkingDirectory.Detach(operation);
            log.Write("Update helper working directory detached into its validated operation.");
            log.Write("Validating pinned installer and installation ownership.");
            phase = UpdatePhase.PackageOwnership;
            ProfileOwnership.Verify(Path.GetDirectoryName(plan.InstallerPath)!);
            phase = UpdatePhase.InstallationPaths;
            UpdatePaths.NoRedirects(plan.InstallDirectory);
            if (
                plan.InstallDirectory.Equals(
                    Path.GetPathRoot(plan.InstallDirectory),
                    StringComparison.OrdinalIgnoreCase
                )
                || UpdatePaths.Equal(plan.InstallDirectory, root)
                || plan.InstallDirectory.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
                || root.StartsWith(
                    plan.InstallDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidDataException("Installation and update cache overlap.");
            phase = UpdatePhase.Registration;
            var registry = portable is null
                ? InstallationRegistration.Capture(plan.InstallDirectory)
                : null;
            phase = UpdatePhase.InstallationOwnership;
            ProfileOwnership.Verify(plan.InstallDirectory);
            phase = UpdatePhase.InstallationParentOwnership;
            ProfileOwnership.Verify(Path.GetDirectoryName(plan.InstallDirectory)!);
            phase = UpdatePhase.Package;
            using var package = new PackageLease(
                plan.InstallerPath,
                plan.Version,
                plan.InstallerSha256,
                root,
                portable is not null
            );
            if (portable is not null)
                _ = PortablePayload.ValidateArchive(package, plan.Version);
            phase = UpdatePhase.FallbackPackage;
            using var fallback = plan.Fallback is { } previous
                ? new PackageLease(
                    previous.InstallerPath,
                    previous.Version,
                    previous.InstallerSha256,
                    root,
                    portable is not null
                )
                : null;
            phase = UpdatePhase.ParentIdentity;
            using var parent = new ParentLease(plan);
            using var original = portable is null ? null : new PortableInventory(portable.Root);
            string installedVersion = registry?.Version ?? original!.Descriptor.Version;
            if (
                plan.Fallback is { } old
                && !UpdateProtocol.SameVersion(old.Version, installedVersion)
            )
                throw new InvalidDataException("Fallback does not identify the installed version.");
            IApplyEffects effects = portable is null
                ? new NativeApplyEffects(plan, operation, parent, package, registry!, log.Write)
                : new PortableApplyEffects(
                    plan,
                    operation,
                    parent,
                    package,
                    portable,
                    original!,
                    log.Write
                );
            phase = UpdatePhase.InstalledImage;
            effects.VerifyInstalled(installedVersion);
            phase = UpdatePhase.Cancellation;
            effects.CheckCancellation();
            phase = UpdatePhase.Readiness;
            Evidence.WriteNew(
                Path.Combine(operation, "ready.json"),
                new UpdateReady(1, plan.OperationId, plan.ParentPid, plan.ParentStartedAtUtc)
            );
            ready = true;
            log.Write("Ready. Waiting for the authenticated Manager to exit normally.");
            phase = UpdatePhase.Apply;
            var result = await ApplyEngine.Run(plan, effects, log.Write).ConfigureAwait(false);
            log.Write(result.Outcome + ": " + result.Detail);
            phase = UpdatePhase.Outcome;
            Evidence.WriteNew(Path.Combine(operation, "outcome.json"), result);
            if (result.Outcome == "Installed" && effects is PortableApplyEffects portableEffects)
                _ = portableEffects.CleanupCompleted();
            return result.Outcome is "Installed" or "RolledBack" ? 0
                : result.Outcome == "Unknown" ? 75
                : 20;
        }
        catch (OperationCanceledException ex)
        {
            AdmissionDiagnostics.Write(phase, ex);
            Refused("Failed", "The update was cancelled before changing the installation.", ex);
            return 20;
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex))
        {
            AdmissionDiagnostics.Write(phase, ex);
            Refused(
                ready ? "Unknown" : "Failed",
                ready
                    ? "The update outcome could not be confirmed. Available evidence and recovery copies were preserved."
                    : "Update preparation was refused before changing the installation.",
                ex
            );
            return 65;
        }
        finally
        {
            log?.Dispose();
        }

        void Refused(string outcome, string detail, Exception failure)
        {
            try
            {
                log?.Write(AdmissionDiagnostics.Encode(phase, failure));
                ApplyDiagnostics.Failure(
                    log is null ? null : log.Write,
                    "admission-" + phase,
                    failure
                );
                log?.Write(outcome + ": " + detail);
                if (
                    trustedOperation is not null
                    && trustedPlan is not null
                    && !File.Exists(Path.Combine(trustedOperation, "outcome.json"))
                )
                    Evidence.WriteNew(
                        Path.Combine(trustedOperation, "outcome.json"),
                        new UpdateOutcome(
                            1,
                            trustedPlan.OperationId,
                            outcome,
                            detail,
                            trustedPlan.Version,
                            null,
                            null,
                            DateTimeOffset.UtcNow
                        )
                    );
            }
            catch (Exception ex) when (UpdateProtocol.Known(ex)) { }
        }
    }
}
