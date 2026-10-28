#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$BaselineInstaller,
    [Parameter(Mandatory = $true)][string]$TargetInstaller,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateRange(30, 300)][int]$InstallerTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This installation smoke test requires a disposable GitHub-hosted Windows runner.'
}

$productGuid = '62d1c352-a9c4-556f-981c-2237ce4d0b68'
$installKey = 'Software\' + $productGuid
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $productGuid
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'Bezoro.AhkManager.WinUI'
$profileRoot = Join-Path $env:LOCALAPPDATA 'Bezoro/AhkManagerElectron'
$utf8 = [Text.UTF8Encoding]::new($false)
$script:checks = [Collections.Generic.List[object]]::new()
$script:trace = [Collections.Generic.List[string]]::new()
$script:unknownInstaller = $false
$script:installationObserved = $false
$script:startupWritten = $false
$script:cleanupConfirmed = $false
$script:outputCreated = $false
$script:failure = $null
$registry = $null
$installRoot = $null
$startupCommand = $null
$lastUninstaller = $null

function Assert-Check([bool]$Condition, [string]$Name) {
    $script:checks.Add([pscustomobject]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw $Name }
    Write-Host "PASS $Name"
}

function Trace([string]$Message) {
    $line = [DateTimeOffset]::UtcNow.ToString('O') + ' ' + $Message
    $script:trace.Add($line)
    Write-Host $line
}

function Local-Path([string]$Path, [switch]$File) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '["\x00\r\n]') {
        throw 'Only absolute local paths without command-line delimiters are accepted.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Redirected path refused: $current" }
        }
    }
    if ($File -and -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "File missing: $full" }
    return $full
}

function Installer-Version([string]$Installer) {
    $raw = [Diagnostics.FileVersionInfo]::GetVersionInfo($Installer).ProductVersion
    if ($raw -notmatch '^(\d+\.\d+\.\d+)(?:\.0)?$') { throw "Unexpected installer version: $raw" }
    return $Matches[1]
}

function Hash([string]$Path) { return (Get-FileHash -LiteralPath (Local-Path $Path -File) -Algorithm SHA256).Hash.ToLowerInvariant() }

function Invoke-Installer([string]$Path, [bool]$Updated) {
    $Path = Local-Path $Path -File
    $expectedHash = Hash $Path
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $OutputRoot
    # NSIS consumes the unquoted remainder after /D=. Keep it last. FileName is
    # separate and no shell interprets the validated path or these fixed switches.
    $start.Arguments = '/S' + $(if ($Updated) { ' --updated' } else { '' }) + ' /D=' + $installRoot
    if ((Hash $Path) -ne $expectedHash) { throw 'Installer changed before dispatch.' }
    Trace "Dispatch installer $([IO.Path]::GetFileName($Path)); updated=$Updated; sha256=$expectedHash"
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($InstallerTimeoutSeconds * 1000)) {
            $script:unknownInstaller = $true
            throw 'Installer exit was not observed. The test will not repeat or kill it.'
        }
        Assert-Check ($process.ExitCode -eq 0) "installer exits successfully; updated=$Updated"
    }
    finally { $process.Dispose() }
}

function Read-Uninstaller {
    $key = $registry.OpenSubKey($uninstallKey)
    try { $command = if ($null -ne $key) { $key.GetValue('UninstallString') } else { $null } }
    finally { if ($null -ne $key) { $key.Dispose() } }
    if ($command -isnot [string] -or $command -notmatch '^"([^"]+)"(?: /currentuser)?$') { throw 'Unexpected uninstaller registration.' }
    $path = Local-Path $Matches[1] -File
    Assert-Check ([IO.Path]::GetDirectoryName($path) -eq $installRoot) 'registered uninstaller stays inside the exact test installation'
    return [pscustomobject]@{ path = $path; hash = Hash $path }
}

function Check-Installation([string]$ExpectedVersion, [string]$ExpectedInstaller) {
    $key = $registry.OpenSubKey($installKey)
    try {
        Assert-Check ($null -ne $key) 'installation registry exists'
        Assert-Check ($key.GetValue('InstallLocation') -eq $installRoot) 'installation registry retains the exact destination with spaces'
        Assert-Check ($key.GetValue('UpdateProtocol') -eq 1) 'installation advertises update protocol 1'
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
    $view32 = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
    $sharedKey = $null
    try {
        $sharedKey = $view32.OpenSubKey($installKey)
        Assert-Check ($null -ne $sharedKey -and $sharedKey.GetValue('InstallLocation') -eq $installRoot) 'the shared HKCU 32-bit view identifies the same owned installation'
    }
    finally { if ($null -ne $sharedKey) { $sharedKey.Dispose() }; $view32.Dispose() }
    $key = $registry.OpenSubKey($uninstallKey)
    try { Assert-Check ($null -ne $key -and $key.GetValue('DisplayVersion') -eq $ExpectedVersion) "installed version is $ExpectedVersion" }
    finally { if ($null -ne $key) { $key.Dispose() } }
    Assert-Check (Test-Path -LiteralPath (Join-Path $installRoot 'Bezoro.AhkManager.exe') -PathType Leaf) 'manager executable exists without being launched'
    Assert-Check (@(Get-Process -Name 'Bezoro.AhkManager' -ErrorAction SilentlyContinue).Count -eq 0) 'silent installation does not launch the Manager'
    $cached = Join-Path $profileRoot "Updates/installers/$ExpectedVersion/installer.exe"
    Assert-Check ((Hash $cached) -eq (Hash $ExpectedInstaller)) "cached installer for $ExpectedVersion preserves the exact downloaded bytes"
    $receipt = [IO.File]::ReadAllText((Join-Path ([IO.Path]::GetDirectoryName($cached)) 'receipt.json')) | ConvertFrom-Json
    Assert-Check ($receipt.schemaVersion -eq 1 -and $receipt.updateProtocol -eq 1 -and $receipt.version -eq $ExpectedVersion -and $receipt.sha256 -eq (Hash $cached)) "cached installer receipt for $ExpectedVersion matches its bytes and protocol"
    $script:lastUninstaller = Read-Uninstaller
    $script:installationObserved = $true
}

function Check-Preserved([hashtable]$ExpectedFiles) {
    foreach ($path in $ExpectedFiles.Keys) { Assert-Check ((Hash $path) -eq $ExpectedFiles[$path]) "preserved file: $([IO.Path]::GetFileName($path))" }
    $key = $registry.OpenSubKey($runKey)
    try { Assert-Check ($null -ne $key -and $key.GetValue($runValue) -eq $startupCommand) 'update preserves the exact pre-existing startup entry' }
    finally { if ($null -ne $key) { $key.Dispose() } }
}

try {
    $BaselineInstaller = Local-Path $BaselineInstaller -File
    $TargetInstaller = Local-Path $TargetInstaller -File
    $OutputRoot = Local-Path $OutputRoot
    if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a fresh isolated output directory.' }
    if (Test-Path -LiteralPath $profileRoot) { throw 'A real operational profile already exists; no installation test is allowed.' }
    if (@(Get-Process -Name 'Bezoro.AhkManager' -ErrorAction SilentlyContinue).Count) { throw 'A Manager is already running; no installation test is allowed.' }
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                foreach ($path in @($installKey, $uninstallKey)) {
                    $key = $base.OpenSubKey($path)
                    try { if ($null -ne $key) { throw 'An installation registration already exists; no test may alter it.' } }
                    finally { if ($null -ne $key) { $key.Dispose() } }
                }
            }
            finally { $base.Dispose() }
        }
    }
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $registry.OpenSubKey($runKey)
    try { if ($null -ne $key -and $null -ne $key.GetValue($runValue)) { throw 'A startup registration already exists; the test will not replace it.' } }
    finally { if ($null -ne $key) { $key.Dispose() } }
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $script:outputCreated = $true
    $installRoot = Join-Path $OutputRoot 'installation with spaces'
    $baselineVersion = Installer-Version $BaselineInstaller
    $targetVersion = Installer-Version $TargetInstaller
    Assert-Check ($baselineVersion -ne $targetVersion) 'baseline and target fixtures have distinct versions'
    Assert-Check ([version]$baselineVersion -ge [version]'1.2.0' -and [version]$targetVersion -ge [version]'1.2.0') 'both fixtures support the update protocol compatibility floor'
    $startupCommand = '"' + (Join-Path $installRoot 'Bezoro.AhkManager.exe') + '" --login --background'

    Invoke-Installer $BaselineInstaller $false
    $script:lastUninstaller = Read-Uninstaller
    $script:installationObserved = $true
    Check-Installation $baselineVersion $BaselineInstaller
    [IO.Directory]::CreateDirectory($profileRoot) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $OutputRoot 'scripts')) | Out-Null
    $foreign = Join-Path $installRoot 'foreign-user-file.txt'
    $profile = Join-Path $profileRoot 'ci-profile-preservation.txt'
    $scriptFile = Join-Path $OutputRoot 'scripts/inert-preserved.ahk'
    [IO.File]::WriteAllText($foreign, 'Owned CI sentinel outside the package allowlist.', $utf8)
    [IO.File]::WriteAllText($profile, 'Owned CI profile sentinel. No operational profile was opened.', $utf8)
    [IO.File]::WriteAllText($scriptFile, '; Inert CI preservation fixture. Never executed.', $utf8)
    $preserved = @{}
    foreach ($path in @($foreign, $profile, $scriptFile, (Join-Path $profileRoot "Updates/installers/$baselineVersion/installer.exe"))) { $preserved[$path] = Hash $path }
    $key = $registry.CreateSubKey($runKey, $true)
    try { $key.SetValue($runValue, $startupCommand, [Microsoft.Win32.RegistryValueKind]::String); $script:startupWritten = $true }
    finally { $key.Dispose() }

    Invoke-Installer $TargetInstaller $true
    Check-Installation $targetVersion $TargetInstaller
    Check-Preserved $preserved
    $preserved[(Join-Path $profileRoot "Updates/installers/$targetVersion/installer.exe")] = Hash $TargetInstaller

    # Exercise explicit rollback through the same verified protocol. These two
    # packages are CI fixtures from the same source, not a legacy migration test.
    Invoke-Installer $BaselineInstaller $true
    Check-Installation $baselineVersion $BaselineInstaller
    Check-Preserved $preserved
    Trace 'Installation, update, explicit rollback and preservation checks passed.'
}
catch { $script:failure = $_.Exception.Message; Write-Error -ErrorAction Continue $script:failure }
finally {
    if ($null -ne $registry) {
        if ($script:startupWritten) {
            $key = $registry.OpenSubKey($runKey, $true)
            try {
                if ($null -ne $key -and $key.GetValue($runValue) -eq $startupCommand) { $key.DeleteValue($runValue, $false) }
                elseif ($null -ne $key -and $null -ne $key.GetValue($runValue)) { $script:failure = 'The startup entry changed during testing and was preserved.' }
            }
            finally { if ($null -ne $key) { $key.Dispose() } }
        }
        if ($script:installationObserved -and -not $script:unknownInstaller) {
            try {
                $current = Read-Uninstaller
                if ($current.path -ne $lastUninstaller.path -or $current.hash -ne $lastUninstaller.hash) { throw 'Uninstaller identity changed before cleanup.' }
                $start = [Diagnostics.ProcessStartInfo]::new($current.path)
                $start.UseShellExecute = $false
                $start.WorkingDirectory = $OutputRoot
                $start.Arguments = '/S /currentuser _?=' + $installRoot
                $process = [Diagnostics.Process]::Start($start)
                try {
                    if (-not $process.WaitForExit($InstallerTimeoutSeconds * 1000)) { throw 'Cleanup exit was not observed. The uninstaller was not killed.' }
                    if ($process.ExitCode -ne 0) { throw "Cleanup failed: $($process.ExitCode)." }
                }
                finally { $process.Dispose() }
                $key = $registry.OpenSubKey($installKey)
                try { $script:cleanupConfirmed = $null -eq $key }
                finally { if ($null -ne $key) { $key.Dispose() } }
                if (-not $script:cleanupConfirmed) { throw 'Cleanup did not remove its own installation registration.' }
                Trace 'Owned test installation was uninstalled; preserved files and caches remain in the disposable runner.'
            }
            catch {
                $cleanupFailure = "Cleanup: $($_.Exception.Message)"
                $script:failure = if ($null -eq $script:failure) { $cleanupFailure } else { $script:failure + " | " + $cleanupFailure }
            }
        }
        $registry.Dispose()
    }
    if ($script:outputCreated -and (Test-Path -LiteralPath $OutputRoot -PathType Container)) {
        [IO.File]::WriteAllLines((Join-Path $OutputRoot 'installation.log'), $script:trace.ToArray(), $utf8)
        $summary = [ordered]@{ fixture = $true; checks = $script:checks.ToArray(); failure = $script:failure; unknownInstaller = $script:unknownInstaller; cleanupConfirmed = $script:cleanupConfirmed; installation = $installRoot }
        [IO.File]::WriteAllText((Join-Path $OutputRoot 'installation-summary.json'), ($summary | ConvertTo-Json -Depth 15), $utf8)
    }
}
if ($null -ne $script:failure) { throw $script:failure }
if (-not $script:cleanupConfirmed) { throw 'Installation test cleanup was not confirmed.' }
Write-Host "Installation smoke passed: $($script:checks.Count) checks; no Manager or AutoHotkey script was launched."
