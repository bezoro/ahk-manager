using System.Security.Cryptography;

namespace Bezoro.AhkManager.UpdateHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
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
            string root = UpdatePaths.UpdatesRoot;
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
            // Portable copies have no managed installation registration to authorize an update.
            phase = UpdatePhase.Registration;
            var registry = InstallationRegistration.Capture(plan.InstallDirectory);
            if (
                plan.Fallback is { } old
                && !UpdateProtocol.SameVersion(old.Version, registry.Version)
            )
                throw new InvalidDataException("Fallback does not identify the installed version.");
            phase = UpdatePhase.InstallationOwnership;
            ProfileOwnership.Verify(plan.InstallDirectory);
            phase = UpdatePhase.InstallationParentOwnership;
            ProfileOwnership.Verify(Path.GetDirectoryName(plan.InstallDirectory)!);
            phase = UpdatePhase.Package;
            using var package = new PackageLease(
                plan.InstallerPath,
                plan.Version,
                plan.InstallerSha256,
                root
            );
            phase = UpdatePhase.FallbackPackage;
            using var fallback = plan.Fallback is { } previous
                ? new PackageLease(
                    previous.InstallerPath,
                    previous.Version,
                    previous.InstallerSha256,
                    root
                )
                : null;
            phase = UpdatePhase.ParentIdentity;
            using var parent = new ParentLease(plan);
            var effects = new NativeApplyEffects(
                plan,
                operation,
                parent,
                package,
                registry,
                log.Write
            );
            phase = UpdatePhase.InstalledImage;
            effects.VerifyInstalled(registry.Version);
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
            var result = await ApplyEngine.Run(plan, effects).ConfigureAwait(false);
            log.Write(result.Outcome + ": " + result.Detail);
            phase = UpdatePhase.Outcome;
            Evidence.WriteNew(Path.Combine(operation, "outcome.json"), result);
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
