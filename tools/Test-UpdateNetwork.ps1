#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$HarnessRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Run the Electron network harness on Windows.' }
foreach ($path in @($HarnessRoot, $OutputRoot)) {
    if (-not [IO.Path]::IsPathFullyQualified($path) -or (Test-Path -LiteralPath $path)) { throw 'Use fresh absolute harness and output directories.' }
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path $repoRoot 'Presentation.Electron'
$node = (Get-Command node.exe -ErrorAction Stop).Source
$electron = Join-Path $frontend 'node_modules/electron/dist/electron.exe'
& $node (Join-Path $frontend 'scripts/build-update-network-harness.cjs') --out $HarnessRoot
if ($LASTEXITCODE -ne 0) { throw 'The network harness build failed.' }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $electron
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add((Join-Path $HarnessRoot 'update-network-harness.cjs'))
$start.ArgumentList.Add('--output')
$start.ArgumentList.Add($OutputRoot)
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(120000)) { throw 'The isolated network harness did not exit within its deadline.' }
[IO.File]::WriteAllText(($OutputRoot + '.stdout.log'), $stdout.GetAwaiter().GetResult())
[IO.File]::WriteAllText(($OutputRoot + '.stderr.log'), $stderr.GetAwaiter().GetResult())
if ($process.ExitCode -ne 0) { throw 'The Electron network regression checks failed. See update-network/summary.json.' }
$summary = Get-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Raw | ConvertFrom-Json
if ($summary.failures.Count -ne 0 -or @($summary.checks | Where-Object { -not $_.passed }).Count -ne 0 -or $summary.checks.Count -lt 10) { throw 'The network harness did not confirm all checks.' }
Write-Host "Passed $($summary.checks.Count) Electron network checks, including the shipped redirect failure and the repaired transport."
