using System.Security.Cryptography;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Bezoro.AhkManager.Backend;

internal sealed class ElevationFiles : IDisposable
{
    readonly string backend,
        host,
        archive;
    readonly FileStream backendLease,
        hostLease,
        archiveLease;
    public string BackendHash { get; }
    public string HostHash { get; }
    public string ArchiveHash { get; }

    public ElevationFiles(string backend, string host, string archive)
    {
        this.backend = ScriptPaths.Canonical(backend, false);
        this.host = ScriptPaths.Canonical(host, false);
        this.archive = ScriptPaths.Canonical(archive, false);
        backendLease = new(this.backend, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            hostLease = new(this.host, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                archiveLease = new(this.archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch
            {
                hostLease.Dispose();
                throw;
            }
        }
        catch
        {
            backendLease.Dispose();
            throw;
        }

        BackendHash = Hash(backendLease);
        HostHash = Hash(hostLease);
        ArchiveHash = Hash(archiveLease);
    }

    static string Hash(FileStream file)
    {
        file.Position = 0;
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    public bool Valid() =>
        ScriptPaths.Equal(ScriptPaths.Canonical(backend, false), backend)
        && ScriptPaths.Equal(ScriptPaths.Canonical(host, false), host)
        && ScriptPaths.Equal(ScriptPaths.Canonical(archive, false), archive)
        && Hash(backendLease) == BackendHash
        && Hash(hostLease) == HostHash
        && Hash(archiveLease) == ArchiveHash;

    public void Dispose()
    {
        archiveLease.Dispose();
        hostLease.Dispose();
        backendLease.Dispose();
    }
}
