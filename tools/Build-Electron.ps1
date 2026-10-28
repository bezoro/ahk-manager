param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$NpmPath = '',
    [string]$PackagesPath = (Join-Path $env:LOCALAPPDATA 'Bezoro/AhkManagerBuildPackages'),
    [string]$ReleaseVersion = '',
    [string]$SourceCommit = ''
)
$ErrorActionPreference = 'Stop'
if (-not $NpmPath) { $NpmPath = (Get-Command npm.cmd -ErrorAction Stop).Source }
$repoRoot = Split-Path -Parent $PSScriptRoot
$stageRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $stageRoot) { throw 'Use a fresh output directory.' }
New-Item -ItemType Directory -Path $stageRoot | Out-Null
$frontend = Join-Path $repoRoot 'Presentation.Electron'
if (-not $ReleaseVersion) { $ReleaseVersion = (Get-Content -LiteralPath (Join-Path $frontend 'package.json') -Raw | ConvertFrom-Json).version }
if (-not $SourceCommit) {
    $SourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Source commit is unavailable.' }
}
$previousReleaseVersion = $env:AHK_RELEASE_VERSION
$previousSourceCommit = $env:AHK_SOURCE_COMMIT
$env:AHK_RELEASE_VERSION = $ReleaseVersion
$env:AHK_SOURCE_COMMIT = $SourceCommit.ToLowerInvariant()
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
    & (Join-Path $PSScriptRoot 'Set-ReleaseBuildInfo.ps1') -FrontendRoot (Join-Path $stageRoot 'frontend') -Version $ReleaseVersion -Commit $SourceCommit
    & dotnet.exe restore (Join-Path $repoRoot 'Backend/AhkManager.Backend.csproj') --locked-mode --packages $PackagesPath --configfile (Join-Path $repoRoot 'NuGet.Config') -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "Backend restore failed: $LASTEXITCODE" }
    & dotnet.exe publish (Join-Path $repoRoot 'Backend/AhkManager.Backend.csproj') -c Release -r win-x64 --self-contained true --no-restore "-p:Version=$ReleaseVersion" "-p:SourceRevisionId=$SourceCommit" -o (Join-Path $stageRoot 'backend') --nologo
    if ($LASTEXITCODE -ne 0) { throw "Backend publish failed: $LASTEXITCODE" }
    & dotnet.exe restore (Join-Path $repoRoot 'UpdateHost/AhkManager.UpdateHost.csproj') --locked-mode --packages $PackagesPath --configfile (Join-Path $repoRoot 'NuGet.Config') -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "Update host restore failed: $LASTEXITCODE" }
    & dotnet.exe publish (Join-Path $repoRoot 'UpdateHost/AhkManager.UpdateHost.csproj') -c Release -r win-x64 --self-contained true --no-restore '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:Version=$ReleaseVersion" "-p:SourceRevisionId=$SourceCommit" -o (Join-Path $stageRoot 'backend/update') --nologo
    if ($LASTEXITCODE -ne 0) { throw "Update host publish failed: $LASTEXITCODE" }
    & (Join-Path $stageRoot 'backend/update/AhkManager.UpdateHost.exe') --self-test --root (Join-Path $stageRoot 'update-host-tests')
    if ($LASTEXITCODE -ne 0) { throw "Update host checks failed: $LASTEXITCODE" }
} finally {
    Pop-Location
    $env:AHK_RELEASE_VERSION = $previousReleaseVersion
    $env:AHK_SOURCE_COMMIT = $previousSourceCommit
}
