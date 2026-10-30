#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ApplicationRoot,
    [Parameter(Mandatory = $true)][string]$LauncherPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
if (-not (Test-ReleaseVersion $Version) -or -not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use an exact release version and a fresh absolute portable output directory.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$stage = Join-Path $OutputRoot 'staging'
$archivePath = Join-Path $OutputRoot "AHK-Manager-$Version-Portable-x64.zip"
$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Presentation.Electron/scripts/pack-portable.cjs'
& node $scriptPath --source $ApplicationRoot --launcher $LauncherPath --out $stage --version $Version --commit $Commit
if ($LASTEXITCODE -ne 0) { throw 'Portable package staging failed.' }
$zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) { $files.Add([IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')) }
    $files.Sort([StringComparer]::Ordinal)
    foreach ($relative in $files) {
        $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        # Keep archive metadata deterministic; runtime identity comes from hashes.
        $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $source = [IO.File]::OpenRead((Join-Path $stage $relative))
        $destination = $entry.Open()
        try { $source.CopyTo($destination) }
        finally { $destination.Dispose(); $source.Dispose() }
    }
}
finally { $zip.Dispose() }
if ((Get-Item -LiteralPath $archivePath).Length -gt 1GB) { throw 'The portable archive exceeds the 1 GiB update limit.' }
Write-Host "Built $([IO.Path]::GetFileName($archivePath)) without user data."
