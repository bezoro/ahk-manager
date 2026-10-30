using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Bezoro.AhkManager.UpdateHost;

internal sealed record PortableSignatureRequest(
    string ManifestPath,
    string SignaturePath,
    string[] Publishers
);

// Read-only publisher authentication. Paths are data, never commands or apply authority.
internal static class PortableSignatureVerification
{
    internal static int Run()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("Publisher verification requires Windows.");
            var layout =
                PortableLayout.FromParent()
                ?? throw new InvalidDataException("The parent is not a portable Manager.");
            PortableInitialization.VerifySourceHelper(layout);
            ProfileOwnership.Verify(layout.Root);
            ProfileOwnership.Verify(layout.Application);
            var input = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = Console.In.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (input.Length + count > UpdateProtocol.MaximumPlanBytes)
                    throw new InvalidDataException(
                        "Publisher verification request exceeds its limit."
                    );
                input.Append(buffer, 0, count);
            }
            var request = UpdateProtocol.Read<PortableSignatureRequest>(
                Encoding.UTF8.GetBytes(input.ToString())
            );
            if (
                request.Publishers is not { Length: > 0 and <= 32 }
                || request.Publishers.Any(name =>
                    string.IsNullOrWhiteSpace(name)
                    || name.Length > 1024
                    || name.Any(c => char.IsControl(c))
                )
            )
                throw new InvalidDataException(
                    "Expected publisher identities are missing or invalid."
                );
            using var manifest = FileLease.Open(
                request.ManifestPath,
                UpdateProtocol.MaximumPlanBytes
            );
            using var signature = FileLease.Open(request.SignaturePath, 64 * 1024);
            byte[] manifestBytes = manifest.Read(UpdateProtocol.MaximumPlanBytes);
            Verify(manifestBytes, signature.Read(64 * 1024), request.Publishers);
            WriteResult(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
            return 0;
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex) || ex is CryptographicException)
        {
            WriteResult(null);
            return 65;
        }
    }

    internal static void Verify(
        byte[] manifest,
        byte[] signature,
        string[] publishers,
        X509Certificate2? testTrustRoot = null
    )
    {
        var cms = new SignedCms(new ContentInfo(manifest), detached: true);
        cms.Decode(signature);
        if (
            cms.SignerInfos.Count != 1
            || cms.Certificates.Count is < 1 or > 16
            || cms.SignerInfos[0].CounterSignerInfos.Count != 0
            || cms.SignerInfos[0].DigestAlgorithm.Value != "2.16.840.1.101.3.4.2.1"
            || !cms.ContentInfo.Content.AsSpan().SequenceEqual(manifest)
        )
            throw new CryptographicException("Exactly one portable publisher is required.");
        cms.CheckSignature(verifySignatureOnly: true);
        var certificate =
            cms.SignerInfos[0].Certificate
            ?? throw new CryptographicException("The publisher certificate is missing.");
        if (
            !publishers.Contains(
                certificate.GetNameInfo(X509NameType.SimpleName, false),
                StringComparer.Ordinal
            )
        )
            throw new CryptographicException("The portable publisher does not match policy.");
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        var extended = certificate
            .Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SingleOrDefault();
        if (
            usage is null
            || (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0
            || extended is null
            || !extended.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3")
        )
            throw new CryptographicException("The certificate is not a code-signing identity.");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = testTrustRoot is null
            ? X509RevocationMode.Online
            : X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
        chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
        if (testTrustRoot is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(testTrustRoot);
        }
        if (!chain.Build(certificate))
            throw new CryptographicException("The publisher certificate chain is not trusted.");
    }

    static void WriteResult(string? manifestSha256)
    {
        try
        {
            Console.WriteLine(
                manifestSha256 is null
                    ? "{\"valid\":false}"
                    : "{\"valid\":true,\"manifestSha256\":\"" + manifestSha256 + "\"}"
            );
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }
}
