using System.Security.Cryptography;
using System.Text.Json;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Backend;

internal static class ElevationReceiptStore
{
    internal static string ProfileRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bezoro",
            "AhkManagerElectron"
        );
    internal static string ChromiumRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Bezoro",
            "AhkManagerElectron",
            "Chromium"
        );

    internal static bool ProfileRootsMatch(string operational, string chromium) =>
        ScriptPaths.Equal(operational, ProfileRoot) && ScriptPaths.Equal(chromium, ChromiumRoot);

    internal static string CoordinationPath(string nonce) =>
        Path.Combine(Path.GetTempPath(), "Bezoro.AhkManager.Administration." + nonce);

    internal static FileStream PinReceipt(
        string path,
        byte[] expected,
        out string hash,
        out string identity
    )
    {
        hash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        identity = "";
        var input = new FileStream(
            ScriptPaths.Canonical(path, false),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        try
        {
            if (
                input.Length > StartupContinuationStore.MaximumBytes
                || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != hash
            )
                throw new ProtocolException("IdentityChanged");
            identity = FileIdentity(input);
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    internal static string FileIdentity(FileStream file)
    {
        if (
            !ProcessNative.GetFileInformationByHandle(file.SafeFileHandle, out var stamp)
            || stamp.Links != 1
        )
            throw new ProtocolException("IdentityChanged");
        return stamp.Volume.ToString("x8")
            + stamp.IndexHigh.ToString("x8")
            + stamp.IndexLow.ToString("x8")
            + ":"
            + file.Length;
    }

    internal static ElevationReceipt ReadCommitted(
        FileStream receiptLease,
        ElevationFrame commit,
        ElevationBootstrapRequest request,
        string nonce,
        ProcessStamp normalStamp,
        ProcessStamp hostStamp,
        ProcessStamp own,
        ElevationFiles files
    )
    {
        if (
            receiptLease.Length > StartupContinuationStore.MaximumBytes
            || FileIdentity(receiptLease) != commit.FileIdentity
            || Convert.ToHexString(SHA256.HashData(receiptLease)).ToLowerInvariant()
                != commit.ReceiptHash
        )
            throw new ProtocolException("IdentityChanged");
        receiptLease.Position = 0;
        var receipt =
            JsonSerializer.Deserialize(receiptLease, Protocol.Json.ElevationReceipt)
            ?? throw new ProtocolException("InvalidRequest");
        if (
            receipt.Version != 1
            || receipt.OperationId != request.OperationId
            || receipt.Nonce != nonce
            || receipt.ReceiptId == Guid.Empty
            || receipt.NormalBackend != normalStamp
            || receipt.NormalHost != hostStamp
            || receipt.Bootstrap != own
            || receipt.BackendHash != files.BackendHash
            || receipt.HostHash != files.HostHash
            || receipt.ArchiveHash != files.ArchiveHash
            || !ProfileRootsMatch(receipt.ProfileRoot, receipt.ChromiumRoot)
            || receipt.ExpiresAt <= DateTimeOffset.UtcNow
            || receipt.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(120)
            || !files.Valid()
        )
            throw new ProtocolException("IdentityChanged");
        if (
            receipt.StartupContinuation is { } continuation
            && (
                continuation.OperationId == Guid.Empty
                || continuation.UserSid != own.Sid
                || continuation.SessionId != own.Session
                || !ScriptPaths.Equal(continuation.ProfileRoot, receipt.ProfileRoot)
                || continuation.ExpiresAt <= DateTimeOffset.UtcNow
            )
        )
            throw new ProtocolException("IdentityChanged");
        return receipt;
    }
}
