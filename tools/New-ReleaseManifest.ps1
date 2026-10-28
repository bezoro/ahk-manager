#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [ValidateLength(0, 4000)][string]$Notes = '',
    [DateTimeOffset]$PublishedAt = [DateTimeOffset]::UtcNow
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Limits.psm1') -Force
if ($Notes.Contains([char]0)) { throw 'Release notes cannot contain NUL.' }
if (-not [IO.Path]::IsPathFullyQualified($InstallerPath) -or -not [IO.Path]::IsPathFullyQualified($OutputPath)) { throw 'Release paths must be absolute.' }
if (Test-Path -LiteralPath $OutputPath) { throw 'Release manifest must be written to a fresh path.' }
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.PSIsContainer -or $installer.Name -cne "AHK-Manager-$Version-Setup-x64.exe" -or -not (Test-ReleaseInstallerLength $installer.Length)) { throw 'Invalid release installer name or size; installers must be at most 1 GiB.' }
$sha512 = Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA512
$sha512Bytes = [Convert]::FromHexString($sha512.Hash)
$manifest = [ordered]@{
    schemaVersion = 1
    updateProtocol = 1
    version = $Version
    commit = $Commit.ToLowerInvariant()
    publishedAt = $PublishedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    notes = $Notes
    installer = [ordered]@{
        name = $installer.Name
        sha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        sha512 = [Convert]::ToBase64String($sha512Bytes)
        bytes = $installer.Length
    }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[IO.File]::WriteAllText($OutputPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host "Prepared immutable release manifest for $Version at commit $($Commit.Substring(0, 12))."
