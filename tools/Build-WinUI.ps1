param([string]$PackagesPath = (Join-Path $env:LOCALAPPDATA 'Bezoro/AhkManagerBuildPackages'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repoRoot
try {
    & dotnet restore 'Tests.WinUI/Tests.WinUI.csproj' --packages $PackagesPath --locked-mode --configfile 'NuGet.Config' '-p:Platform=x64'
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $LASTEXITCODE" }
    & dotnet build 'Tests.WinUI/Tests.WinUI.csproj' -c Release '-p:Platform=x64' --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
} finally { Pop-Location }
