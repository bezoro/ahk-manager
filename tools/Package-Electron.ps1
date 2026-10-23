param(
    [Parameter(Mandatory = $true)][string]$BuildRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$NpmPath = '',
    [switch]$DirectoryOnly
)
$ErrorActionPreference = 'Stop'
if (-not $NpmPath) { $NpmPath = (Get-Command npm.cmd -ErrorAction Stop).Source }
$repoRoot = Split-Path -Parent $PSScriptRoot
$env:AHK_FRONTEND_DIST = [IO.Path]::GetFullPath((Join-Path $BuildRoot 'frontend'))
$env:AHK_BACKEND_DIST = [IO.Path]::GetFullPath((Join-Path $BuildRoot 'backend'))
$env:AHK_PACKAGE_OUT = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $env:AHK_PACKAGE_OUT) { throw 'Use a fresh package output directory.' }
Push-Location -LiteralPath (Join-Path $repoRoot 'Presentation.Electron')
try {
    if ($DirectoryOnly) { & $NpmPath run package -- --dir } else { & $NpmPath run package }
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed: $LASTEXITCODE" }
} finally {
    Pop-Location
    Remove-Item Env:AHK_FRONTEND_DIST,Env:AHK_BACKEND_DIST,Env:AHK_PACKAGE_OUT -ErrorAction SilentlyContinue
}
