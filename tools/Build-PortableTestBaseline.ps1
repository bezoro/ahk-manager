#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$FrontendRoot,
    [Parameter(Mandatory = $true)][string]$CandidateApplicationRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [string]$Version = '1.3.3-test.1.1.1'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Build the portable regression baseline only on a disposable GitHub-hosted Windows runner.' }
foreach ($path in @($FrontendRoot, $CandidateApplicationRoot, $OutputRoot)) { if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Use absolute portable baseline paths.' } }
if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a fresh portable baseline output directory.' }
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
if (-not (Test-ReleaseVersion $Version)) { throw 'The portable baseline version is invalid.' }
$repository = Split-Path -Parent $PSScriptRoot
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$frontend = Join-Path $OutputRoot 'frontend'
Copy-Item -LiteralPath $FrontendRoot -Destination $frontend -Recurse
& (Join-Path $PSScriptRoot 'Set-ReleaseBuildInfo.ps1') -FrontendRoot $frontend -Version $Version -Commit $Commit
$backend = Join-Path $OutputRoot 'backend'
& dotnet publish (Join-Path $repository 'Backend/AhkManager.Backend.csproj') -c Release -r win-x64 --self-contained true --no-restore "-p:Version=$Version" "-p:SourceRevisionId=$Commit" -o $backend
if ($LASTEXITCODE -ne 0) { throw 'Portable baseline backend publish failed.' }
& dotnet publish (Join-Path $repository 'UpdateHost/AhkManager.UpdateHost.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" "-p:SourceRevisionId=$Commit" -o (Join-Path $backend 'update')
if ($LASTEXITCODE -ne 0) { throw 'Portable baseline update host publish failed.' }
$environmentNames = @('AHK_FRONTEND_DIST', 'AHK_BACKEND_DIST', 'AHK_PACKAGE_OUT', 'AHK_RELEASE_VERSION', 'AHK_SOURCE_COMMIT')
$previous = @{}
foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:AHK_FRONTEND_DIST = $frontend
    $env:AHK_BACKEND_DIST = $backend
    $env:AHK_PACKAGE_OUT = Join-Path $OutputRoot 'application'
    $env:AHK_RELEASE_VERSION = $Version
    $env:AHK_SOURCE_COMMIT = $Commit
    Push-Location (Join-Path $repository 'Presentation.Electron')
    try {
        # --dir builds the real signed/fused application, without another NSIS setup.
        & node node_modules/electron-builder/cli.js --config electron-builder.cjs --win --x64 --dir --publish never
        if ($LASTEXITCODE -ne 0) { throw 'Portable baseline application packaging failed.' }
    }
    finally { Pop-Location }
    $application = Join-Path $env:AHK_PACKAGE_OUT 'win-unpacked'
    # electron-builder omits its update policy for a dir-only target. Reuse the
    # exact candidate policy rather than changing publisher requirements in CI.
    $candidatePolicy = Join-Path $CandidateApplicationRoot 'resources/app-update.yml'
    $baselinePolicy = Join-Path $application 'resources/app-update.yml'
    if (Test-Path -LiteralPath $baselinePolicy) {
        if ((Get-FileHash -LiteralPath $candidatePolicy -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $baselinePolicy -Algorithm SHA256).Hash) { throw 'The baseline changed the exact candidate publisher policy.' }
    }
    else { [IO.File]::Copy($candidatePolicy, $baselinePolicy, $false) }
    $launcher = Join-Path $OutputRoot 'launcher'
    & (Join-Path $PSScriptRoot 'Build-PortableLauncher.ps1') -OutputRoot $launcher -Version $Version -IconPath (Join-Path $frontend 'app.ico')
    $packages = Join-Path $OutputRoot 'package'
    & (Join-Path $PSScriptRoot 'Build-PortablePackage.ps1') -ApplicationRoot $application -LauncherPath (Join-Path $launcher 'Bezoro.AhkManager.exe') -OutputRoot $packages -Version $Version -Commit $Commit
    $archive = Join-Path $packages "AHK-Manager-$Version-Portable-x64.zip"
    $manifest = Join-Path $packages 'ahk-manager-portable-release.json'
    & (Join-Path $PSScriptRoot 'New-PortableReleaseManifest.ps1') -ArchivePath $archive -OutputPath $manifest -Version $Version -Commit $Commit -Notes 'Isolated portable update regression baseline. Not a published release.'
    & (Join-Path $PSScriptRoot 'Test-PortablePackage.ps1') -ArchivePath $archive -ManifestPath $manifest -OutputRoot (Join-Path $OutputRoot 'evidence')
}
finally { foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) } }
Write-Host "Built the coherent, test-only portable baseline $Version at $Commit."
