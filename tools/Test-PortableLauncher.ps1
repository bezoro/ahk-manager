#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$LauncherPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not [IO.Path]::IsPathFullyQualified($LauncherPath) -or -not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use Windows, the built launcher, and a fresh absolute test directory.' }
$repository = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repository 'PortableLauncher/Build.psm1') -Force
$root = Join-Path $OutputRoot 'Portable Manager with spaces'
[IO.Directory]::CreateDirectory((Join-Path $root 'App')) | Out-Null
$launcher = Join-Path $root 'Bezoro.AhkManager.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $launcher
[IO.File]::WriteAllText((Join-Path $root 'portable.json'), '{"schemaVersion":1,"applicationId":"Bezoro.AhkManager.Electron","dataDirectory":"Data"}')
[IO.File]::WriteAllText((Join-Path $root 'portable-package.json'), '{}')
$probe = Join-Path $root 'App/Bezoro.AhkManager.exe'
Build-PortableWin32Executable -SourcePath (Join-Path $repository 'PortableLauncher/LauncherProbe.cpp') -OutputPath $probe
$evidence = Join-Path $OutputRoot 'arguments.bin'
$expected = @('--login', '--background', 'two words', '', 'quote"inside', 'trailing\', 'C:\folder with spaces\', 'unicode-ação')
$start = [Diagnostics.ProcessStartInfo]::new($launcher)
$start.UseShellExecute = $false
$start.WorkingDirectory = $env:TEMP
$start.ArgumentList.Add($evidence)
foreach ($argument in $expected) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
try { if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'The GUI launcher did not dispatch normally.' } }
finally { $process.Dispose() }
$deadline = [DateTime]::UtcNow.AddSeconds(10)
$bytes = $null
do {
    try { if (Test-Path -LiteralPath $evidence) { $bytes = [IO.File]::ReadAllBytes($evidence) } }
    catch [IO.IOException] { }
    if ($null -eq $bytes) { Start-Sleep -Milliseconds 50 }
} while ($null -eq $bytes -and [DateTime]::UtcNow -lt $deadline)
if ($null -eq $bytes) { throw 'The native GUI child did not produce complete evidence.' }
$fields = [Text.Encoding]::Unicode.GetString($bytes).Split([char]0)
if ($fields.Count -ne $expected.Count + 4 -or $fields[0] -cne 'no-console' -or $fields[1] -cne $root -or $fields[2] -cne $probe) { throw 'The portable launcher changed the working directory, target identity, or console policy.' }
for ($index = 0; $index -lt $expected.Count; $index++) { if ($fields[$index + 3] -cne $expected[$index]) { throw "Portable argument $index changed during forwarding." } }
$summary = [ordered]@{ passed = $true; gui = $true; consoleCreated = $false; targetVerified = $true; workingDirectoryVerified = $true; arguments = $expected.Count; launcherExitCode = 0 }
[IO.File]::WriteAllText((Join-Path $OutputRoot 'launcher-summary.json'), ($summary | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Portable GUI launcher passed: exact target, root working directory, no console, and $($expected.Count) arguments."
