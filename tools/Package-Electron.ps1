param(
    [Parameter(Mandatory = $true)][string]$BuildRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$NpmPath = '',
    [switch]$DirectoryOnly
)
$ErrorActionPreference = 'Stop'
if (-not $NpmPath) { $NpmPath = (Get-Command npm.cmd -ErrorAction Stop).Source }
$repoRoot = Split-Path -Parent $PSScriptRoot
$frontend = [IO.Path]::GetFullPath((Join-Path $BuildRoot 'frontend'))
$backend = [IO.Path]::GetFullPath((Join-Path $BuildRoot 'backend'))
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh package output directory.' }
$info = Get-Content -LiteralPath (Join-Path $frontend 'build-info.json') -Raw | ConvertFrom-Json
if (-not $info.version -or -not $info.commit -or $info.updateProtocol -ne 1) { throw 'Rebuild the bundle with its release version, source commit and update protocol.' }
$previous = @{}
foreach ($name in @('AHK_FRONTEND_DIST', 'AHK_BACKEND_DIST', 'AHK_PACKAGE_OUT', 'AHK_RELEASE_VERSION', 'AHK_SOURCE_COMMIT')) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location -LiteralPath (Join-Path $repoRoot 'Presentation.Electron')
try {
    $env:AHK_FRONTEND_DIST = $frontend
    $env:AHK_BACKEND_DIST = $backend
    $env:AHK_PACKAGE_OUT = $output
    $env:AHK_RELEASE_VERSION = $info.version
    $env:AHK_SOURCE_COMMIT = $info.commit
    if ($DirectoryOnly) { & $NpmPath run package -- --dir --publish never } else { & $NpmPath run package -- --publish never }
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed: $LASTEXITCODE" }
} finally {
    Pop-Location
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
