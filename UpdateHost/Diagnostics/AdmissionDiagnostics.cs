using System.ComponentModel;
using System.Security;
using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal enum UpdatePhase
{
    Arguments,
    HostLocation,
    CacheOwnership,
    HostOwnership,
    HostLease,
    HostIntegrity,
    PlanLease,
    PlanIntegrity,
    PlanRead,
    PlanValidation,
    PlanLocation,
    OperationOwnership,
    PackageOwnership,
    InstallationPaths,
    InstallationOwnership,
    InstallationParentOwnership,
    OperationLock,
    OperationLog,
    Package,
    FallbackPackage,
    Registration,
    ParentIdentity,
    InstalledImage,
    Cancellation,
    Readiness,
    Apply,
    Outcome,
}

// Refusal output uses fixed codes before any operation path is trusted.
internal static class AdmissionDiagnostics
{
    internal const string Prefix = "AHK_UPDATE_REFUSAL:";

    internal static string Encode(UpdatePhase phase, Exception exception)
    {
        string name = phase switch
        {
            UpdatePhase.Arguments => "arguments",
            UpdatePhase.HostLocation => "host-location",
            UpdatePhase.CacheOwnership => "cache-ownership",
            UpdatePhase.HostOwnership => "host-ownership",
            UpdatePhase.HostLease => "host-lease",
            UpdatePhase.HostIntegrity => "host-integrity",
            UpdatePhase.PlanLease => "plan-lease",
            UpdatePhase.PlanIntegrity => "plan-integrity",
            UpdatePhase.PlanRead => "plan-read",
            UpdatePhase.PlanValidation => "plan-validation",
            UpdatePhase.PlanLocation => "plan-location",
            UpdatePhase.OperationOwnership => "operation-ownership",
            UpdatePhase.PackageOwnership => "package-ownership",
            UpdatePhase.InstallationPaths => "installation-paths",
            UpdatePhase.InstallationOwnership => "installation-ownership",
            UpdatePhase.InstallationParentOwnership => "installation-parent-ownership",
            UpdatePhase.OperationLock => "operation-lock",
            UpdatePhase.OperationLog => "operation-log",
            UpdatePhase.Package => "package",
            UpdatePhase.FallbackPackage => "fallback-package",
            UpdatePhase.Registration => "registration",
            UpdatePhase.ParentIdentity => "parent-identity",
            UpdatePhase.InstalledImage => "installed-image",
            UpdatePhase.Cancellation => "cancellation",
            UpdatePhase.Readiness => "readiness",
            UpdatePhase.Apply => "apply",
            UpdatePhase.Outcome => "outcome",
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
        string code = exception switch
        {
            OperationCanceledException => "cancelled",
            InvalidDataException => "invalid-data",
            UnauthorizedAccessException => "access-denied",
            FileNotFoundException => "file-not-found",
            DirectoryNotFoundException => "directory-not-found",
            ArgumentException => "invalid-argument",
            InvalidOperationException => "invalid-state",
            JsonException => "invalid-json",
            SecurityException => "security-error",
            TimeoutException => "timeout",
            Win32Exception native => FileError(native.NativeErrorCode, "native-error"),
            IOException io => FileError(io.HResult, "io-error"),
            _ => throw new ArgumentException(
                "Unsupported diagnostic exception.",
                nameof(exception)
            ),
        };
        // Both values are closed ASCII vocabularies. Never serialize exception
        // messages, paths, stack traces, or caller-controlled argument text.
        return Prefix
            + "{\"schemaVersion\":1,\"phase\":\""
            + name
            + "\",\"code\":\""
            + code
            + "\"}";
    }

    internal static void Write(UpdatePhase phase, Exception exception)
    {
        try
        {
            Console.Error.WriteLine(Encode(phase, exception));
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    static string FileError(int error, string fallback) =>
        error switch
        {
            2 or unchecked((int)0x80070002) => "file-not-found",
            3 or unchecked((int)0x80070003) => "directory-not-found",
            5 or unchecked((int)0x80070005) => "access-denied",
            32 or unchecked((int)0x80070020) => "sharing-violation",
            33 or unchecked((int)0x80070021) => "lock-violation",
            _ => fallback,
        };
}
