param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $outputRoot) { throw 'Destination must be a new directory.' }
if ($outputRoot.StartsWith((Join-Path $repoRoot 'publish'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Legacy publish is protected.' }
Push-Location -LiteralPath $repoRoot
try {
    & dotnet publish 'Presentation.WinUI/Bezoro.AhkManager.WinUI.csproj' -c Release '-p:Platform=x64' --no-build --no-restore -o $outputRoot --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'distribution/README.pt-BR.md') -Destination (Join-Path $outputRoot 'LEIA-ME.md')
    $files = Get-ChildItem -LiteralPath $outputRoot -File -Recurse | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($outputRoot, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [ordered]@{ format = 1; runtime = 'win-x64'; files = @($files) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot 'payload-manifest.json') -Encoding utf8
} finally { Pop-Location }
