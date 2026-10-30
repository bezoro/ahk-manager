#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$ValidatorModule = (Join-Path $PSScriptRoot 'PortableRelease.psm1')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute portable evidence directory.' }
Import-Module $ValidatorModule -Force
$identity = Read-PortableReleaseManifest -ManifestPath $ManifestPath -ArchivePath $ArchivePath
$archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if (-not $entries.TryAdd($entry.FullName, $entry) -or $entry.FullName -cmatch '(^|/)Data(/|$)' -or $entry.FullName.Contains('\') -or $entry.FullName.StartsWith('/') -or $entry.FullName.Split('/') -contains '..') { throw 'The portable archive contains duplicate, mutable or unsafe entries.' }
    }
    function Read-ZipJson([string]$Name, [long]$Limit) {
        if (-not $entries.ContainsKey($Name) -or $entries[$Name].Length -le 0 -or $entries[$Name].Length -gt $Limit) { throw "Portable metadata is missing or oversized: $Name" }
        $inputStream = $entries[$Name].Open()
        $reader = [IO.StreamReader]::new($inputStream, [Text.UTF8Encoding]::new($false, $true))
        try { return $reader.ReadToEnd() | ConvertFrom-Json -Depth 15 }
        finally { $reader.Dispose(); $inputStream.Dispose() }
    }
    $marker = Read-ZipJson 'portable.json' 4096
    $descriptor = Read-ZipJson 'portable-package.json' 4MB
    if ($marker.schemaVersion -ne 1 -or $marker.applicationId -cne 'Bezoro.AhkManager.Electron' -or $marker.dataDirectory -cne 'Data' -or $descriptor.schemaVersion -ne 1 -or $descriptor.applicationId -cne $marker.applicationId -or $descriptor.updateProtocol -ne 2 -or $descriptor.version -cne $identity.Version -or $descriptor.commit -cne $identity.Commit) { throw 'The packaged portable marker and inventory do not match the release.' }
    if ($descriptor.files.Count -gt 10000 -or $entries.Count -ne $descriptor.files.Count + 2) { throw 'The ZIP entry count differs from its exact inventory.' }
    $expanded = 0L
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $descriptor.files) {
        if (-not $seen.Add($file.path) -or -not $entries.ContainsKey($file.path) -or $entries[$file.path].FullName -cne $file.path -or ($file.path -cne 'Bezoro.AhkManager.exe' -and -not $file.path.StartsWith('App/', [StringComparison]::Ordinal))) { throw 'An inventory path is duplicate, missing or outside the runtime.' }
        $entry = $entries[$file.path]
        if ($entry.Length -ne $file.bytes -or $entry.Length -lt 0 -or $entry.Length -gt 512MB) { throw 'A packaged file exceeds its exact inventory length.' }
        $expanded += $entry.Length
        if ($expanded -gt 2GB) { throw 'The portable runtime exceeds the expanded-size limit.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -cne $file.sha256) { throw 'A packaged file differs from its recorded SHA256.' }
    }
    foreach ($required in @('Bezoro.AhkManager.exe', 'App/Bezoro.AhkManager.exe', 'App/resources/app.asar', 'App/resources/app-update.yml', 'App/backend/AhkManager.Backend.exe', 'App/backend/update/AhkManager.UpdateHost.exe')) { if (-not $seen.Contains($required) -or $entries[$required].Length -le 0) { throw 'A critical portable runtime file is absent or empty.' } }
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $summary = [ordered]@{ passed = $true; version = $identity.Version; commit = $identity.Commit; files = $descriptor.files.Count; entries = $entries.Count; expandedBytes = $expanded; userDataIncluded = $false; archiveSha256 = $identity.Sha256 }
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'portable-package-summary.json'), ($summary | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "Portable archive verified: $($descriptor.files.Count) exact files; user Data excluded."
}
finally { $archive.Dispose() }
