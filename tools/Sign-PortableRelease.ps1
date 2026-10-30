#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1')
$certificate = $env:CSC_LINK
$publisher = $env:AHK_PUBLISHER_NAME
if ([string]::IsNullOrWhiteSpace($certificate) -and [string]::IsNullOrWhiteSpace($publisher)) { return $false }
if ([string]::IsNullOrWhiteSpace($certificate) -or [string]::IsNullOrWhiteSpace($publisher)) { throw 'Portable signing requires both the configured certificate and public publisher name.' }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_REPOSITORY -cne 'bezoro/ahk-manager' -or $env:GITHUB_EVENT_NAME -cnotin @('push', 'workflow_run')) { throw 'Portable signing requires the trusted main-branch publisher.' }
$null = Read-PortableReleaseManifest -ManifestPath $ManifestPath -ArchivePath $ArchivePath
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path ([IO.Path]::GetDirectoryName($ManifestPath)) 'ahk-manager-portable-release.json.p7s' }
if (-not [IO.Path]::IsPathFullyQualified($OutputPath) -or [IO.Path]::GetFileName($OutputPath) -cne 'ahk-manager-portable-release.json.p7s') { throw 'Use the canonical absolute portable signature path.' }
if (Test-Path -LiteralPath $OutputPath) {
    Assert-PortableManifestSignature -ManifestPath $ManifestPath -SignaturePath $OutputPath -Publisher $publisher
    return $true
}
$directory = Join-Path $env:RUNNER_TEMP ('ahk-portable-signing-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$certificatePath = Join-Path $directory 'publisher.pfx'
$certificates = $null
$chain = $null
try {
    # Protected main-branch credentials are read by trusted code only. Neither
    # portable payload files nor any code in the pull request is executed.
    if ($certificate.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        try { Invoke-WebRequest -Uri $certificate -OutFile $certificatePath -MaximumRedirection 3 | Out-Null }
        catch { throw 'The configured portable signing certificate could not be downloaded.' }
    }
    elseif ([IO.Path]::IsPathFullyQualified($certificate) -and (Test-Path -LiteralPath $certificate -PathType Leaf)) { [IO.File]::Copy($certificate, $certificatePath) }
    else {
        try { [IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($certificate)) }
        catch { throw 'The configured portable signing certificate is invalid.' }
    }
    if ((Get-Item -LiteralPath $certificatePath).Length -gt 1MB) { throw 'The portable signing certificate exceeds its limit.' }
    try { $certificates = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12CollectionFromFile($certificatePath, $env:CSC_KEY_PASSWORD, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet) }
    catch { throw 'The configured portable signing key could not be loaded.' }
    $signers = @($certificates | Where-Object { $_.HasPrivateKey -and $_.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ceq $publisher })
    if ($signers.Count -ne 1) { throw 'The configured portable signing key does not identify exactly one expected publisher.' }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(10)
    $chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
    $chain.ChainPolicy.ExtraStore.AddRange($certificates)
    if (-not $chain.Build($signers[0])) { throw 'The portable publisher certificate is not currently trusted for code signing.' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($ManifestPath)), $true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($signers[0])
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    # Include validated intermediate certificates so verification does not rely
    # on a client fetching an intermediate from a publisher-controlled URL.
    for ($index = 1; $index -lt $chain.ChainElements.Count - 1; $index++) { $signer.Certificates.Add($chain.ChainElements[$index].Certificate) | Out-Null }
    $cms.ComputeSignature($signer, $true)
    $bytes = $cms.Encode()
    if ($bytes.Length -gt 65536) { throw 'The portable signature exceeds its limit.' }
    $stream = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    Assert-PortableManifestSignature -ManifestPath $ManifestPath -SignaturePath $OutputPath -Publisher $publisher
    Write-Host 'Portable manifest signed and its publisher verified.'
    return $true
}
finally {
    if ($null -ne $chain) { $chain.Dispose() }
    if ($null -ne $certificates) { foreach ($item in $certificates) { $item.Dispose() } }
    Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
}
