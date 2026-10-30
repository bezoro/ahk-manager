#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$FixtureRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][hashtable]$RequestFixture,
    [Parameter(Mandatory = $true)][string]$SourceManifestPath,
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][object[]]$LegacyEntries
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1')
Import-Module (Join-Path $PSScriptRoot 'TestReleaseArtifacts.psm1')
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute portable test directory.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Assert-PortableCheck([bool]$Value, [string]$Name) { if (-not $Value) { throw $Name }; $checks.Add($Name); Write-Host "PASS $Name" }
function Assert-PortableRefused([scriptblock]$Action, [string]$Name) { $refused = $false; try { & $Action | Out-Null } catch { $refused = $true }; Assert-PortableCheck $refused $Name }
function New-PortableArtifactFixture([string]$Path, [object[]]$Entries) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $Entries) {
            $entry = $archive.CreateEntry($item.name)
            $stream = $entry.Open()
            try { $stream.Write($item.value, 0, $item.value.Length) } finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return $Path
}
$source = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($SourceManifestPath))
try {
    $publishedAt = $source.RootElement.GetProperty('publishedAt').GetString()
    $notes = $source.RootElement.GetProperty('notes').GetString()
}
finally { $source.Dispose() }
$archiveName = "AHK-Manager-$Version-Portable-x64.zip"
$archivePath = Join-Path $OutputRoot $archiveName
[IO.File]::WriteAllBytes($archivePath, [byte[]](3, 1, 4, 1, 5, 9))
$portableSource = [ordered]@{
    schemaVersion = 1; updateProtocol = 2; version = $Version; commit = $Commit; publishedAt = $publishedAt; notes = $notes
    archive = [ordered]@{ name = $archiveName; bytes = (Get-Item -LiteralPath $archivePath).Length; sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant(); sha512 = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash)) }
}
$portableText = $portableSource | ConvertTo-Json -Depth 5
$portableManifestPath = Join-Path $OutputRoot 'ahk-manager-portable-release.json'
[IO.File]::WriteAllText($portableManifestPath, $portableText, [Text.UTF8Encoding]::new($false))
$identity = Read-PortableReleaseManifest -ManifestPath $portableManifestPath -ArchivePath $archivePath -ExpectedVersion $Version -ExpectedCommit $Commit -ExpectedPublishedAt $publishedAt -ExpectedNotes $notes
Assert-PortableCheck ($identity.Version -ceq $Version -and $identity.Commit -ceq $Commit -and $identity.ArchiveBytes -eq 6 -and $identity.Notes -ceq $notes) 'portable metadata retains exact strings and both archive checksums without executing its payload'
foreach ($case in @(
    @{ field = 'schemaVersion'; value = 2 }, @{ field = 'updateProtocol'; value = 1 }, @{ field = 'commit'; value = 'A' * 40 }, @{ field = 'publishedAt'; value = '2026-02-30T00:00:00Z' }, @{ field = 'version'; value = '1.0.0-beta.1' }, @{ field = 'notes'; value = ('x' * 4001) }, @{ field = 'extra'; value = 'unrecognized' }
)) {
    $copy = @{}; foreach ($key in $portableSource.Keys) { $copy[$key] = $portableSource[$key] }; $copy[$case.field] = $case.value
    Assert-PortableRefused { ConvertFrom-PortableReleaseJson ($copy | ConvertTo-Json -Depth 5) } "portable metadata rejects invalid $($case.field)"
}
foreach ($case in @(
    @{ field = 'name'; value = '../payload.zip' }, @{ field = 'bytes'; value = 0 }, @{ field = 'bytes'; value = 1GB + 1L }, @{ field = 'sha256'; value = 'A' * 64 }, @{ field = 'sha512'; value = 'not-base64' }, @{ field = 'extra'; value = 1 }
)) {
    $copy = @{}; foreach ($key in $portableSource.Keys) { $copy[$key] = $portableSource[$key] }
    $package = @{}; foreach ($key in $portableSource.archive.Keys) { $package[$key] = $portableSource.archive[$key] }; $copy.archive = $package; $copy.archive[$case.field] = $case.value
    Assert-PortableRefused { ConvertFrom-PortableReleaseJson ($copy | ConvertTo-Json -Depth 5) } "portable metadata rejects invalid archive $($case.field)"
}
Assert-PortableRefused { ConvertFrom-PortableReleaseJson $portableText.Replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1') } 'portable metadata rejects duplicate root fields'
Assert-PortableRefused { ConvertFrom-PortableReleaseJson $portableText.Replace('"name":', '"name": "duplicate", "name":') } 'portable metadata rejects duplicate nested fields'
foreach ($field in @('Version', 'Commit', 'PublishedAt', 'Notes')) {
    $expected = @{ ManifestPath = $portableManifestPath; ArchivePath = $archivePath }; $expected['Expected' + $field] = 'different'
    Assert-PortableRefused { Read-PortableReleaseManifest @expected } "portable family refuses mismatched $field"
}
[IO.File]::WriteAllBytes($archivePath, [byte[]](3, 1, 4, 1, 5, 8))
Assert-PortableRefused { Read-PortableReleaseManifest -ManifestPath $portableManifestPath -ArchivePath $archivePath } 'modified portable archive bytes are rejected before publication'
[IO.File]::WriteAllBytes($archivePath, [byte[]](3, 1, 4, 1, 5, 9))
$entries = @($LegacyEntries) + @(@{ name = $archiveName; value = [IO.File]::ReadAllBytes($archivePath) }, @{ name = 'ahk-manager-portable-release.json'; value = [Text.Encoding]::UTF8.GetBytes($portableText) })
$familyZip = New-PortableArtifactFixture (Join-Path $OutputRoot 'family.zip') $entries
$familyIdentity = Read-TestReleaseArtifactIdentity $familyZip
$extracted = Join-Path $OutputRoot 'extracted'
Expand-TestReleaseArtifact -ArchivePath $familyZip -OutputRoot $extracted -Version $Version
$null = Read-PortableReleaseManifest -ManifestPath (Join-Path $extracted 'ahk-manager-portable-release.json') -ArchivePath (Join-Path $extracted $archiveName) -ExpectedCommit $Commit
Assert-PortableCheck ($familyIdentity.HasPortable -and $familyIdentity.Version -ceq $Version -and (Get-ChildItem -LiteralPath $extracted -File).Count -eq 5) 'the five-file Actions artifact binds both editions and extracts only the complete expected family'
foreach ($bad in @(@($entries[0], $entries[1], $entries[2], $entries[3]), @($entries[0], $entries[1], $entries[2], $entries[3], @{ name = 'ahk-manager-portable-release.json'; value = [Text.Encoding]::UTF8.GetBytes($portableText.Replace($Commit, ('b' * 40))) }), @($entries[0], $entries[1], $entries[2], @{ name = '../escape.zip'; value = [byte[]](1) }, $entries[4]), @($entries[0], $entries[1], $entries[2], $entries[3], $entries[4], @{ name = 'extra'; value = [byte[]](1) }))) {
    $path = New-PortableArtifactFixture (Join-Path $OutputRoot ([Guid]::NewGuid().ToString('N') + '.zip')) $bad
    Assert-PortableRefused { Read-TestReleaseArtifactIdentity $path } 'incomplete mixed-source or unexpected portable artifact families are rejected'
}
$publisher = Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1'
$parameters = @{ InstallerPath = $InstallerPath; ManifestPath = $SourceManifestPath; PortableArchivePath = $archivePath; PortableManifestPath = $portableManifestPath; Repository = 'bezoro/ahk-manager'; Commit = $Commit; Channel = 'test'; TestRunId = 7 }
& $publisher @parameters -ValidateOnly
$RequestFixture.Release = $null; $RequestFixture.Tag = $null; $RequestFixture.Published = $null; $RequestFixture.ChangeHead = $false
$RequestFixture.Responses['https://api.github.com/repos/bezoro/ahk-manager/pulls/2'].head.sha = $Commit
& $publisher @parameters
Assert-PortableCheck (-not $RequestFixture.Release.draft -and $RequestFixture.Release.assets.Count -eq 4) 'installer and portable assets are all uploaded before a single prerelease publication'
$before = $RequestFixture.Calls.Count
& $publisher @parameters
Assert-PortableCheck (@($RequestFixture.Calls.GetRange($before, $RequestFixture.Calls.Count - $before) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'an identical published portable family remains an immutable read-only no-op'
$allAssets = $RequestFixture.Release.assets
$RequestFixture.Release.assets = @($allAssets | Where-Object name -CNE 'ahk-manager-portable-release.json')
$before = $RequestFixture.Calls.Count
Assert-PortableRefused { & $publisher @parameters } 'an incomplete published portable release never receives new assets'
Assert-PortableCheck (@($RequestFixture.Calls.GetRange($before, $RequestFixture.Calls.Count - $before) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'missing portable publication assets cause no mutation'
Assert-PortableRefused { & (Join-Path $PSScriptRoot 'Get-ReleasePublicationStatus.ps1') -Repository 'bezoro/ahk-manager' -Version $Version -Commit $Commit -Portable } 'portable status refuses an installer-only or incomplete immutable release'
$RequestFixture.Release.assets = $allAssets
$status = & (Join-Path $PSScriptRoot 'Get-ReleasePublicationStatus.ps1') -Repository 'bezoro/ahk-manager' -Version $Version -Commit $Commit -Portable
Assert-PortableCheck ($status -ceq 'Published') 'portable publication status confirms the complete four-asset family'
$status = & (Join-Path $PSScriptRoot 'Get-ReleasePublicationStatus.ps1') -Repository 'bezoro/ahk-manager' -Version $Version -Commit $Commit -PortableArchivePath $archivePath -PortableManifestPath $portableManifestPath
Assert-PortableCheck ($status -ceq 'Published') 'preview no-op status compares immutable portable asset hashes with the approved artifact'
$portableAsset = @($RequestFixture.Release.assets | Where-Object name -CEQ $archiveName)[0]
$originalDigest = $portableAsset.digest
$portableAsset.digest = 'sha256:' + ('0' * 64)
Assert-PortableRefused { & (Join-Path $PSScriptRoot 'Get-ReleasePublicationStatus.ps1') -Repository 'bezoro/ahk-manager' -Version $Version -Commit $Commit -PortableArchivePath $archivePath -PortableManifestPath $portableManifestPath } 'already-published status cannot hide different portable bytes'
$portableAsset.digest = $originalDigest
Assert-PortableRefused { & (Join-Path $PSScriptRoot 'Get-ReleasePublicationStatus.ps1') -Repository 'bezoro/ahk-manager' -Version $Version -Commit $Commit -Portable -RequirePortableSignature } 'signed portable policy refuses a release missing its sidecar'
$RequestFixture.Release = $null; $RequestFixture.Tag = $null; $RequestFixture.Published = $null; $RequestFixture.FailAssetName = 'ahk-manager-portable-release.json'
Assert-PortableRefused { & $publisher @parameters } 'portable upload interruption prevents publication'
Assert-PortableCheck ($null -eq $RequestFixture.Published -and $RequestFixture.Release.draft -and $RequestFixture.Release.assets.Count -eq 3) 'an interrupted portable family remains a recoverable draft'
$RequestFixture.FailAssetName = $null
& $publisher @parameters
Assert-PortableCheck (-not $RequestFixture.Release.draft -and $RequestFixture.Release.assets.Count -eq 4) 'retry resumes identical draft assets and publishes only the complete family'
$RequestFixture.Release = $null; $RequestFixture.Tag = $null; $RequestFixture.Published = $null; $RequestFixture.MutatePathAfterSetup = $archivePath
Assert-PortableRefused { & $publisher @parameters } 'a portable file changed during upload cannot redefine the approved package digest'
Assert-PortableCheck ($null -eq $RequestFixture.Published -and $RequestFixture.Release.draft) 'concurrent package mutation leaves its mismatched asset family unpublished'
[IO.File]::WriteAllBytes($archivePath, [byte[]](3, 1, 4, 1, 5, 9))
$env:AHK_PUBLISHER_NAME = 'Portable Fixture Publisher'
$before = $RequestFixture.Calls.Count
Assert-PortableRefused { & $publisher @parameters } 'a configured publisher refuses unsigned portable metadata before network access'
Assert-PortableCheck ($RequestFixture.Calls.Count -eq $before) 'publisher policy refusal performs no upload or release lookup'
$env:AHK_PUBLISHER_NAME = ''
$unsigned = & (Join-Path $PSScriptRoot 'Sign-PortableRelease.ps1') -ArchivePath $archivePath -ManifestPath $portableManifestPath
Assert-PortableCheck ($unsigned -is [bool] -and -not $unsigned) 'unconfigured signing remains explicitly unsigned without using credentials'

# Verify real CMS bytes without adding a fixture certificate to any trust store.
# Public validation must still reject this cryptographically valid self-signed key.
$rsa = [Security.Cryptography.RSA]::Create(2048)
$certificate = $null
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Portable Fixture Publisher', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddHours(1))
    $bytes = [IO.File]::ReadAllBytes($portableManifestPath)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes), $true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $cms.ComputeSignature($signer, $true)
    $signatureBytes = $cms.Encode()
    $module = Get-Module PortableRelease
    $verified = & $module { param($Manifest, $Signature) Read-PortableManifestSigner $Manifest $Signature } $bytes $signatureBytes
    Assert-PortableCheck ($verified.Certificate.Thumbprint -ceq $certificate.Thumbprint) 'real detached SHA256 CMS binds the exact manifest bytes and signer certificate'
    Assert-PortableRefused { & $module { param($Manifest, $Signature) Read-PortableManifestSigner $Manifest $Signature } ([Text.Encoding]::UTF8.GetBytes($portableText + ' ')) $signatureBytes } 'a valid CMS sidecar cannot authenticate modified metadata'
    $signaturePath = Join-Path $OutputRoot 'ahk-manager-portable-release.json.p7s'
    [IO.File]::WriteAllBytes($signaturePath, $signatureBytes)
    Assert-PortableRefused { Assert-PortableManifestSignature -ManifestPath $portableManifestPath -SignaturePath $signaturePath -Publisher 'Different Publisher' } 'a portable CMS signer cannot replace the expected publisher'
    Assert-PortableRefused { Assert-PortableManifestSignature -ManifestPath $portableManifestPath -SignaturePath $signaturePath -Publisher 'Portable Fixture Publisher' } 'a self-signed portable certificate is rejected without changing machine trust'
    # Mock only certificate-chain trust inside this process. The real CMS parser
    # still verifies the original signature and returns fingerprints of those
    # exact buffers; replace the file immediately after that verification.
    $originalVerifier = & $module { (Get-Command Assert-PortableManifestSignature).ScriptBlock }
    $fixtureVerifier = {
        param($ManifestPath, $SignaturePath, $Publisher, [switch]$PassThru)
        $identity = Read-PortableManifestSigner ([IO.File]::ReadAllBytes($ManifestPath)) ([IO.File]::ReadAllBytes($SignaturePath))
        if ($identity.Certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne $Publisher) { throw 'Unexpected CMS fixture publisher.' }
        $replacement = [IO.File]::ReadAllBytes($SignaturePath)
        $replacement[0] = $replacement[0] -bxor 1
        [IO.File]::WriteAllBytes($SignaturePath, $replacement)
        if ($PassThru) { return [pscustomobject]@{ ManifestSha256 = $identity.ManifestSha256; SignatureSha256 = $identity.SignatureSha256; Bytes = $identity.Bytes } }
    }
    $savedPublisher = $env:AHK_PUBLISHER_NAME
    $RequestFixture.Release = $null; $RequestFixture.Tag = $null; $RequestFixture.Published = $null
    try {
        & $module { param($Verifier) Set-Item Function:script:Assert-PortableManifestSignature $Verifier; Export-ModuleMember -Function Assert-PortableManifestSignature } $fixtureVerifier
        $env:AHK_PUBLISHER_NAME = 'Portable Fixture Publisher'
        $signatureFolder = Join-Path $OutputRoot 'signature-path'
        [IO.Directory]::CreateDirectory($signatureFolder) | Out-Null
        $alternatePath = [IO.Path]::Combine($signatureFolder, '..', 'ahk-manager-portable-release.json.p7s')
        foreach ($candidatePath in @($signaturePath, $alternatePath)) {
            [IO.File]::WriteAllBytes($signaturePath, $signatureBytes)
            $parameters.PortableSignaturePath = $candidatePath
            $before = $RequestFixture.Calls.Count
            Assert-PortableRefused { & $publisher @parameters } 'a CMS replacement after verification cannot become the approved publication fingerprint'
            Assert-PortableCheck ($RequestFixture.Calls.Count -eq $before) 'canonical and dot-segment signature paths retain the verified byte identity before any publication lookup'
        }
    }
    finally {
        & $module { param($Verifier) Set-Item Function:script:Assert-PortableManifestSignature $Verifier; Export-ModuleMember -Function Assert-PortableManifestSignature } $originalVerifier
        $env:AHK_PUBLISHER_NAME = $savedPublisher
        $parameters.Remove('PortableSignaturePath')
        [IO.File]::WriteAllBytes($signaturePath, $signatureBytes)
    }
    $cms.ComputeSignature($signer, $true)
    Assert-PortableRefused { & $module { param($Manifest, $Signature) Read-PortableManifestSigner $Manifest $Signature } $bytes ($cms.Encode()) } 'multiple portable CMS signers are rejected'
}
finally { if ($null -ne $certificate) { $certificate.Dispose() }; $rsa.Dispose() }
[IO.File]::WriteAllText((Join-Path $OutputRoot 'summary.json'), (@{ passed = $true; checks = $checks.ToArray(); network = 'mocked'; executedPayload = $false; changedCertificateStores = $false } | ConvertTo-Json -Depth 4))
Write-Host "Portable release tooling passed $($checks.Count) checks."
