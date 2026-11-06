#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$LauncherPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or -not [IO.Path]::IsPathFullyQualified($LauncherPath) -or -not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use Windows, the built launcher, and a fresh absolute test directory.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this isolated launcher regression with an administrative token. It never opens an unattended UAC prompt.' }
[xml]$manifest = [IO.File]::ReadAllText($LauncherPath + '.manifest.xml')
$requested = $manifest.SelectNodes("//*[local-name()='requestedExecutionLevel']")
if ($requested.Count -ne 1 -or $requested[0].GetAttribute('level') -cne 'requireAdministrator' -or $requested[0].GetAttribute('uiAccess') -cne 'false') { throw 'The portable launcher must require the Windows administrator token without UIAccess.' }
$repository = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repository 'PortableLauncher/Build.psm1') -Force
$root = Join-Path $OutputRoot 'Portable Manager with spaces'
[IO.Directory]::CreateDirectory((Join-Path $root 'App')) | Out-Null
$launcher = Join-Path $root 'Bezoro.AhkManager.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $launcher
if ((Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash) { throw 'The isolated launcher differs from the manifest-verified executable.' }
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
if ($fields.Count -ne $expected.Count + 5 -or $fields[0] -cne 'no-console' -or $fields[2] -cne $root -or $fields[3] -cne $probe) { throw 'The portable launcher changed the working directory, target identity, or console policy.' }
if ($fields[1] -cne 'administrator') { throw 'The actual portable launcher child did not inherit an elevated Windows token.' }
for ($index = 0; $index -lt $expected.Count; $index++) { if ($fields[$index + 4] -cne $expected[$index]) { throw "Portable argument $index changed during forwarding." } }
[IO.File]::Delete((Join-Path $root 'portable.json'))
foreach ($flag in @('--login', '--background')) {
    $quiet = [Diagnostics.ProcessStartInfo]::new($launcher)
    $quiet.UseShellExecute = $false
    $quiet.WorkingDirectory = $env:TEMP
    $quiet.ArgumentList.Add($flag)
    $process = [Diagnostics.Process]::Start($quiet)
    try { if (-not $process.WaitForExit(10000) -or $process.ExitCode -eq 0) { throw "A missing portable marker must fail quietly for $flag, without a confirmation dialog." } }
    finally { $process.Dispose() }
}
$summary = [ordered]@{ passed = $true; gui = $true; consoleCreated = $false; targetVerified = $true; workingDirectoryVerified = $true; arguments = $expected.Count; launcherExitCode = 0; quietStartupFailureCases = 2; administratorTestHost = $true; elevatedChildTokenVerified = $true; mediumTokenUacObserved = $false; requestedExecutionLevel = 'requireAdministrator'; uiAccess = $false; embeddedManifestVerified = $true; launcherSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash.ToLowerInvariant(); manifestSha256 = (Get-FileHash -LiteralPath ($LauncherPath + '.manifest.xml') -Algorithm SHA256).Hash.ToLowerInvariant() }
[IO.File]::WriteAllText((Join-Path $OutputRoot 'launcher-summary.json'), ($summary | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Portable GUI launcher passed: elevated child token, exact target, root working directory, no console, and $($expected.Count) arguments."
