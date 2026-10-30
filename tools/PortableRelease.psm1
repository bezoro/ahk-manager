#requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')

function ConvertFrom-PortableReleaseJson {
    param([Parameter(Mandatory = $true)][string]$Json)
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 65536) { throw 'The portable manifest exceeds its limit.' }
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        $root = $document.RootElement
        if ((@($root.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'archive,commit,notes,publishedAt,schemaVersion,updateProtocol,version') { throw 'Unexpected or duplicate portable manifest fields.' }
        $package = $root.GetProperty('archive')
        if ((@($package.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'bytes,name,sha256,sha512') { throw 'Unexpected or duplicate portable archive fields.' }
        $version = $root.GetProperty('version').GetString()
        $commit = $root.GetProperty('commit').GetString()
        $publishedAt = $root.GetProperty('publishedAt').GetString()
        $notes = $root.GetProperty('notes').GetString()
        $name = $package.GetProperty('name').GetString()
        $bytes = $package.GetProperty('bytes').GetInt64()
        $sha256 = $package.GetProperty('sha256').GetString()
        $sha512 = $package.GetProperty('sha512').GetString()
        $date = [DateTimeOffset]::MinValue
        if ($root.GetProperty('schemaVersion').GetInt32() -ne 1 -or $root.GetProperty('updateProtocol').GetInt32() -ne 2 -or -not (Test-ReleaseVersion $version) -or $commit -cnotmatch '\A[0-9a-f]{40}\z' -or $null -eq $notes -or $notes.Length -gt 4000 -or $notes.Contains([char]0)) { throw 'Invalid portable release identity.' }
        if ($null -eq $publishedAt -or $publishedAt -cnotmatch '\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z\z' -or -not [DateTimeOffset]::TryParse($publishedAt, [ref]$date)) { throw 'Invalid portable publication timestamp.' }
        if ($name -cne "AHK-Manager-$version-Portable-x64.zip" -or $bytes -le 0 -or $bytes -gt 1GB -or $sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or $null -eq $sha512) { throw 'Invalid portable archive identity or size.' }
        try { $decoded = [Convert]::FromBase64String($sha512) } catch { throw 'Invalid portable SHA512 checksum.' }
        if ($decoded.Length -ne 64 -or [Convert]::ToBase64String($decoded) -cne $sha512) { throw 'Invalid portable SHA512 checksum.' }
        return [pscustomobject]@{ Version = $version; Commit = $commit; PublishedAt = $publishedAt; Notes = $notes; ArchiveName = $name; ArchiveBytes = $bytes; Sha256 = $sha256; Sha512 = $sha512 }
    }
    finally { $document.Dispose() }
}

function Read-PortableReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [string]$ExpectedVersion,
        [string]$ExpectedCommit,
        [string]$ExpectedPublishedAt,
        [AllowEmptyString()][string]$ExpectedNotes
    )
    if (-not [IO.Path]::IsPathFullyQualified($ManifestPath) -or -not [IO.Path]::IsPathFullyQualified($ArchivePath) -or [IO.Path]::GetFileName($ManifestPath) -cne 'ahk-manager-portable-release.json') { throw 'Use absolute portable archive and manifest paths with their canonical names.' }
    $manifest = Get-Item -LiteralPath $ManifestPath
    $archive = Get-Item -LiteralPath $ArchivePath
    if ($manifest.PSIsContainer -or ($manifest.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $manifest.Length -le 0 -or $manifest.Length -gt 65536 -or $archive.PSIsContainer -or ($archive.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The portable release files are not bounded regular files.' }
    $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
    $identity = ConvertFrom-PortableReleaseJson $text
    foreach ($field in @('Version', 'Commit', 'PublishedAt', 'Notes')) {
        $parameter = 'Expected' + $field
        if ($PSBoundParameters.ContainsKey($parameter) -and $identity.$field -cne $PSBoundParameters[$parameter]) { throw "The portable $field does not match the installer release." }
    }
    if ($archive.Name -cne $identity.ArchiveName -or $archive.Length -ne $identity.ArchiveBytes) { throw 'The portable archive name or length does not match its manifest.' }
    $sha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sha512 = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA512).Hash))
    if ($sha256 -cne $identity.Sha256 -or $sha512 -cne $identity.Sha512) { throw 'The portable archive checksums do not match its manifest.' }
    $identity | Add-Member -NotePropertyName ManifestSha256 -NotePropertyValue ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant())
    $identity | Add-Member -NotePropertyName ManifestPath -NotePropertyValue $manifest.FullName
    $identity | Add-Member -NotePropertyName ArchivePath -NotePropertyValue $archive.FullName
    return $identity
}

function Read-PortableManifestSigner {
    param([byte[]]$ManifestBytes, [byte[]]$SignatureBytes)
    if ($ManifestBytes.Length -le 0 -or $ManifestBytes.Length -gt 65536 -or $SignatureBytes.Length -le 0 -or $SignatureBytes.Length -gt 65536) { throw 'The portable manifest or signature exceeds its limit.' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($ManifestBytes), $true)
    $cms.Decode($SignatureBytes)
    if (-not $cms.Detached -or $cms.ContentInfo.ContentType.Value -cne '1.2.840.113549.1.7.1' -or [Convert]::ToBase64String($cms.ContentInfo.Content) -cne [Convert]::ToBase64String($ManifestBytes) -or $cms.SignerInfos.Count -ne 1 -or $cms.Certificates.Count -gt 16 -or $cms.SignerInfos[0].DigestAlgorithm.Value -cne '2.16.840.1.101.3.4.2.1' -or $null -eq $cms.SignerInfos[0].Certificate -or $cms.SignerInfos[0].CounterSignerInfos.Count -ne 0) { throw 'The portable manifest signature has an unsupported signer.' }
    $cms.CheckSignature($true)
    return [pscustomobject]@{ Certificate = $cms.SignerInfos[0].Certificate; Certificates = $cms.Certificates; ManifestSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($ManifestBytes)).ToLowerInvariant(); SignatureSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($SignatureBytes)).ToLowerInvariant(); Bytes = $SignatureBytes.Length }
}

function Assert-PortableManifestSignature {
    param([Parameter(Mandatory = $true)][string]$ManifestPath, [Parameter(Mandatory = $true)][string]$SignaturePath, [Parameter(Mandatory = $true)][string]$Publisher, [switch]$PassThru)
    if ([string]::IsNullOrWhiteSpace($Publisher) -or $Publisher.Length -gt 1024 -or $Publisher -match '[\x00\r\n]' -or -not [IO.Path]::IsPathFullyQualified($SignaturePath) -or [IO.Path]::GetFileName($SignaturePath) -cne 'ahk-manager-portable-release.json.p7s') { throw 'A canonical signature path and configured portable publisher are required.' }
    $manifest = Get-Item -LiteralPath $ManifestPath
    $signature = Get-Item -LiteralPath $SignaturePath
    if ($manifest.PSIsContainer -or ($manifest.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $manifest.Length -le 0 -or $manifest.Length -gt 65536 -or $signature.PSIsContainer -or ($signature.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $signature.Length -le 0 -or $signature.Length -gt 65536) { throw 'The portable manifest and signature must be bounded regular files.' }
    $signer = Read-PortableManifestSigner ([IO.File]::ReadAllBytes($ManifestPath)) ([IO.File]::ReadAllBytes($SignaturePath))
    if ($signer.Certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne $Publisher) { throw 'The portable signature does not match the configured publisher.' }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(10)
        $chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
        $chain.ChainPolicy.ExtraStore.AddRange($signer.Certificates)
        if (-not $chain.Build($signer.Certificate)) { throw 'The portable publisher certificate is not currently trusted for code signing.' }
    }
    finally { $chain.Dispose() }
    if ($PassThru) { return [pscustomobject]@{ ManifestSha256 = $signer.ManifestSha256; SignatureSha256 = $signer.SignatureSha256; Bytes = $signer.Bytes } }
}

Export-ModuleMember -Function ConvertFrom-PortableReleaseJson, Read-PortableReleaseManifest, Assert-PortableManifestSignature
