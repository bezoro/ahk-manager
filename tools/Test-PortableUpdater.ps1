#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$BaselineArchive,
    [Parameter(Mandatory = $true)][string]$BaselineManifest,
    [Parameter(Mandatory = $true)][string]$CandidateArchive,
    [Parameter(Mandatory = $true)][string]$CandidateManifest,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateSet('default', 'custom-data', 'foreign-root')][string]$Scenario = 'default',
    [ValidateRange(60, 900)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Portable updater regression requires a disposable GitHub-hosted Windows runner.' }
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute portable evidence directory.' }
Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'test-support/PortableUpdaterSession.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'test-support/PortableSigningFixture.psm1') -Force
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/InstalledUpdaterUi.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableStartupUi.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableDataPickerUi.cs')
$readStartupDialog = [Delegate]::CreateDelegate([Func[IntPtr, int, string, DateTime, string]], [AhkManager.TestSupport.InstalledUpdaterUi], 'AccessibleDialogText')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$sid = $identity.User.Value
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'This isolated regression uses an administrative token to match the affected application.' }
$utf8 = [Text.UTF8Encoding]::new($false)
$legacyRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Bezoro/AhkManagerElectron'
$legacyChromium = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Bezoro/AhkManagerElectron/Chromium'
$guid = '62d1c352-a9c4-556f-981c-2237ce4d0b68'
$registryPaths = @("Software\$guid", "Software\Microsoft\Windows\CurrentVersion\Uninstall\$guid")
$runPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'AhkManager.CI.PortablePreservation.' + [Guid]::NewGuid().ToString('N')
$checks = [Collections.Generic.List[object]]::new()
$trace = [Collections.Generic.List[string]]::new()
$session = $null
$inert = $null
$registry = $null
$failure = $null
$helperInFlight = $false
$complete = $false
$outputCreated = $false
$startupWritten = $false
$updatesRoot = $null
$searchShortcutPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft/Windows/Start Menu/Programs/AHK Manager Portable.lnk'
$searchShortcutBefore = $null
$retiredDataHashes = @{}
$signingFixture = $null
$signingFixtureCleanup = 'NotCreated'

function Check([bool]$Condition, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw $Name }
    Write-Host "PASS $Name"
}
function Trace([string]$Message) { $line = [DateTimeOffset]::UtcNow.ToString('O') + ' ' + $Message; $trace.Add($line); Write-Host $line }
function Write-Json([string]$Name, [object]$Value) { [IO.File]::WriteAllText((Join-Path $OutputRoot $Name), ($Value | ConvertTo-Json -Depth 35), $utf8) }
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Same-Path([string]$First, [string]$Second) {
    return [String]::Equals([IO.Path]::GetFullPath($First), [IO.Path]::GetFullPath($Second), [StringComparison]::OrdinalIgnoreCase)
}
function Check-SearchShortcut([string]$Phase) {
    $launcher = Join-Path $portableRoot 'Bezoro.AhkManager.exe'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $matched = $false
    $observation = $null
    do {
        if ($session.Process.HasExited -or -not [AhkManager.TestSupport.InstalledUpdaterUi]::Matches($session.Process.Id, $session.Image, $session.Started)) { throw 'The owned portable session ended before its Search shortcut could be verified.' }
        $shell = $null
        $shortcut = $null
        try {
            if (Test-Path -LiteralPath $searchShortcutPath) {
                $file = Get-Item -LiteralPath $searchShortcutPath -Force
                if ($file -isnot [IO.FileInfo] -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -le 0 -or $file.Length -gt 1MB) { throw 'The portable Search shortcut is not a bounded regular file.' }
                $before = Hash $searchShortcutPath
                $shell = New-Object -ComObject WScript.Shell
                $shortcut = $shell.CreateShortcut($searchShortcutPath)
                $target = [string]$shortcut.TargetPath
                $workingDirectory = [string]$shortcut.WorkingDirectory
                $icon = [string]$shortcut.IconLocation
                $arguments = [string]$shortcut.Arguments
                $description = [string]$shortcut.Description
                $iconSeparator = $icon.LastIndexOf(',')
                $iconPath = if ($iconSeparator -gt 0) { $icon.Substring(0, $iconSeparator).Trim('"') } else { '' }
                $iconIndex = if ($iconSeparator -gt 0) { $icon.Substring($iconSeparator + 1).Trim() } else { '' }
                $observation = [ordered]@{ fixture = $true; actualWindowsShell = $true; phase = $Phase; path = $searchShortcutPath; target = $target; workingDirectory = $workingDirectory; icon = $icon; arguments = $arguments; description = $description; sha256 = $before }
                $matched = $target.Length -gt 0 -and $workingDirectory.Length -gt 0 -and $iconPath.Length -gt 0 -and (Same-Path $target $launcher) -and (Same-Path $workingDirectory $portableRoot) -and (Same-Path $iconPath $launcher) -and $iconIndex -ceq '0' -and $arguments -ceq '--foreground' -and $description -ceq 'Manage AutoHotkey scripts with AHK Manager Portable.' -and (Hash $searchShortcutPath) -ceq $before
            }
            else { $observation = [ordered]@{ fixture = $true; actualWindowsShell = $true; phase = $Phase; path = $searchShortcutPath; absent = $true } }
        }
        catch { $observation = [ordered]@{ fixture = $true; actualWindowsShell = $true; phase = $Phase; path = $searchShortcutPath; error = $_.Exception.Message } }
        finally {
            if ($shortcut -and [Runtime.InteropServices.Marshal]::IsComObject($shortcut)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
            if ($shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        }
        if ($matched) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json ("search-shortcut-$Phase.json") $observation
    Check $matched 'the actual current-user Windows Search shortcut launches the root executable in its folder with the correct icon and foreground argument'
}
function No-Registration {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                foreach ($path in $registryPaths) {
                    $key = $base.OpenSubKey($path)
                    try { if ($key) { return $false } }
                    finally { if ($key) { $key.Dispose() } }
                }
            }
            finally { $base.Dispose() }
        }
    }
    return $true
}
function New-PrivateDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw 'A fixture private directory already exists.' }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($sid))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($trusted in @($sid, 'S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($trusted), [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Is-Private([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    $trusted = @($sid, 'S-1-5-18', 'S-1-5-32-544')
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { return $false }
    $writes = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteAttributes, WriteExtendedAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and ($rule.FileSystemRights -band $writes) -ne 0 -and $rule.IdentityReference.Value -notin $trusted) { return $false }
    }
    return $true
}
function Save-OperationEvidence([string]$Phase, [string]$Operation) {
    $destination = Join-Path $OutputRoot $Phase
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Operation -File -Force) {
        if ($file.Name -match '\A(?:plan|ready|commit|cancel|outcome|helper-refusal)\.json\z|\Ahelper\.log\z' -and $file.Length -le 8MB) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) }
    }
}
function Seed-Package($Release, [string]$Archive, [string]$Manifest, [string]$Signature) {
    $directory = Join-Path $updatesRoot (Join-Path 'installers' $Release.Version)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::Copy($Archive, (Join-Path $directory 'archive.zip'), $false)
    [IO.File]::Copy($Manifest, (Join-Path $directory 'release.json'), $false)
    $signatureFile = Get-Item -LiteralPath $Signature
    if ($signatureFile -isnot [IO.FileInfo] -or $signatureFile.Length -le 0 -or $signatureFile.Length -gt 65536) { throw 'The isolated portable manifest signature is absent or oversized.' }
    [IO.File]::Copy($Signature, (Join-Path $directory 'release.json.p7s'), $false)
    $receipt = [ordered]@{ schemaVersion = 1; updateProtocol = 2; version = $Release.Version; sha256 = $Release.Sha256; bytes = $Release.ArchiveBytes }
    [IO.File]::WriteAllText((Join-Path $directory 'receipt.json'), ($receipt | ConvertTo-Json -Compress), $utf8)
}
function Wait-Ready($Current, [string]$ExpectedVersion) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $state = Invoke-PortableEvaluation $Current 'window.ahkManager ? window.ahkManager.getState() : null'
        if ($state -and $state.connection -eq 'ready' -and -not $state.busy) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json ("host-$ExpectedVersion.json") $state
    Check ($state -and $state.connection -eq 'ready' -and $state.authorityMode -eq 'Administrator' -and -not $state.snapshot.readOnly) 'the portable application has its real administrative backend and writable profile'
    Check ($state.snapshot.scripts.Count -eq 1 -and $state.snapshot.scripts[0].id -eq $scriptId -and $state.snapshot.scripts[0].path -eq $scriptFile -and $state.snapshot.scripts[0].runAtLogin -eq $false) 'the imported external script identity and disabled automatic execution are preserved'
    Check ($state.snapshot.settings.editorArguments -contains 'portable-ci-preservation') 'the imported user preferences survive in the real backend state'
    $update = Invoke-PortableEvaluation $Current 'window.ahkManager.getUpdateState()'
    Check ($update.currentVersion -ceq $ExpectedVersion) 'the packaged app reports its exact coherent release version'
    $storage = Invoke-PortableEvaluation $Current 'window.ahkManager.getStorageState()'
    Check ($storage.mode -ceq 'portable' -and $storage.currentDataDirectory -eq $dataRoot -and $storage.defaultDataDirectory -eq (Join-Path $portableRoot 'Data') -and $null -eq $storage.pendingDataDirectory -and $null -eq $storage.error) 'the real storage API reports the exact selected portable data folder'
}
function Capture-Preferences {
    $opened = Invoke-PortableEvaluation $session '(() => { const button = Array.from(document.querySelectorAll("button")).find(value => value.textContent.trim() === "Preferences"); if (!button || button.disabled) return false; button.click(); return true; })()'
    Check $opened 'the actual portable Preferences button opens its dialog'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $shown = $null
    do {
        $shown = Invoke-PortableEvaluation $session '(() => { const section = document.querySelector("[aria-labelledby=settings-data-location-title]"); const input = section?.querySelector("input"); if (!input) return null; section.scrollIntoView({block:"center"}); return { current: input.value, editable: !input.readOnly, change: Array.from(section.querySelectorAll("button")).some(value => value.textContent.trim() === "Change location…") }; })()'
        if ($shown) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check ($shown -and $shown.current -eq $dataRoot -and -not $shown.editable -and $shown.change) 'the actual Preferences data section displays the native-selected location and portable change action'
    Invoke-PortableEvaluation $session 'new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))' | Out-Null
    $capture = Invoke-PortableCdp $session 'Page.captureScreenshot' @{ format = 'png'; fromSurface = $true }
    $bytes = [Convert]::FromBase64String($capture.data)
    if ($bytes.Length -lt 8 -or $bytes.Length -gt 10MB -or [Convert]::ToHexString($bytes[0..7]) -cne '89504E470D0A1A0A') { throw 'The actual portable Preferences capture was not a bounded PNG.' }
    [IO.File]::WriteAllBytes((Join-Path $OutputRoot 'portable-data-preferences.png'), $bytes)
    Write-Json 'portable-data-preferences.json' ([ordered]@{ fixture = $true; actualApplication = $true; actualBackend = $true; scenario = $Scenario; dataDirectory = $dataRoot; currentLocationReadOnly = $true; changeActionPresent = $true; screenshot = 'portable-data-preferences.png' })
    $closed = Invoke-PortableEvaluation $session '(() => { const dialog = document.querySelector("[role=dialog]"); const button = dialog && Array.from(dialog.querySelectorAll("button")).find(value => value.textContent.trim() === "Cancel"); if (!button || button.disabled) return false; button.click(); return true; })()'
    Check $closed 'the actual portable Preferences dialog closes without changing settings'
    Invoke-PortableEvaluation $session 'new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))' | Out-Null
}
function Move-PortableData {
    $originalDataRoot = $dataRoot
    $originalUpdatesRoot = $updatesRoot
    Invoke-PortableCdp $session 'Page.bringToFront' | Out-Null
    $null = [AhkManager.TestSupport.InstalledUpdaterUi]::Focus($session.Process.Id, $session.Image, $session.Started)
    $opened = Invoke-PortableEvaluation $session '(() => { const button = Array.from(document.querySelectorAll("button")).find(value => value.textContent.trim() === "Preferences"); if (!button || button.disabled) return false; button.click(); return true; })()'
    Check $opened 'the real Preferences dialog opens for data relocation'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $chosen = $false
    do {
        $chosen = Invoke-PortableEvaluation $session '(() => { const section = document.querySelector("[aria-labelledby=settings-data-location-title]"); const button = section && Array.from(section.querySelectorAll("button")).find(value => value.textContent.trim() === "Change location…"); if (!button || button.disabled) return false; button.click(); return true; })()'
        if ($chosen) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check $chosen 'the real portable Change location button requests the native picker'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $picked = $false
    do {
        $picked = [AhkManager.TestSupport.PortableDataPickerUi]::Choose($session.Process.Id, $session.Image, $session.Started, $customDataRoot)
        if ($picked) { break }
        Start-Sleep -Milliseconds 100
    } while (-not $session.Process.HasExited -and [DateTime]::UtcNow -lt $deadline)
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'data-picker-windows.txt'), [AhkManager.TestSupport.PortableDataPickerUi]::Describe($session.Process.Id, $session.Image, $session.Started), $utf8)
    Check $picked 'only the exact owned native folder picker receives and confirms the custom data path'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $prepared = $null
    do {
        $prepared = Invoke-PortableEvaluation $session 'window.ahkManager.getStorageState()'
        if ($prepared.pendingDataDirectory -eq $customDataRoot) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json 'data-move-prepared.json' $prepared
    Check ($prepared.currentDataDirectory -eq $originalDataRoot -and $prepared.pendingDataDirectory -eq $customDataRoot) 'choosing the native folder prepares migration without changing the active data location'
    $intentPath = Join-Path $portableRoot 'portable-data-move.json'
    Check ((Test-Path -LiteralPath $intentPath) -and (Hash (Join-Path $portableRoot 'portable.json')) -ceq $markerHash -and @(Get-ChildItem -LiteralPath $customDataRoot -Force).Count -eq 0) 'preparing a real data move leaves the active pointer and empty destination unchanged'
    $restartedAfter = [DateTime]::UtcNow
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $restart = $false
    do {
        $restart = Invoke-PortableEvaluation $session '(() => { const section = document.querySelector("[aria-labelledby=settings-data-location-title]"); const button = section && Array.from(section.querySelectorAll("button")).find(value => value.textContent.trim() === "Restart Manager"); if (!button || button.disabled) return false; button.click(); return true; })()'
        if ($restart) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check $restart 'the actual prepared-data Restart Manager button starts a normal restart'
    Check ($session.Process.WaitForExit(30000) -and $session.Process.ExitCode -eq 0) 'the original portable Manager completes its normal backend shutdown before migration'
    foreach ($subdirectory in @('Profile', 'Updates')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $originalDataRoot $subdirectory) -File -Recurse) { $retiredDataHashes[$file.FullName] = Hash $file.FullName }
    }
    Close-PortableSession $session
    $script:session = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $configuration = $null
    do {
        $configuration = Get-Content -LiteralPath (Join-Path $portableRoot 'portable.json') -Raw | ConvertFrom-Json
        if ($configuration.dataDirectory -eq $customDataRoot -and -not (Test-Path -LiteralPath $intentPath) -and (Test-Path -LiteralPath (Join-Path $customDataRoot 'Chromium/Local State'))) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check ($configuration.dataDirectory -eq $customDataRoot -and -not (Test-Path -LiteralPath $intentPath)) 'a fresh normal startup completes the data copy and commits the selected location once'
    Check ((Test-Path -LiteralPath (Join-Path $customDataRoot 'Profile/settings.json')) -and (Test-Path -LiteralPath (Join-Path $customDataRoot 'Chromium/Local State'))) 'the restarted application uses the selected profile and a fresh Chromium directory at the custom location'
    foreach ($release in @($baseline, $candidate)) {
        $previous = Join-Path $originalUpdatesRoot "installers/$($release.Version)/archive.zip"
        $moved = Join-Path $customDataRoot "Updates/installers/$($release.Version)/archive.zip"
        Check ((Hash $previous) -ceq $release.Sha256 -and (Hash $moved) -ceq $release.Sha256) 'data relocation transfers the exact portable package history and preserves the original cache'
    }
    Check ((Hash (Join-Path $customDataRoot 'Profile/ci-state-sentinel.txt')) -ceq $stateHash -and (Hash $scriptFile) -ceq $scriptHash -and -not $inert.HasExited) 'data relocation preserves user data, external scripts and unrelated running processes'
    Check ((Get-Acl -LiteralPath $customParent).Sddl -ceq $customParentAcl -and (No-Registration)) 'data relocation changes neither parent directory permissions nor installer registration'
    Close-ReopenedPortableSession -Root $portableRoot -Earliest $restartedAfter
    $script:dataRoot = $customDataRoot
    $script:updatesRoot = Join-Path $dataRoot 'Updates'
    $script:stateSentinel = Join-Path $dataRoot 'Profile/ci-state-sentinel.txt'
    $script:markerHash = Hash (Join-Path $portableRoot 'portable.json')
    $reopened = Join-Path $OutputRoot 'after-data-move'
    [IO.Directory]::CreateDirectory($reopened) | Out-Null
    $script:session = Start-PortableSession -Root $portableRoot -EvidenceRoot $reopened -ViaLauncher
    Wait-Ready $session $baseline.Version
    Capture-Preferences
    foreach ($path in $retiredDataHashes.Keys) { Check ((Hash $path) -ceq $retiredDataHashes[$path]) "the retired data backup preserves $([IO.Path]::GetFileName($path))" }
    Close-PortableSession $session
    $script:session = $null
}
function Run-Update([string]$Phase, $Target) {
    $phaseRoot = Join-Path $OutputRoot $Phase
    [IO.Directory]::CreateDirectory($phaseRoot) | Out-Null
    $script:session = Start-PortableSession -Root $portableRoot -EvidenceRoot $phaseRoot
    Wait-Ready $session $currentVersion
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $update = Invoke-PortableEvaluation $session 'window.ahkManager.getUpdateState()'
        $choice = @($update.releases | Where-Object { $_.version -ceq $Target.Version -and $_.cached -and $_.canInstall }) | Select-Object -First 1
        if ($choice -and $update.phase -notin @('checking', 'downloading', 'installing')) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json ("$Phase-before.json") $update
    Check ($null -ne $choice) 'the actual portable updater exposes the exact verified cached target'
    Invoke-PortableCdp $session 'Page.bringToFront' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $null = [AhkManager.TestSupport.InstalledUpdaterUi]::Focus($session.Process.Id, $session.Image, $session.Started)
        $ready = Invoke-PortableEvaluation $session '({ visible: document.visibilityState === "visible", focused: document.hasFocus(), mounted: !!document.querySelector("main"), dialog: !!document.querySelector("[role=dialog]") })'
        if ($ready.visible -and $ready.focused -and $ready.mounted -and -not $ready.dialog) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check ($ready.visible -and $ready.focused -and $ready.mounted -and -not $ready.dialog) 'the real portable renderer is visible, focused and ready for update consent'
    Invoke-PortableEvaluation $session 'new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))' | Out-Null
    $before = @(Get-ChildItem -LiteralPath (Join-Path $updatesRoot 'operations') -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
    $id = ConvertTo-Json -InputObject $choice.id -Compress
    Invoke-PortableEvaluation $session ("globalThis.__portableUpdate = { pending: true }; void window.ahkManager.installUpdate($id).then(value => globalThis.__portableUpdate = {pending:false,value}, error => globalThis.__portableUpdate = {pending:false,error:String(error)}); true") | Out-Null
    $script:helperInFlight = $true
    $confirmed = $false
    $result = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($session.Process.HasExited) { break }
        if (-not $confirmed -and [AhkManager.TestSupport.InstalledUpdaterUi]::Confirm($session.Process.Id, $session.Image, $session.Started, $Target.Version)) { $confirmed = $true; Trace "$Phase native consent confirmed for $($Target.Version)." }
        try { $result = Invoke-PortableEvaluation $session 'globalThis.__portableUpdate' }
        catch { if ($confirmed -and $session.Socket.State -ne [Net.WebSockets.WebSocketState]::Open -and $session.Process.WaitForExit(30000)) { break }; throw }
        if ($result -and -not $result.pending) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check $confirmed 'the actual portable update received exact-version native consent'
    Write-Json ("$Phase-result.json") $result
    Check ($session.Process.WaitForExit(30000) -and $session.Process.ExitCode -eq 0) 'the authenticated portable application exits normally before payload replacement'
    $operations = @(Get-ChildItem -LiteralPath (Join-Path $updatesRoot 'operations') -Directory | Where-Object { $_.Name -notin $before })
    Check ($operations.Count -eq 1) 'one explicit action creates exactly one portable update operation'
    $operation = $operations[0].FullName
    $plan = Get-Content -LiteralPath (Join-Path $operation 'plan.json') -Raw | ConvertFrom-Json
    $readyReceipt = Get-Content -LiteralPath (Join-Path $operation 'ready.json') -Raw | ConvertFrom-Json
    $commit = Get-Content -LiteralPath (Join-Path $operation 'commit.json') -Raw | ConvertFrom-Json
    Check ($plan.schemaVersion -eq 2 -and $plan.deployment -ceq 'portable' -and $plan.parentPid -eq $session.Process.Id -and ([DateTimeOffset]$plan.parentStartedAtUtc).ToUnixTimeMilliseconds() -eq [DateTimeOffset]::new($session.Started).ToUnixTimeMilliseconds() -and $plan.installDirectory -eq (Join-Path $portableRoot 'App') -and $plan.installerSha256 -ceq $Target.Sha256) 'the native portable plan binds deployment, actual parent session, application directory and exact package hash'
    Check ($readyReceipt.operationId -eq $plan.operationId -and $commit.operationId -eq $plan.operationId -and $readyReceipt.parentPid -eq $session.Process.Id -and $commit.parentPid -eq $session.Process.Id) 'portable readiness and shutdown commit identify the same authenticated update'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $outcomePath = Join-Path $operation 'outcome.json'
    while (-not (Test-Path -LiteralPath $outcomePath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $outcomePath)) { throw 'The portable update outcome is unknown; all processes and recovery files were preserved.' }
    $outcome = Get-Content -LiteralPath $outcomePath -Raw | ConvertFrom-Json
    Save-OperationEvidence $Phase $operation
    Check ($outcome.operationId -eq $plan.operationId -and $outcome.outcome -ceq 'Installed' -and $outcome.version -ceq $Target.Version) 'the real native portable transaction installs the exact requested package'
    $script:helperInFlight = $false
    Check ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $portableRoot 'App/Bezoro.AhkManager.exe')).FileVersion -ceq $Target.Version -and [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $portableRoot 'Bezoro.AhkManager.exe')).FileVersion -ceq $Target.Version) 'both root launcher and application retain the exact requested release resources'
    Check (No-Registration) 'portable initialization and updates create no installer or uninstall registration'
    Check ((Hash (Join-Path $portableRoot 'portable.json')) -ceq $markerHash -and (Hash $stateSentinel) -ceq $stateHash -and (Hash $scriptFile) -ceq $scriptHash) 'portable configuration, user Data and external script bytes are preserved'
    foreach ($path in $legacyHashes.Keys) { Check ((Hash $path) -ceq $legacyHashes[$path]) "portable update preserves legacy source $([IO.Path]::GetFileName($path))" }
    foreach ($path in $retiredDataHashes.Keys) { Check ((Hash $path) -ceq $retiredDataHashes[$path]) "portable update preserves retired data $([IO.Path]::GetFileName($path))" }
    $key = $registry.OpenSubKey($runPath)
    try { Check ($key -and $key.GetValue($runValue) -ceq $startupCommand) 'the unrelated Windows startup entry is unchanged' }
    finally { if ($key) { $key.Dispose() } }
    Check (-not $inert.HasExited) 'the unrelated inert process continues running through portable update'
    Check ((Get-Acl -LiteralPath $parent).Sddl -ceq $parentAcl) 'the portable updater leaves the shared parent directory permissions unchanged'
    Close-PortableSession $session
    $script:session = $null
    Close-ReopenedPortableSession -Root $portableRoot -Earliest $phaseStarted
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $workspaces = @(Get-ChildItem -LiteralPath $portableRoot -Directory -Force | Where-Object { $_.Name.StartsWith('.app-stage-', [StringComparison]::Ordinal) -or $_.Name.StartsWith('.app-previous-', [StringComparison]::Ordinal) })
        if ($workspaces.Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check ($workspaces.Count -eq 0) 'a durable successful portable install removes its verified transaction workspace so the folder can be moved normally'
}
function Move-PortableRoot {
    if ($Scenario -cne 'default' -or $session -or $helperInFlight) { throw 'Whole-folder relocation requires the normally closed default portable fixture.' }
    $originalRoot = $portableRoot
    $destination = Join-Path $parent 'Relocated Portable Manager & Data'
    Check (-not (Test-Path -LiteralPath $destination) -and (Join-Path $originalRoot 'Data') -eq $dataRoot) 'whole-folder relocation starts with the default relative Data folder and a fresh sibling destination'
    $configuration = Get-Content -LiteralPath (Join-Path $originalRoot 'portable.json') -Raw | ConvertFrom-Json
    Check ($configuration.dataDirectory -ceq 'Data') 'the portable configuration retains a relative Data location before the folder is moved'
    $sourceFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $dataRoot 'Profile') -File -Recurse -Force) { $sourceFiles[[IO.Path]::GetRelativePath($originalRoot, $file.FullName)] = Hash $file.FullName }
    foreach ($release in @($baseline, $candidate)) {
        foreach ($name in @('archive.zip', 'release.json', 'release.json.p7s', 'receipt.json')) {
            $file = Join-Path $updatesRoot "installers/$($release.Version)/$name"
            $sourceFiles[[IO.Path]::GetRelativePath($originalRoot, $file)] = Hash $file
        }
    }
    $priorShortcut = Hash $searchShortcutPath
    $processPrefix = $originalRoot + [IO.Path]::DirectorySeparatorChar
    $running = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($processPrefix, [StringComparison]::OrdinalIgnoreCase)) })
    Check ($running.Count -eq 0) 'normal portable shutdown leaves no application or helper process in the folder to be moved'
    [IO.Directory]::Move($originalRoot, $destination)
    $script:portableRoot = $destination
    $script:dataRoot = Join-Path $portableRoot 'Data'
    $script:updatesRoot = Join-Path $dataRoot 'Updates'
    $script:stateSentinel = Join-Path $dataRoot 'Profile/ci-state-sentinel.txt'
    Check (-not (Test-Path -LiteralPath $originalRoot) -and (Hash (Join-Path $portableRoot 'portable.json')) -ceq $markerHash -and (Hash $searchShortcutPath) -ceq $priorShortcut) 'moving the whole closed portable folder preserves its relative configuration and leaves shortcut repair to the real application'
    foreach ($relative in $sourceFiles.Keys) { Check ((Hash (Join-Path $portableRoot $relative)) -ceq $sourceFiles[$relative]) "whole-folder relocation preserves $relative" }
    Write-Json 'portable-root-relocation.json' ([ordered]@{ fixture = $true; actualDirectoryMove = $true; source = $originalRoot; destination = $portableRoot; relativeDataDirectory = 'Data'; dataDirectory = $dataRoot; sourceAbsent = $true; preservedFiles = $sourceFiles })
}
function Check-RelocatedHistory {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $history = $null
    do {
        $history = Invoke-PortableEvaluation $session 'window.ahkManager.getUpdateState()'
        $cached = @($history.releases | Where-Object { $_.cached -and $_.version -in @($baseline.Version, $candidate.Version) })
        if ($cached.Count -eq 2) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json 'relocated-update-state.json' $history
    Check ($cached.Count -eq 2 -and @($cached | Where-Object { $_.version -ceq $baseline.Version -and $_.installed }).Count -eq 1 -and @($cached | Where-Object { $_.version -ceq $candidate.Version -and $_.canInstall }).Count -eq 1) 'the real relocated app recognizes the installed baseline and verified cached update in its moved version history'
    foreach ($release in @($baseline, $candidate)) {
        $directory = Join-Path $updatesRoot "installers/$($release.Version)"
        $archive = Join-Path $directory 'archive.zip'
        $manifest = Join-Path $directory 'release.json'
        $manifestFile = Get-Item -LiteralPath $manifest
        if ($manifestFile -isnot [IO.FileInfo] -or $manifestFile.Length -le 0 -or $manifestFile.Length -gt 65536) { throw 'The moved version history manifest is not a bounded regular file.' }
        $verified = ConvertFrom-PortableReleaseJson (Get-Content -LiteralPath $manifest -Raw)
        $signature = if ($release.Version -ceq $baseline.Version) { $signingFixture.BaselineSignaturePath } else { $signingFixture.CandidateSignaturePath }
        $receipt = Get-Content -LiteralPath (Join-Path $directory 'receipt.json') -Raw | ConvertFrom-Json
        Check ($verified.Version -ceq $release.Version -and $verified.Commit -ceq $SourceCommit -and $verified.Sha256 -ceq $release.Sha256 -and $verified.ArchiveBytes -eq $release.ArchiveBytes -and (Hash $archive) -ceq $release.Sha256 -and (Get-Item -LiteralPath $archive).Length -eq $release.ArchiveBytes -and (Hash $manifest) -ceq $release.ManifestSha256 -and (Hash (Join-Path $directory 'release.json.p7s')) -ceq (Hash $signature) -and $receipt.schemaVersion -eq 1 -and $receipt.updateProtocol -eq 2 -and $receipt.version -ceq $release.Version -and $receipt.sha256 -ceq $release.Sha256 -and $receipt.bytes -eq $release.ArchiveBytes) 'the moved version history retains each exact verified package, manifest, cache receipt and authenticated signature'
    }
    $key = $registry.OpenSubKey($runPath)
    try { Check ($key -and $key.GetValue($runValue) -ceq $startupCommand) 'whole-folder relocation preserves the unrelated Windows startup entry' }
    finally { if ($key) { $key.Dispose() } }
    Check ((Hash $stateSentinel) -ceq $stateHash -and (Hash $scriptFile) -ceq $scriptHash -and -not $inert.HasExited) 'the relocated app retains its user state, external AutoHotkey file and unrelated running process'
    foreach ($path in $legacyHashes.Keys) { Check ((Hash $path) -ceq $legacyHashes[$path]) "whole-folder relocation preserves legacy source $([IO.Path]::GetFileName($path))" }
    Check ((Get-Acl -LiteralPath $parent).Sddl -ceq $parentAcl -and (No-Registration)) 'whole-folder relocation changes neither shared parent permissions nor installer registration'
}

try {
    foreach ($path in @($legacyRoot, $legacyChromium)) { if (Test-Path -LiteralPath $path) { throw 'An existing operational profile will not be opened by this fixture.' } }
    if (@(Get-Process -Name 'Bezoro.AhkManager' -ErrorAction SilentlyContinue).Count -or -not (No-Registration)) { throw 'An existing Manager or registered installation prevents the portable fixture.' }
    if (Test-Path -LiteralPath $searchShortcutPath) {
        if ($Scenario -ne 'foreign-root') { throw 'An existing portable Search shortcut will not be replaced by this fixture.' }
        $searchShortcutBefore = Hash $searchShortcutPath
    }
    $baseline = Read-PortableReleaseManifest -ArchivePath $BaselineArchive -ManifestPath $BaselineManifest -ExpectedCommit $SourceCommit
    $candidate = Read-PortableReleaseManifest -ArchivePath $CandidateArchive -ManifestPath $CandidateManifest -ExpectedCommit $SourceCommit
    Check ($baseline.Version -cne $candidate.Version) 'both coherent fixture packages bind the same exact CI source and distinct versions'
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $outputCreated = $true
    $parent = Join-Path $OutputRoot 'shared-parent'
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $acl = Get-Acl -LiteralPath $parent
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), [Security.AccessControl.FileSystemRights]::Modify, [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    Set-Acl -LiteralPath $parent -AclObject $acl
    $parentAcl = (Get-Acl -LiteralPath $parent).Sddl
    $portableRoot = Join-Path $parent 'Portable Manager'
    [IO.Compression.ZipFile]::ExtractToDirectory($BaselineArchive, $portableRoot)
    $publisherPolicyPath = Join-Path (Split-Path -Parent $BaselineManifest) 'publisher-policy.json'
    $policyFile = Get-Item -LiteralPath $publisherPolicyPath
    if ($policyFile -isnot [IO.FileInfo] -or $policyFile.Length -le 0 -or $policyFile.Length -gt 65536) { throw 'The test-only application publisher policy is absent or oversized.' }
    $policy = Get-Content -LiteralPath $publisherPolicyPath -Raw | ConvertFrom-Json
    $candidateZip = [IO.Compression.ZipFile]::OpenRead($CandidateArchive)
    try {
        $entries = @($candidateZip.Entries | Where-Object { $_.FullName -ieq 'App/resources/app-update.yml' })
        if ($entries.Count -ne 1 -or $entries[0].FullName -cne 'App/resources/app-update.yml' -or $entries[0].Length -le 0 -or $entries[0].Length -gt 65536) { throw 'The candidate archive has no exact bounded application publisher policy.' }
        $stream = $entries[0].Open()
        $buffer = [IO.MemoryStream]::new()
        try {
            $chunk = [byte[]]::new(4096)
            while (($read = $stream.Read($chunk, 0, $chunk.Length)) -gt 0) {
                if ($buffer.Length + $read -gt 65536) { throw 'The candidate publisher policy exceeds its bounded length.' }
                $buffer.Write($chunk, 0, $read)
            }
            if ($buffer.Length -ne $entries[0].Length) { throw 'The candidate publisher policy differs from its declared archive length.' }
            $policyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buffer.ToArray())).ToLowerInvariant()
        }
        finally { $buffer.Dispose(); $stream.Dispose() }
    }
    finally { $candidateZip.Dispose() }
    Check ($policy.sourceCommit -ceq $SourceCommit -and $policy.candidatePolicySha256 -ceq $policyHash) 'the exact candidate archive publisher policy matches the source-bound production parser metadata'
    $signingFixture = New-PortableSigningFixture -ApplicationRoot (Join-Path $portableRoot 'App') -CandidateManifestPath $CandidateManifest -BaselineManifestPath $BaselineManifest -PublisherPolicyPath $publisherPolicyPath -OutputRoot (Join-Path $OutputRoot 'signing-fixture')
    $signingFixtureCleanup = 'Pending'
    Write-Json 'portable-signing-proof.json' $signingFixture.Proof
    Check ($null -ne $signingFixture -and (Test-Path -LiteralPath $signingFixture.CandidateSignaturePath -PathType Leaf) -and (Test-Path -LiteralPath $signingFixture.BaselineSignaturePath -PathType Leaf)) 'both exact portable manifests have authenticated fixture signatures before any cache entry is seeded'
    $dataRoot = Join-Path $portableRoot 'Data'
    if ($Scenario -eq 'custom-data') {
        $customParent = Join-Path $OutputRoot 'custom-state-parent'
        [IO.Directory]::CreateDirectory($customParent) | Out-Null
        $customParentAcl = (Get-Acl -LiteralPath $customParent).Sddl
        $customDataRoot = Join-Path $customParent 'Portable Data'
        [IO.Directory]::CreateDirectory($customDataRoot) | Out-Null
    }
    $markerHash = Hash (Join-Path $portableRoot 'portable.json')
    if ($Scenario -eq 'foreign-root') {
        $foreign = Join-Path $portableRoot 'unrelated.txt'
        [IO.File]::WriteAllText($foreign, 'An unrelated existing file must never authorize initialization.', $utf8)
        $foreignHash = Hash $foreign
        $rootAcl = (Get-Acl -LiteralPath $portableRoot).Sddl
        $session = Start-PortableSession -Root $portableRoot -EvidenceRoot $OutputRoot -ViaLauncher -ExpectStartupRefusal
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $dismissed = $false
        do {
            if ([AhkManager.TestSupport.PortableStartupUi]::DismissRefusal($session.Process.Id, $session.Image, $session.Started, $readStartupDialog)) { $dismissed = $true; break }
            Start-Sleep -Milliseconds 100
        } while (-not $session.Process.HasExited -and [DateTime]::UtcNow -lt $deadline)
        Write-Json 'foreign-root-windows.json' ([AhkManager.TestSupport.InstalledUpdaterUi]::Describe($session.Process.Id, $session.Image, $session.Started))
        Write-Json 'foreign-root-observation.json' ([ordered]@{ fixture = $true; dismissed = $dismissed; processExited = $session.Process.HasExited; observation = [AhkManager.TestSupport.PortableStartupUi]::LastObservation })
        Check ($dismissed -and $session.Process.WaitForExit(30000) -and $session.Process.ExitCode -eq 65) 'the actual portable app refuses an unknown root file and exits after its exact owned error dialog closes'
        Check ((Hash $foreign) -ceq $foreignHash -and (Get-Acl -LiteralPath $portableRoot).Sddl -ceq $rootAcl -and (Get-Acl -LiteralPath $parent).Sddl -ceq $parentAcl) 'failed initialization preserves unrelated files and both directory permission sets'
        Check (-not (Test-Path -LiteralPath $dataRoot) -and (No-Registration)) 'refused portable startup creates neither user Data nor installer registration'
        $shortcutPreserved = if ($null -eq $searchShortcutBefore) { -not (Test-Path -LiteralPath $searchShortcutPath) } else { (Test-Path -LiteralPath $searchShortcutPath -PathType Leaf) -and (Hash $searchShortcutPath) -ceq $searchShortcutBefore }
        Check $shortcutPreserved 'refused portable startup creates no Search shortcut and preserves any pre-existing entry'
        $session.Process.Dispose(); $session = $null
        $complete = $true
    }
    else {
        New-PrivateDirectory $legacyRoot
        $scriptFile = Join-Path $OutputRoot 'external-preserved.ahk'
        [IO.File]::WriteAllText($scriptFile, '; Inert external script fixture. Never executed.', $utf8)
        $scriptHash = Hash $scriptFile
        $scriptId = [Guid]::NewGuid().ToString()
        $settings = [ordered]@{
            Schema = 1
            Scripts = @([ordered]@{ Id = $scriptId; Path = $scriptFile; FriendlyName = 'Portable CI external script'; Description = 'Preserved without execution'; CollectionId = $null; RunAtLogin = $false; RunAsAdministrator = $false; LaunchBlocked = $false; LegacyGuard = $false; LegacySourcePath = $null; LegacySourceHash = $null; InterpreterPath = $null; ManualHotkeys = @() })
            Collections = @()
            Settings = [ordered]@{ InterpreterPath = $null; EditorPath = $null; EditorArguments = @('portable-ci-preservation'); LaunchManagerAtLogin = $false; LaunchManagerAsAdministrator = $false; ShowWindowAtLogin = $true; StartInTray = $false; Theme = 2; CloseBehavior = 1; Window = @{ X = 80; Y = 80; Width = 1200; Height = 780; Maximized = $false; Dpi = 96 } }
            LastLoginKey = 'portable-ci-preserved-login-key'
        }
        [IO.File]::WriteAllText((Join-Path $legacyRoot 'settings.json'), ($settings | ConvertTo-Json -Depth 15), $utf8)
        [IO.File]::WriteAllText((Join-Path $legacyRoot 'operations.json'), '{"Schema":1,"Operations":[]}', $utf8)
        $activity = @{ Id = [Guid]::NewGuid().ToString(); Timestamp = [DateTimeOffset]::UtcNow.ToString('O'); ScriptId = $null; Operation = $null; Outcome = 2; Code = 0; Detail = 'Portable profile import fixture.' }
        [IO.File]::WriteAllText((Join-Path $legacyRoot 'activity.jsonl'), (($activity | ConvertTo-Json -Compress) + "`n"), $utf8)
        $oldCache = Join-Path $legacyRoot 'Updates/installers/1.3.2'
        [IO.Directory]::CreateDirectory($oldCache) | Out-Null
        [IO.File]::WriteAllText((Join-Path $oldCache 'installer.exe'), 'Unexecuted legacy installer fixture.', $utf8)
        [IO.File]::WriteAllText((Join-Path $oldCache 'receipt.json'), '{"schemaVersion":1,"updateProtocol":1,"version":"1.3.2","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":36}', $utf8)
        [IO.File]::WriteAllText((Join-Path $legacyRoot 'Updates/update-channel.json'), '{"schemaVersion":1,"channel":"test"}', $utf8)
        $legacyHashes = @{}
        foreach ($file in Get-ChildItem -LiteralPath $legacyRoot -File -Recurse) { $legacyHashes[$file.FullName] = Hash $file.FullName }
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
        $key = $registry.CreateSubKey($runPath, $true)
        $startupCommand = '"' + (Join-Path $env:WINDIR 'System32/cmd.exe') + '" /d /c exit 0'
        try { $key.SetValue($runValue, $startupCommand, [Microsoft.Win32.RegistryValueKind]::String); $startupWritten = $true }
        finally { $key.Dispose() }
        $waiter = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $waiter.UseShellExecute = $false; $waiter.RedirectStandardInput = $true
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', '[Console]::In.ReadLine() | Out-Null')) { $waiter.ArgumentList.Add($argument) }
        $inert = [Diagnostics.Process]::Start($waiter)
        $session = Start-PortableSession -Root $portableRoot -EvidenceRoot $OutputRoot -ViaLauncher
        Wait-Ready $session $baseline.Version
        Check-SearchShortcut 'initial'
        Capture-Preferences
        Check ((Is-Private $portableRoot) -and (Is-Private (Join-Path $portableRoot 'App')) -and (Is-Private $dataRoot)) 'first real launch secures only the dedicated portable runtime and Data folders'
        Check ((Get-Acl -LiteralPath $parent).Sddl -ceq $parentAcl) 'first launch preserves the permissive shared parent permissions'
        $profile = Join-Path $dataRoot 'Profile'
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $profile 'migration-backup') -Filter settings.json -File -Recurse)
        Check ($backups.Count -eq 1 -and (Hash $backups[0].FullName) -ceq $legacyHashes[(Join-Path $legacyRoot 'settings.json')]) 'automatic portable import preserves an exact backup of the original settings'
        Close-PortableSession $session; $session = $null
        $updatesRoot = Join-Path $dataRoot 'Updates'
        Check (-not (Test-Path -LiteralPath (Join-Path $updatesRoot 'installers/1.3.2/installer.exe'))) 'legacy NSIS packages are not imported into portable package history'
        $stateSentinel = Join-Path $profile 'ci-state-sentinel.txt'
        [IO.File]::WriteAllText($stateSentinel, 'Portable user state survives update and rollback.', $utf8)
        $stateHash = Hash $stateSentinel
        Seed-Package $baseline $BaselineArchive $BaselineManifest $signingFixture.BaselineSignaturePath
        Seed-Package $candidate $CandidateArchive $CandidateManifest $signingFixture.CandidateSignaturePath
        [IO.File]::WriteAllText((Join-Path $updatesRoot 'update-channel.json'), '{"schemaVersion":1,"channel":"test"}', $utf8)
        if ($Scenario -eq 'custom-data') {
            $moveRoot = Join-Path $OutputRoot 'before-data-move'
            [IO.Directory]::CreateDirectory($moveRoot) | Out-Null
            $session = Start-PortableSession -Root $portableRoot -EvidenceRoot $moveRoot -ViaLauncher
            Wait-Ready $session $baseline.Version
            Move-PortableData
        }
        $currentVersion = $baseline.Version
        $phaseStarted = [DateTime]::UtcNow
        Run-Update 'update' $candidate
        $currentVersion = $candidate.Version
        $phaseStarted = [DateTime]::UtcNow
        Run-Update 'rollback' $baseline
        if ($Scenario -eq 'default') { Move-PortableRoot }
        $finalRoot = Join-Path $OutputRoot 'final-launch'
        [IO.Directory]::CreateDirectory($finalRoot) | Out-Null
        $session = Start-PortableSession -Root $portableRoot -EvidenceRoot $finalRoot -ViaLauncher
        Wait-Ready $session $baseline.Version
        Check-SearchShortcut 'final'
        if ($Scenario -eq 'default') { Check-RelocatedHistory }
        Close-PortableSession $session; $session = $null
        $complete = $true
    }
}
catch { $failure = $_.Exception.Message; Write-Error -ErrorAction Continue $failure }
finally {
    if ($outputCreated -and $updatesRoot -and (Test-Path -LiteralPath (Join-Path $updatesRoot 'operations'))) {
        try {
            foreach ($operation in @(Get-ChildItem -LiteralPath (Join-Path $updatesRoot 'operations') -Directory | Select-Object -First 8)) { Save-OperationEvidence ('operation-' + $operation.Name) $operation.FullName }
        }
        catch { $trace.Add('Read-only operation evidence could not be completed: ' + $_.Exception.Message) }
    }
    if ($session -and -not $helperInFlight -and $Scenario -ne 'foreign-root') { try { Close-PortableSession $session } catch { $failure = "Normal portable close failed: $($_.Exception.Message)" } }
    if ($startupWritten -and $registry -and -not $helperInFlight) {
        $key = $registry.OpenSubKey($runPath, $true)
        try { if ($key -and $key.GetValue($runValue) -ceq $startupCommand) { $key.DeleteValue($runValue, $false) } }
        finally { if ($key) { $key.Dispose() } }
    }
    if ($inert) { $inert.StandardInput.Close(); if (-not $inert.WaitForExit(10000)) { $failure = 'The unrelated fixture process did not exit after EOF; it was preserved.' }; $inert.Dispose() }
    if ($registry) { $registry.Dispose() }
    if ($signingFixture) {
        try {
            $owned = @(Get-CimInstance Win32_Process -Filter "Name='Bezoro.AhkManager.exe'" | Where-Object { $_.ExecutablePath -eq (Join-Path $portableRoot 'App/Bezoro.AhkManager.exe') -or $_.ExecutablePath -eq (Join-Path $portableRoot 'Bezoro.AhkManager.exe') })
            if (-not $helperInFlight -and $owned.Count -eq 0) { Remove-PortableSigningFixture -Fixture $signingFixture; $signingFixtureCleanup = 'Removed' }
            else { $signingFixtureCleanup = 'PreservedForUnknownSession'; $trace.Add('The isolated signing fixture was preserved because an owned app or update helper may still be active.') }
        }
        catch { $signingFixtureCleanup = 'CleanupFailed'; $failure = 'The isolated signing fixture could not be removed: ' + $_.Exception.Message }
    }
    $identity.Dispose()
    if ($outputCreated) {
        [IO.File]::WriteAllLines((Join-Path $OutputRoot 'portable-update.log'), $trace.ToArray(), $utf8)
        Write-Json 'portable-update-summary.json' ([ordered]@{ fixture = $true; scenario = $Scenario; commit = $SourceCommit; complete = $complete; unknownHelperOutcome = $helperInFlight; signingFixtureCleanup = $signingFixtureCleanup; checks = $checks.ToArray(); failure = $failure })
    }
}
if ($failure) { throw $failure }
if (-not $complete) { throw 'The portable update regression did not complete.' }
Write-Host "Portable updater passed: $($checks.Count) checks; scenario=$Scenario."
