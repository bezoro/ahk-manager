#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [Alias('RequirePortable')][switch]$Portable,
    [switch]$RequirePortableSignature,
    [string]$PortableArchivePath,
    [string]$PortableManifestPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-GitTags.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1')
if (-not (Test-ReleaseVersion $Version)) { throw 'Invalid release version.' }
$portableFiles = @()
if (-not [string]::IsNullOrWhiteSpace($PortableArchivePath) -or -not [string]::IsNullOrWhiteSpace($PortableManifestPath)) {
    if ([string]::IsNullOrWhiteSpace($PortableArchivePath) -or [string]::IsNullOrWhiteSpace($PortableManifestPath)) { throw 'Portable publication status needs both local archive and manifest paths.' }
    $null = Read-PortableReleaseManifest -ManifestPath $PortableManifestPath -ArchivePath $PortableArchivePath -ExpectedVersion $Version -ExpectedCommit $Commit.ToLowerInvariant()
    $portableFiles = @(Get-Item -LiteralPath $PortableArchivePath; Get-Item -LiteralPath $PortableManifestPath)
    $Portable = $true
}
$RequirePortableSignature = $RequirePortableSignature -or ($Portable -and -not [string]::IsNullOrWhiteSpace($env:AHK_PUBLISHER_NAME))
if ($RequirePortableSignature -and -not $Portable) { throw 'A portable signature status check requires its portable release family.' }
if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { throw 'The release status token is missing.' }
$headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'Bezoro-AhkManager-Release' }
$tag = 'v' + $Version
$invoke = Get-Command Invoke-RestMethod -ErrorAction Stop
$request = {
    param($Method, $Uri, $Body)
    if ($Method -ine 'Get') { throw 'Release status queries may only read GitHub data.' }
    & $invoke -Method Get -Uri $Uri -Headers $headers
}.GetNewClosure()
$tagCommit = Get-ReleaseGitTagCommit -Repository $Repository -TagName $tag -Request $request
if ($null -ne $tagCommit -and $tagCommit -cne $Commit.ToLowerInvariant()) { throw 'The existing Git tag belongs to a different commit.' }
try {
    $release = & $request 'Get' "https://api.github.com/repos/$Repository/releases/tags/$tag" $null
}
catch {
    $response = $_.Exception.PSObject.Properties['Response']
    if ($null -ne $response -and $null -ne $response.Value -and [int]$response.Value.StatusCode -eq 404) { return 'Missing' }
    throw
}
if ($null -eq $release -or $release.tag_name -cne $tag -or $release.target_commitish -cne $Commit.ToLowerInvariant() -or $release.prerelease -isnot [bool] -or $release.prerelease -ne ((Get-ReleaseVersionChannel $Version) -ceq 'test')) { throw 'The existing release belongs to a different commit, tag or channel.' }
if ($release.draft) { return 'Draft' }
if ($null -eq $tagCommit) { throw 'The published release is missing its Git tag.' }
$required = @("AHK-Manager-$Version-Setup-x64.exe", 'ahk-manager-release.json')
if ($Portable) { $required += @("AHK-Manager-$Version-Portable-x64.zip", 'ahk-manager-portable-release.json') }
if ($RequirePortableSignature) { $required += 'ahk-manager-portable-release.json.p7s' }
foreach ($name in $required) {
    if (@($release.assets | Where-Object { $_.name -ceq $name -and $_.state -ceq 'uploaded' }).Count -ne 1) { throw 'The immutable published release is incomplete.' }
}
foreach ($file in $portableFiles) {
    $assets = @($release.assets | Where-Object { $_.name -ceq $file.Name -and $_.state -ceq 'uploaded' })
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($assets.Count -ne 1 -or $assets[0].size -ne $file.Length -or $null -eq $assets[0].PSObject.Properties['digest'] -or $assets[0].digest -cne $digest) { throw 'The immutable published portable assets differ from the approved build.' }
}
if ((Get-ReleaseGitTagCommit -Repository $Repository -TagName $tag -Request $request) -cne $Commit.ToLowerInvariant()) { throw 'The Git tag changed during release status verification.' }
return 'Published'
