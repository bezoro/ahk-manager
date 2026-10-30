#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [ValidateLength(0, 4000)][string]$Notes = '',
    [DateTimeOffset]$PublishedAt = [DateTimeOffset]::UtcNow
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
if (-not (Test-ReleaseVersion $Version) -or $Notes.Contains([char]0)) { throw 'Invalid portable release identity.' }
if (-not [IO.Path]::IsPathFullyQualified($ArchivePath) -or -not [IO.Path]::IsPathFullyQualified($OutputPath) -or (Test-Path -LiteralPath $OutputPath)) { throw 'Use absolute archive paths and a fresh manifest output path.' }
$archive = Get-Item -LiteralPath $ArchivePath
if ($archive.PSIsContainer -or $archive.Name -cne "AHK-Manager-$Version-Portable-x64.zip" -or $archive.Length -le 0 -or $archive.Length -gt 1GB) { throw 'Invalid portable archive name or size.' }
$manifest = [ordered]@{
    schemaVersion = 1
    updateProtocol = 2
    version = $Version
    commit = $Commit.ToLowerInvariant()
    publishedAt = $PublishedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    notes = $Notes
    archive = [ordered]@{
        name = $archive.Name
        sha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        sha512 = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA512).Hash))
        bytes = $archive.Length
    }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[IO.File]::WriteAllText($OutputPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host "Prepared portable release metadata for $Version at commit $($Commit.Substring(0, 12))."
