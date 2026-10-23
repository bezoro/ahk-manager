param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$NpmPath = '',
    [string]$PackagesPath = (Join-Path $env:LOCALAPPDATA 'Bezoro/AhkManagerBuildPackages')
)
$ErrorActionPreference = 'Stop'
if (-not $NpmPath) { $NpmPath = (Get-Command npm.cmd -ErrorAction Stop).Source }
$repoRoot = Split-Path -Parent $PSScriptRoot
$stageRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $stageRoot) { throw 'Use a fresh output directory.' }
New-Item -ItemType Directory -Path $stageRoot | Out-Null
$frontend = Join-Path $repoRoot 'Presentation.Electron'
Push-Location -LiteralPath $frontend
try {
    & $NpmPath ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed: $LASTEXITCODE" }
    & node.exe 'node_modules/electron/install.js'
    if ($LASTEXITCODE -ne 0) { throw "Electron runtime download failed: $LASTEXITCODE" }
    & $NpmPath run typecheck
    if ($LASTEXITCODE -ne 0) { throw "Typecheck failed: $LASTEXITCODE" }
    & $NpmPath run test -- --out (Join-Path $stageRoot 'tests')
    if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed: $LASTEXITCODE" }
    & $NpmPath run build -- --out (Join-Path $stageRoot 'frontend')
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed: $LASTEXITCODE" }
    & dotnet.exe restore (Join-Path $repoRoot 'Backend/AhkManager.Backend.csproj') --locked-mode --packages $PackagesPath --configfile (Join-Path $repoRoot 'NuGet.Config') -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "Backend restore failed: $LASTEXITCODE" }
    & dotnet.exe publish (Join-Path $repoRoot 'Backend/AhkManager.Backend.csproj') -c Release -r win-x64 --self-contained true --no-restore -o (Join-Path $stageRoot 'backend') --nologo
    if ($LASTEXITCODE -ne 0) { throw "Backend publish failed: $LASTEXITCODE" }
} finally { Pop-Location }
