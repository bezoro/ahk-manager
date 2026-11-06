#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$BaselineArchive,
    [Parameter(Mandatory = $true)][string]$BaselineManifest,
    [Parameter(Mandatory = $true)][string]$CandidateArchive,
    [Parameter(Mandatory = $true)][string]$CandidateManifest,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateSet('default', 'custom-data', 'foreign-root', 'download-cancel')][string]$Scenario = 'default',
    [switch]$PublishedBaseline,
    [ValidateRange(60, 900)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Portable updater regression requires a disposable GitHub-hosted Windows runner.' }
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute portable evidence directory.' }
Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'test-support/PortableUpdaterSession.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'test-support/PortableSigningFixture.psm1') -Force
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableUpdaterUi.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableStartupUi.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableDataPickerUi.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableShortcutSharingProbe.cs')
$readStartupDialog = [Delegate]::CreateDelegate([Func[IntPtr, int, string, DateTime, string]], [AhkManager.TestSupport.PortableUpdaterUi], 'AccessibleDialogText')
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
$baselineCommit = $null
$searchShortcutPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft/Windows/Start Menu/Programs/AHK Manager Portable.lnk'
$searchShortcutBefore = $null
$retiredDataHashes = @{}
$signingFixture = $null
$signingFixtureCleanup = 'NotCreated'
$shortcutSharingProbe = $null
$downloadProxy = $null

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
        if ($session.Process.HasExited -or -not [AhkManager.TestSupport.PortableUpdaterUi]::Matches($session.Process.Id, $session.Image, $session.Started)) { throw 'The owned portable session ended before its Search shortcut could be verified.' }
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
function Test-InheritedAccessRepair {
    $tool = Join-Path $PSScriptRoot 'Repair-PortableInheritedAccess.ps1'
    $application = Join-Path $portableRoot 'App'
    $rootPermissions = (Get-Acl -LiteralPath $portableRoot).Sddl
    $applicationOwner = (Get-Acl -LiteralPath $application).GetOwner([Security.Principal.SecurityIdentifier]).Value
    $dataPermissions = @{}
    $dataHashes = @{}
    foreach ($item in @((Get-Item -LiteralPath $dataRoot -Force)) + @(Get-ChildItem -LiteralPath $dataRoot -Force -Recurse)) {
        $dataPermissions[$item.FullName] = (Get-Acl -LiteralPath $item.FullName).Sddl
        if ($item -is [IO.FileInfo]) { $dataHashes[$item.FullName] = Hash $item.FullName }
    }
    $critical = @{}
    foreach ($relative in @('Bezoro.AhkManager.exe', 'portable-package.json', 'portable.json', 'App/Bezoro.AhkManager.exe', 'App/backend/AhkManager.Backend.exe', 'App/backend/update/AhkManager.UpdateHost.exe')) {
        $file = Join-Path $portableRoot $relative
        $critical[$file] = [ordered]@{ owner = (Get-Acl -LiteralPath $file).GetOwner([Security.Principal.SecurityIdentifier]).Value; sha256 = Hash $file }
    }
    # Reproduce private effective inheritance from the retained root without
    # changing owners, grants, user Data or the permissive shared parent.
    $applicationAcl = Get-Acl -LiteralPath $application
    $applicationAcl.SetAccessRuleProtection($false, $true)
    Set-Acl -LiteralPath $application -AclObject $applicationAcl
    $inheritedPermissions = (Get-Acl -LiteralPath $application).Sddl
    Check (-not (Get-Acl -LiteralPath $application).AreAccessRulesProtected -and (Is-Private $application)) 'the recovery fixture reproduces a private App folder that safely inherits from the protected portable root'
    $backupRoot = Join-Path $OutputRoot 'inherited-access-backups'
    New-PrivateDirectory $backupRoot
    $preview = & $tool -PortableRoot $portableRoot -BackupRoot $backupRoot
    Write-Json 'inherited-access-preview.json' $preview
    Check ($preview.Mode -ceq 'Preview' -and $preview.Version -ceq $baseline.Version -and (Same-Path $preview.PortableRoot $portableRoot) -and @($preview.ChangedPaths).Count -eq 1 -and (Same-Path $preview.ChangedPaths[0] $application) -and (Get-Acl -LiteralPath $application).Sddl -ceq $inheritedPermissions -and @(Get-ChildItem -LiteralPath $backupRoot -Force).Count -eq 0) 'the recovery preview identifies only inherited App permissions and leaves every permission and backup path unchanged'
    $redirectTarget = Join-Path $OutputRoot 'inherited-access-redirect-target'
    $redirect = Join-Path $OutputRoot 'inherited-access-backup-junction'
    New-PrivateDirectory $redirectTarget
    $null = New-Item -ItemType Junction -Path $redirect -Target $redirectTarget
    $redirectRefused = $false
    try { $null = & $tool -PortableRoot $portableRoot -BackupRoot (Join-Path $redirect 'uncreated-backup-parent') -Apply }
    catch { $redirectRefused = $_.Exception.GetBaseException().Message.StartsWith('Unsupported redirected entry: ', [StringComparison]::Ordinal) }
    Check ($redirectRefused -and @(Get-ChildItem -LiteralPath $redirectTarget -Force).Count -eq 0 -and (Get-Acl -LiteralPath $application).Sddl -ceq $inheritedPermissions -and @(Get-ChildItem -LiteralPath $backupRoot -Force).Count -eq 0) 'recovery refuses a redirected backup ancestor before creating any directory through it or changing App permissions'
    [IO.Directory]::Delete($redirect)
    $applied = & $tool -PortableRoot $portableRoot -BackupRoot $backupRoot -Apply
    Write-Json 'inherited-access-applied.json' $applied
    Check ($applied.Mode -ceq 'Applied' -and @($applied.ChangedPaths).Count -eq 1 -and (Same-Path $applied.ChangedPaths[0] $application) -and (Get-Acl -LiteralPath $application).AreAccessRulesProtected -and (Get-Acl -LiteralPath $application).GetOwner([Security.Principal.SecurityIdentifier]).Value -ceq $applicationOwner -and (Is-Private $application) -and (Test-Path -LiteralPath $applied.BackupDirectory -PathType Container) -and (Is-Private $applied.BackupDirectory) -and ([IO.Path]::GetFullPath($applied.BackupDirectory)).StartsWith([IO.Path]::GetFullPath($backupRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'the real recovery command freezes only App inheritance, preserves its owner and retains a private backup outside the portable installation'
    $again = & $tool -PortableRoot $portableRoot -BackupRoot $backupRoot
    Write-Json 'inherited-access-idempotent.json' $again
    Check ($again.Mode -ceq 'Preview' -and @($again.ChangedPaths).Count -eq 0 -and @($again.Targets).Count -eq 8 -and @($again.Targets | Where-Object { -not $_.Protected }).Count -eq 0 -and @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force).Count -eq 1) 'a subsequent recovery preview is idempotent and verifies all eight protected startup targets without another backup'
    $legacyApplicationAcl = Get-Acl -LiteralPath $application
    $legacyApplicationAcl.SetAccessRuleProtection($false, $true)
    Set-Acl -LiteralPath $application -AclObject $legacyApplicationAcl
    if ((Get-Acl -LiteralPath $application).AreAccessRulesProtected -or -not (Is-Private $application)) { throw 'The Windows PowerShell recovery fixture must begin with privately inherited App permissions.' }
    $windowsPowerShell = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $nativeModules = Join-Path (Split-Path -Parent $windowsPowerShell) 'Modules'
    $legacyPreviewFile = Join-Path $OutputRoot 'inherited-access-windows-powershell-preview.json'
    $legacyFailureFile = Join-Path $OutputRoot 'inherited-access-windows-powershell-failure.txt'
    $legacyParameters = [Convert]::ToBase64String($utf8.GetBytes((@{ tool = $tool; portableRoot = $portableRoot; application = $application; backupRoot = $backupRoot; output = $legacyPreviewFile } | ConvertTo-Json -Compress)))
    $legacyCommand = @'
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$stage = 'version'
$failurePath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__FAILURE_PATH__'))
function Save-Evidence([string]$Path, [string]$Text) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    if ($bytes.Length -gt 65536) { throw 'Windows PowerShell recovery evidence exceeded its limit.' }
    $output = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $output.Write($bytes, 0, $bytes.Length); $output.Flush($true) } finally { $output.Dispose() }
}
try {
    if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'The recovery compatibility check requires Windows PowerShell 5.1.' }
    foreach ($name in @('Security', 'Management', 'Utility')) {
        $stage = 'import-native-' + $name
        $module = 'Microsoft.PowerShell.' + $name
        Import-Module (Join-Path $PSHOME ('Modules\' + $module + '\' + $module + '.psd1')) -Force -ErrorAction Stop
    }
    $stage = 'parameters'
    $inputPaths = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PARAMETERS__')) | ConvertFrom-Json
    $before = Get-Acl -LiteralPath $inputPaths.application
    $owner = $before.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($before.AreAccessRulesProtected) { throw 'Windows PowerShell must apply a real inherited-permission repair.' }
    $stage = 'apply'
    $applied = & $inputPaths.tool -PortableRoot $inputPaths.portableRoot -BackupRoot $inputPaths.backupRoot -Apply
    $stage = 'preview'
    $result = & $inputPaths.tool -PortableRoot $inputPaths.portableRoot -BackupRoot $inputPaths.backupRoot
    $after = Get-Acl -LiteralPath $inputPaths.application
    $modules = @(Get-Module -Name Microsoft.PowerShell.Security, Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility | ForEach-Object { [ordered]@{ name = $_.Name; path = $_.Path; version = $_.Version.ToString() } })
    $evidence = [ordered]@{ fixture = $true; major = $PSVersionTable.PSVersion.Major; minor = $PSVersionTable.PSVersion.Minor; version = $PSVersionTable.PSVersion.ToString(); psHome = $PSHOME; modulePath = $env:PSModulePath; modules = $modules; ownerBefore = $owner; ownerAfter = $after.GetOwner([Security.Principal.SecurityIdentifier]).Value; protectedBefore = $before.AreAccessRulesProtected; protectedAfter = $after.AreAccessRulesProtected; applied = $applied; preview = $result }
    $stage = 'evidence'
    Save-Evidence $inputPaths.output ($evidence | ConvertTo-Json -Depth 20)
}
catch {
    $failure = $_
    $exception = $failure.Exception.ToString()
    $details = "PowerShell: $($PSVersionTable.PSVersion)`r`nPSHOME: $PSHOME`r`nPSModulePath: $env:PSModulePath`r`nStage: $stage`r`nErrorId: $($failure.FullyQualifiedErrorId)`r`n" + $exception.Substring(0, [Math]::Min(8192, $exception.Length))
    Save-Evidence $failurePath $details
    [Console]::Error.WriteLine('Windows PowerShell recovery failed during ' + $stage + '. Details: ' + $failurePath)
    exit 1
}
'@
    $legacyStart = [Diagnostics.ProcessStartInfo]::new($windowsPowerShell)
    $legacyStart.UseShellExecute = $false; $legacyStart.CreateNoWindow = $true; $legacyStart.RedirectStandardOutput = $true; $legacyStart.RedirectStandardError = $true
    # Windows PowerShell must not inherit PowerShell 7's incompatible module path.
    $legacyStart.Environment['PSModulePath'] = $nativeModules
    $legacyEncodedCommand = $legacyCommand.Replace('__PARAMETERS__', $legacyParameters).Replace('__FAILURE_PATH__', [Convert]::ToBase64String($utf8.GetBytes($legacyFailureFile)))
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($legacyEncodedCommand)))) { $legacyStart.ArgumentList.Add($argument) }
    $legacyProcess = [Diagnostics.Process]::Start($legacyStart)
    try {
        $legacyOutput = $legacyProcess.StandardOutput.ReadToEndAsync()
        $legacyError = $legacyProcess.StandardError.ReadToEndAsync()
        if (-not $legacyProcess.WaitForExit(90000)) { $legacyProcess.Kill(); [void]$legacyProcess.WaitForExit(5000); throw 'The isolated Windows PowerShell recovery check exceeded 90 seconds.' }
        $legacyText = $legacyOutput.GetAwaiter().GetResult()
        $legacyErrorText = $legacyError.GetAwaiter().GetResult()
        if ($legacyProcess.ExitCode -ne 0 -or $legacyText.Length -gt 65536 -or $legacyErrorText.Length -gt 65536) { throw "Windows PowerShell recovery check failed. Exit=$($legacyProcess.ExitCode); details=$legacyFailureFile; stderr=$($legacyErrorText.Substring(0, [Math]::Min(4096, $legacyErrorText.Length)))" }
    }
    finally { $legacyProcess.Dispose() }
    $legacyFile = Get-Item -LiteralPath $legacyPreviewFile -Force
    if ($legacyFile -isnot [IO.FileInfo] -or ($legacyFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $legacyFile.Length -le 0 -or $legacyFile.Length -gt 65536) { throw 'Windows PowerShell did not retain bounded regular recovery evidence.' }
    $legacyPreview = [IO.File]::ReadAllText($legacyPreviewFile, $utf8) | ConvertFrom-Json -Depth 25
    Check ($legacyPreview.fixture -eq $true -and $legacyPreview.major -eq 5 -and $legacyPreview.minor -eq 1 -and (Same-Path $legacyPreview.psHome (Split-Path -Parent $windowsPowerShell)) -and @($legacyPreview.modules).Count -eq 3 -and @($legacyPreview.modules | Where-Object { -not $_.path.StartsWith($nativeModules.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0 -and $legacyPreview.protectedBefore -eq $false -and $legacyPreview.protectedAfter -eq $true -and $legacyPreview.ownerBefore -ceq $applicationOwner -and $legacyPreview.ownerAfter -ceq $applicationOwner -and $legacyPreview.applied.Mode -ceq 'Applied' -and @($legacyPreview.applied.ChangedPaths).Count -eq 1 -and (Same-Path $legacyPreview.applied.ChangedPaths[0] $application) -and (Is-Private $legacyPreview.applied.BackupDirectory) -and $legacyPreview.preview.Mode -ceq 'Preview' -and $legacyPreview.preview.Version -ceq $baseline.Version -and (Same-Path $legacyPreview.preview.PortableRoot $portableRoot) -and @($legacyPreview.preview.ChangedPaths).Count -eq 0 -and @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force).Count -eq 2) 'the exact Windows PowerShell 5.1 executable uses native modules, repairs App inheritance without changing its owner and confirms an idempotent preview with its own private backup'
    $privateApplicationAcl = Get-Acl -LiteralPath $application
    $foreignApplicationAcl = Get-Acl -LiteralPath $application
    $foreignApplicationAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), [Security.AccessControl.FileSystemRights]::Modify, [Security.AccessControl.AccessControlType]::Allow))
    Set-Acl -LiteralPath $application -AclObject $foreignApplicationAcl
    $foreignPermissions = (Get-Acl -LiteralPath $application).Sddl
    $foreignRefused = $false
    try { $null = & $tool -PortableRoot $portableRoot -BackupRoot $backupRoot -Apply }
    catch { $foreignRefused = $_.Exception.Message.StartsWith('Another principal has access to ', [StringComparison]::Ordinal) }
    Check ($foreignRefused -and (Get-Acl -LiteralPath $application).Sddl -ceq $foreignPermissions -and @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force).Count -eq 2) 'the recovery command refuses an App folder writable by another principal before changing permissions or creating another backup'
    Set-Acl -LiteralPath $application -AclObject $privateApplicationAcl
    foreach ($file in $critical.Keys) {
        if ((Hash $file) -cne $critical[$file].sha256 -or (Get-Acl -LiteralPath $file).GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $critical[$file].owner) { throw 'Recovery modified a critical portable file or its owner.' }
    }
    $observedData = @((Get-Item -LiteralPath $dataRoot -Force)) + @(Get-ChildItem -LiteralPath $dataRoot -Force -Recurse)
    if ($observedData.Count -ne $dataPermissions.Count) { throw 'Recovery changed the set of portable Data entries.' }
    foreach ($item in $observedData) {
        if (-not $dataPermissions.ContainsKey($item.FullName) -or (Get-Acl -LiteralPath $item.FullName).Sddl -cne $dataPermissions[$item.FullName] -or ($item -is [IO.FileInfo] -and (Hash $item.FullName) -cne $dataHashes[$item.FullName])) { throw 'Recovery modified a portable Data entry, its contents or permissions.' }
    }
    Check ((Get-Acl -LiteralPath $application).Sddl -ceq $privateApplicationAcl.Sddl -and (Get-Acl -LiteralPath $portableRoot).Sddl -ceq $rootPermissions -and (Get-Acl -LiteralPath $parent).Sddl -ceq $parentAcl -and (Hash $scriptFile) -ceq $scriptHash -and -not $inert.HasExited -and (No-Registration)) 'recovery preserves all Data entries and permissions, critical bytes and owners, root and shared-parent ACLs, the external script and the unrelated process'
}
function Save-OperationEvidence([string]$Phase, [string]$Operation) {
    $destination = Join-Path $OutputRoot $Phase
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Operation -File -Force) {
        if ($file.Name -match '\A(?:plan|ready|commit|cancel|outcome|helper-refusal)\.json\z|\Ahelper\.log\z' -and $file.Length -le 8MB) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) }
    }
}
function Read-BackendSessionToken([int]$ManagerPid) {
    # Compare the owned child's nonce in memory only; it must never enter test evidence.
    $image = Join-Path $portableRoot 'App/backend/AhkManager.Backend.exe'
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ManagerPid AND Name='AhkManager.Backend.exe'" | Where-Object { $_.ExecutablePath -and (Same-Path $_.ExecutablePath $image) })
    if ($children.Count -ne 1 -or -not $children[0].CommandLine -or $children[0].CommandLine.Length -gt 32768) { return $null }
    $match = [regex]::Match($children[0].CommandLine, '(?:\A|\s)--session\s+"?([0-9a-f]{64})"?(?=\s|\z)')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}
function Save-SharingProbeEvidence([int]$ManagerPid) {
    Write-Json 'shortcut-sharing-probe.json' ([ordered]@{ fixture = $true; managerPid = $ManagerPid; diagnosticPid = $shortcutSharingProbe.ObservedPid; observed = $shortcutSharingProbe.Observed; released = $shortcutSharingProbe.Released; code = $shortcutSharingProbe.ObservedCode; attempt = $shortcutSharingProbe.ObservedAttempt; failure = $shortcutSharingProbe.Failure })
}
function Save-CommandEvidence([int]$ManagerPid, [AllowNull()][string]$BackendSessionToken) {
    $directory = Join-Path $dataRoot 'Logs'
    Check ((Test-Path -LiteralPath $directory -PathType Container) -and (Is-Private $directory)) 'the real application automatically creates private diagnostic logs beside its profile'
    $files = @(Get-ChildItem -LiteralPath $directory -File -Force)
    Check ($files.Count -gt 0 -and $files.Count -le 5 -and ($files | Measure-Object Length -Sum).Sum -le 10MB) 'the actual command log history stays within its five-file and ten-MiB retention limits'
    $destination = Join-Path $OutputRoot 'command-logs'
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $commands = [Collections.Generic.List[object]]::new()
    $copies = [Collections.Generic.List[object]]::new()
    $sessions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $allowed = @('schemaVersion', 'kind', 'sessionId', 'timestamp', 'source', 'command', 'operation', 'scriptId', 'version', 'channel', 'runAtLogin', 'runAsAdministrator', 'launchManagerAtLogin', 'launchManagerAsAdministrator', 'startInTray', 'requestId', 'parentRequestId', 'phase', 'durationMs', 'result', 'outcome', 'code', 'state', 'addedCount', 'duplicateCount', 'issueCount', 'itemCount', 'correlationId', 'errorName', 'errorCode', 'backendRequestId', 'attempt', 'retrying')
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    foreach ($file in $files) {
        $match = [regex]::Match($file.Name, '\Acommands-(\d{8}T\d{9}Z)-([0-9a-f-]{36})-(\d{6})\.jsonl\z')
        if (-not $match.Success -or $file.Length -le 0 -or $file.Length -gt 2MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not (Is-Private $file.FullName)) { throw 'A command log is not a private bounded regular file with the application-owned name.' }
        $before = Hash $file.FullName
        $text = [IO.File]::ReadAllText($file.FullName, $strictUtf8)
        if (-not $text.EndsWith("`n", [StringComparison]::Ordinal)) { throw 'The normally closed application left a partial command log line.' }
        if ($BackendSessionToken -and $text.Contains($BackendSessionToken, [StringComparison]::Ordinal)) { throw 'An authenticated backend session token leaked into command diagnostics.' }
        $lines = $text.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)
        $header = ConvertFrom-Json -InputObject $lines[0] -AsHashtable
        $timestamp = if ($header['timestamp'] -is [DateTime] -or $header['timestamp'] -is [DateTimeOffset]) { $header['timestamp'].ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture) } else { ([string]$header['timestamp']).Replace('-', '').Replace(':', '').Replace('.', '') }
        if ($header['schemaVersion'] -ne 1 -or $header['kind'] -cne 'session' -or $header['applicationId'] -cne 'Bezoro.AhkManager.Electron' -or $header['sessionId'] -cne $match.Groups[2].Value -or $header['fileIndex'] -ne [int]$match.Groups[3].Value -or $timestamp -cne $match.Groups[1].Value -or @($header.Keys | Where-Object { $_ -notin @('schemaVersion', 'kind', 'applicationId', 'sessionId', 'timestamp', 'pid', 'fileIndex') }).Count -gt 0) { throw 'The command log header does not bind its exact application, session and file index.' }
        [void]$sessions.Add([string]$header['sessionId'])
        for ($index = 1; $index -lt $lines.Count; $index++) {
            if ($strictUtf8.GetByteCount($lines[$index]) -gt 2048) { throw 'A command diagnostic record exceeds its bounded line length.' }
            $record = ConvertFrom-Json -InputObject $lines[$index] -AsHashtable
            if ($record -isnot [Collections.IDictionary] -or $record['schemaVersion'] -ne 1 -or $record['sessionId'] -cne $header['sessionId'] -or $record['kind'] -notin @('command', 'session-end') -or @($record.Keys | Where-Object { $_ -notin $allowed }).Count -gt 0) { throw 'A command log contains an invalid schema or an unapproved payload field.' }
            if ($record['kind'] -ceq 'command') {
                $requestId = [Guid]::Empty
                if ($record['source'] -notin @('ui', 'backend', 'tray', 'lifecycle', 'updates', 'storage') -or $record['command'] -cnotmatch '\A[a-zA-Z][a-zA-Z0-9:._-]{0,63}\z' -or $record['phase'] -notin @('started', 'completed', 'error') -or -not [Guid]::TryParse([string]$record['requestId'], [ref]$requestId)) { throw 'A command record does not identify its source, action, phase and correlation ID.' }
                if ($header['pid'] -eq $ManagerPid) { $commands.Add($record) }
            }
        }
        [IO.File]::Copy($file.FullName, (Join-Path $destination $file.Name), $false)
        if ((Hash $file.FullName) -cne $before -or (Hash (Join-Path $destination $file.Name)) -cne $before) { throw 'A command log changed while its closed-session evidence was copied.' }
        $copies.Add([ordered]@{ file = $file.Name; bytes = $file.Length; sha256 = $before; sessionId = $header['sessionId']; pid = $header['pid'] })
    }
    Check ($commands.Count -gt 0) 'the retained command log session belongs to the exact real application process'
    foreach ($command in @('initializeProfile', 'load', 'shutdown')) {
        $finished = @($commands | Where-Object { $_['source'] -ceq 'backend' -and $_['command'] -ceq $command -and $_['phase'] -ceq 'completed' -and $_['result'] -in @('confirmed', 'completed') })
        Check ($finished.Count -eq 1) "the real backend $command command records one successful completion"
    }
    $updates = @($commands | Where-Object { $_['source'] -ceq 'updates' -and $_['command'] -ceq 'check' -and $_['phase'] -ceq 'completed' })
    Check ($updates.Count -gt 0 -and @($updates | Where-Object { $_['state'] -notin @('idle', 'error') -or ($_['state'] -ceq 'error' -and $_['result'] -cne 'failed') }).Count -eq 0) 'automatic update checks record their settled success or failure without treating a resolved error as success'
    $finishedCommands = @($commands | Where-Object { $_['phase'] -in @('completed', 'error') })
    foreach ($finished in $finishedCommands) {
        $started = @($commands | Where-Object { $_['requestId'] -ceq $finished['requestId'] -and $_['phase'] -ceq 'started' -and $_['source'] -ceq $finished['source'] -and $_['command'] -ceq $finished['command'] })
        if ($started.Count -ne 1 -or ($finished['durationMs'] -isnot [long] -and $finished['durationMs'] -isnot [int]) -or $finished['durationMs'] -lt 0) { throw 'A completed command is missing its matching start or a valid duration.' }
    }
    Check ($finishedCommands.Count -gt 0) 'actual command completions retain their matching start IDs and nonnegative durations'
    if ($Scenario -eq 'default') {
        $sharing = @($commands | Where-Object { $_['command'] -ceq 'repairSearchShortcut.rename' -and $_['phase'] -ceq 'error' -and $_['retrying'] -eq $true -and $_['attempt'] -ge 1 -and $_['errorCode'] -in @('EACCES', 'EPERM', 'EBUSY') })
        Check ($sharing.Count -gt 0) 'the real relocated app records the observed sharing violation and bounded retry under its exact process session'
    }
    Write-Json 'command-logs.json' ([ordered]@{ fixture = $true; actualApplication = $true; directory = $directory; managerPid = $ManagerPid; retainedFiles = $files.Count; sessions = $sessions.Count; currentProcessCommands = $commands.Count; completedCommands = $finishedCommands.Count; backendSessionTokenObserved = -not [string]::IsNullOrEmpty($BackendSessionToken); backendSessionTokenOmitted = $true; approvedFieldsOnly = $true; files = $copies.ToArray() })
}
function Seed-Package($Release, [string]$Archive, [string]$Manifest, [string]$Signature) {
    $directory = Join-Path $updatesRoot (Join-Path 'installers' $Release.Version)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::Copy($Archive, (Join-Path $directory 'archive.zip'), $false)
    [IO.File]::Copy($Manifest, (Join-Path $directory 'release.json'), $false)
    if ($Signature) {
        $signatureFile = Get-Item -LiteralPath $Signature
        if ($signatureFile -isnot [IO.FileInfo] -or $signatureFile.Length -le 0 -or $signatureFile.Length -gt 65536) { throw 'The isolated portable manifest signature is absent or oversized.' }
        [IO.File]::Copy($Signature, (Join-Path $directory 'release.json.p7s'), $false)
    }
    $receipt = [ordered]@{ schemaVersion = 1; updateProtocol = 2; version = $Release.Version; sha256 = $Release.Sha256; bytes = $Release.ArchiveBytes }
    [IO.File]::WriteAllText((Join-Path $directory 'receipt.json'), ($receipt | ConvertTo-Json -Compress), $utf8)
}
function Wait-Ready($Current, [string]$ExpectedVersion, [switch]$PublishedRollback) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $state = Invoke-PortableEvaluation $Current 'window.ahkManager ? window.ahkManager.getState() : null'
        if ($state -and $state.connection -eq 'ready' -and -not $state.busy) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json ("host-$ExpectedVersion.json") $state
    Check ($state -and $state.connection -eq 'ready' -and $state.authorityMode -eq 'Administrator' -and -not $state.snapshot.readOnly) 'the portable application has its real administrative backend and writable profile'
    Check ($state.snapshot.scripts.Count -eq 1 -and $state.snapshot.scripts[0].id -eq $scriptId -and $state.snapshot.scripts[0].path -eq $scriptFile -and $state.snapshot.scripts[0].runAtLogin -eq $false -and $state.snapshot.scripts[0].runAsAdministrator -eq $false) 'the imported external script identity, disabled automatic execution and standard privileges are preserved'
    $expectedAdministrator = $true
    if ($PublishedRollback -or ($PublishedBaseline -and $ExpectedVersion -ceq $baseline.Version)) {
        $persisted = Get-Content -LiteralPath (Join-Path $dataRoot 'Profile/settings.json') -Raw | ConvertFrom-Json
        $expectedAdministrator = $persisted.Settings.LaunchManagerAsAdministrator
        if ($expectedAdministrator -isnot [bool]) { throw 'The published baseline has no durable Manager privilege preference.' }
        if ($PublishedRollback) { Check ($expectedAdministrator -eq $true) 'the published rollback preserves the candidate-migrated Manager privilege preference on disk' }
    }
    Check ($state.snapshot.settings.launchManagerAsAdministrator -eq $expectedAdministrator -and $state.snapshot.settings.launchManagerAtLogin -eq $false) 'the active version preserves Windows startup opt-out and its compatible Manager privilege policy'
    Check ($state.snapshot.settings.editorArguments.Count -eq 1 -and $state.snapshot.settings.editorArguments[0] -ceq 'portable-ci-preservation' -and $null -eq $state.snapshot.settings.interpreterPath -and $null -eq $state.snapshot.settings.editorPath -and $state.snapshot.settings.showWindowAtLogin -eq $true -and $state.snapshot.settings.startInTray -eq $false -and $state.snapshot.settings.theme -ceq 'Dark' -and $state.snapshot.settings.closeBehavior -ceq 'Exit') 'all unrelated imported user preferences survive the Manager-only administrator migration'
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
    $null = [AhkManager.TestSupport.PortableUpdaterUi]::Focus($session.Process.Id, $session.Image, $session.Started)
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
    foreach ($subdirectory in @('Profile', 'Updates', 'Logs')) {
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
function Read-PinnedPortableTarget {
    $api = 'https://api.github.com/repos/bezoro/ahk-manager/releases'
    $sha = 'bee6fedaf2420bb5e63c34a83063b08b9f8f8b4f2562f0b5cacdafc7c732ed5c'
    $metadata = Invoke-RestMethod -Uri "$api/tags/v1.6.2" -TimeoutSec 20 -MaximumRedirection 0
    $archive = @($metadata.assets | Where-Object name -CEQ 'AHK-Manager-1.6.2-Portable-x64.zip')
    $manifest = @($metadata.assets | Where-Object name -CEQ 'ahk-manager-portable-release.json')
    Check ($metadata.tag_name -ceq 'v1.6.2' -and -not $metadata.draft -and -not $metadata.prerelease -and
        $archive.Count -eq 1 -and $archive[0].id -eq 705660061 -and $archive[0].size -eq 234252955 -and $archive[0].digest -ceq "sha256:$sha" -and
        $manifest.Count -eq 1 -and $manifest[0].id -eq 705660452 -and $manifest[0].size -eq 873 -and
        -not @($metadata.assets | Where-Object name -IEQ 'ahk-manager-portable-release.json.p7s').Count) 'published stable metadata binds the exact unsigned portable fixture assets'
    $path = Join-Path $OutputRoot 'cancel-target-manifest.json'
    Invoke-WebRequest -Uri "$api/assets/705660452" -Headers @{ Accept = 'application/octet-stream'; 'X-GitHub-Api-Version' = '2022-11-28' } -TimeoutSec 20 -MaximumRedirection 3 -OutFile $path
    Check ((Get-Item -LiteralPath $path).Length -eq 873 -and (Hash $path) -ceq '24c4b58edb4e49413f1e2bcad24c5ad2e48d7a1cb7b927d72937138164044fa0') 'the exact public manifest matches the pinned bytes'
    $target = ConvertFrom-PortableReleaseJson ([IO.File]::ReadAllText($path, $utf8))
    Check ($target.Version -ceq '1.6.2' -and $target.ArchiveName -ceq $archive[0].name -and $target.ArchiveBytes -eq $archive[0].size -and $target.Sha256 -ceq $sha) 'the portable manifest and GitHub asset metadata agree'
    Write-Json 'cancel-target-identity.json' @{ assetId = $archive[0].id; manifestId = $manifest[0].id; release = $target }
    return $target
}

function Invoke-PortableButton([string]$Label, [string]$Version = '') {
    $labelJson = ConvertTo-Json -InputObject $Label -Compress
    $versionJson = ConvertTo-Json -InputObject $Version -Compress
    return Invoke-PortableEvaluation $session @"
(() => {
  const version = $versionJson;
  const scope = version ? [...document.querySelectorAll('.update-release')].find(row => row.querySelector('h3')?.textContent.trim() === version) : document;
  const button = [...(scope?.querySelectorAll('button') ?? [])].find(item => item.textContent.trim() === $labelJson);
  if (!button || button.disabled || !button.getClientRects().length) return false;
  button.click();
  return true;
})()
"@
}

function Save-PortableScreenshot([string]$Name) {
    $capture = Invoke-PortableCdp $session 'Page.captureScreenshot' @{ format = 'png'; fromSurface = $true }
    [IO.File]::WriteAllBytes((Join-Path $OutputRoot $Name), [Convert]::FromBase64String($capture.data))
}

function Confirm-CanceledDownload($Target, $Choice, [string]$Action) {
    $cache = Join-Path $updatesRoot "installers/$($Target.Version)"
    $downloads = Join-Path $updatesRoot 'downloads'
    $candidateCache = Join-Path $updatesRoot "installers/$currentVersion"
    $images = @('Bezoro.AhkManager.exe', 'App/Bezoro.AhkManager.exe', 'App/resources/app.asar')
    $imageHashes = @{}
    foreach ($image in $images) { $imageHashes[$image] = Hash (Join-Path $portableRoot $image) }
    Check (-not (Test-Path -LiteralPath $cache) -and -not (Test-Path -LiteralPath $downloads)) 'the published target has no pre-existing cache or staging'
    Check (@($downloadProxy.Tunnels | Where-Object { $_.Host -eq 'api.github.com' -and $_.DownstreamBytes -gt 0 }).Count -gt 0) 'the exact packaged updater completed release discovery through its loopback tunnel'
    $downloadProxy.Hold = $true
    Check (Invoke-PortableButton $Action $Target.Version) 'the real Updates history action starts the published target download'
    $confirmed = $false
    $held = @()
    $partial = @()
    $state = $null
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($session.Process.HasExited) { break }
        if (-not $confirmed) { $confirmed = [AhkManager.TestSupport.PortableUpdaterUi]::Confirm($session.Process.Id, $session.Image, $session.Started, $Target.Version) }
        $state = Invoke-PortableEvaluation $session 'window.ahkManager.getUpdateState()'
        $held = @($downloadProxy.Tunnels | Where-Object { $_.HeldAtUtc -and -not $_.ClientClosedAtUtc })
        $partial = @(Get-ChildItem -LiteralPath $downloads -Directory -Filter 'portable-*' -ErrorAction SilentlyContinue | ForEach-Object { Get-Item -LiteralPath (Join-Path $_.FullName 'archive.zip') -ErrorAction SilentlyContinue })
        if ($confirmed -and $held.Count -eq 1 -and $partial.Count -eq 1 -and $partial[0].Length -gt 0 -and $state.phase -ceq 'downloading' -and $state.progress -gt 0 -and $state.downloadId) { break }
        if ($state.phase -ceq 'error') { break }
        Start-Sleep -Milliseconds 100
    } while ($deadline.Elapsed.TotalSeconds -lt 60)
    Write-Json 'cancel-held.json' @{ consent = $confirmed; state = $state; tunnels = $downloadProxy.Tunnels; partial = @($partial | Select-Object FullName, Length) }
    Check ($confirmed -and $held.Count -eq 1 -and $partial.Count -eq 1 -and $partial[0].Length -gt 0 -and $partial[0].Length -lt $Target.ArchiveBytes -and $state.phase -ceq 'downloading' -and $state.progress -gt 0 -and $state.downloadId) 'native consent leads to one held TLS tunnel, a real partial ZIP and positive progress'
    Save-PortableScreenshot 'cancel-in-progress.png'
    $staging = $partial[0].DirectoryName
    $tunnel = $held[0]
    $clickedAt = [DateTimeOffset]::UtcNow
    $deadline.Restart()
    Check (Invoke-PortableButton 'Cancel download') 'the visible portable cancellation button is clicked'
    do {
        $state = Invoke-PortableEvaluation $session 'window.ahkManager.getUpdateState()'
        $terminal = Invoke-PortableEvaluation $session 'document.querySelector(".updates-page")?.innerText.includes("Download canceled.") === true'
        $discarded = -not (Test-Path -LiteralPath $staging)
        # The DOM can advance after this iteration sampled the main process.
        $cancellationComplete = $terminal -and $discarded -and $tunnel.ClientClosedAtUtc -and [DateTimeOffset]$tunnel.ClientClosedAtUtc -ge $clickedAt -and
            $state.phase -ceq 'idle' -and $state.downloadCancellation -ceq 'canceled' -and $null -eq $state.progress -and $null -eq $state.downloadId -and
            @($state.releases | Where-Object { $_.id -ceq $Choice.id -and $_.canInstall -and -not $_.cached }).Count -eq 1
        if ($cancellationComplete) { break }
        Start-Sleep -Milliseconds 50
    } while ($deadline.Elapsed.TotalSeconds -lt 10)
    Write-Json 'cancel-terminal.json' @{ clickedAtUtc = $clickedAt.ToString('O'); elapsedMilliseconds = $deadline.ElapsedMilliseconds; terminal = $terminal; discarded = $discarded; state = $state; tunnel = $tunnel }
    Check ($deadline.Elapsed.TotalSeconds -le 10 -and $cancellationComplete) 'browser closure, exact staging removal and reusable canceled state are observed within ten seconds'
    Save-PortableScreenshot 'cancel-complete.png'
    Check (-not (Test-Path -LiteralPath $cache) -and -not @(Get-ChildItem -LiteralPath (Join-Path $updatesRoot 'operations') -Directory -ErrorAction SilentlyContinue).Count) 'cancellation publishes no target cache or native operation'
    Wait-Ready $session $currentVersion
    foreach ($image in $images) { Check ((Hash (Join-Path $portableRoot $image)) -ceq $imageHashes[$image]) "cancellation preserves $image" }
    Check ((Hash (Join-Path $candidateCache 'archive.zip')) -ceq $candidate.Sha256 -and (Hash (Join-Path $candidateCache 'release.json')) -ceq $candidate.ManifestSha256 -and
        (Hash $stateSentinel) -ceq $stateHash -and (Hash $scriptFile) -ceq $scriptHash -and (Hash (Join-Path $portableRoot 'portable.json')) -ceq $markerHash -and
        $state.channel -ceq 'stable') 'cancellation preserves the verified rollback package, profile, script, update channel and portable configuration'
    $downloadProxy.Hold = $false
    return $tunnel.Id
}

function Run-Update([string]$Phase, $Target, [switch]$RemoteCancel) {
    $phaseRoot = Join-Path $OutputRoot $Phase
    [IO.Directory]::CreateDirectory($phaseRoot) | Out-Null
    $proxyPort = if ($RemoteCancel) { $downloadProxy.Port } else { 0 }
    $script:session = Start-PortableSession -Root $portableRoot -EvidenceRoot $phaseRoot -ProxyPort $proxyPort
    Wait-Ready $session $currentVersion
    if ($RemoteCancel) { Check (Invoke-PortableButton 'Updates') 'the real portable Updates screen opens' }
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $update = Invoke-PortableEvaluation $session 'window.ahkManager.getUpdateState()'
        $choice = @($update.releases | Where-Object { $_.version -ceq $Target.Version -and $_.cached -eq (-not $RemoteCancel) -and $_.canInstall }) | Select-Object -First 1
        if ($choice -and $update.phase -notin @('checking', 'downloading', 'installing')) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json ("$Phase-before.json") $update
    Check ($null -ne $choice -and (-not $RemoteCancel -or $update.phase -ceq 'idle')) 'the actual portable updater exposes the exact target in the expected cache state'
    Invoke-PortableCdp $session 'Page.bringToFront' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $null = [AhkManager.TestSupport.PortableUpdaterUi]::Focus($session.Process.Id, $session.Image, $session.Started)
        $ready = Invoke-PortableEvaluation $session '({ visible: document.visibilityState === "visible", focused: document.hasFocus(), mounted: !!document.querySelector("main"), dialog: !!document.querySelector("[role=dialog]") })'
        if ($ready.visible -and $ready.focused -and $ready.mounted -and -not $ready.dialog) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check ($ready.visible -and $ready.focused -and $ready.mounted -and -not $ready.dialog) 'the real portable renderer is visible, focused and ready for update consent'
    Invoke-PortableEvaluation $session 'new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))' | Out-Null
    $before = @(Get-ChildItem -LiteralPath (Join-Path $updatesRoot 'operations') -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
    if ($RemoteCancel) {
        $action = 'Return to this version'
        $heldId = Confirm-CanceledDownload $Target $choice $action
        $retryBaseline = @{}
        foreach ($tunnel in $downloadProxy.Tunnels) { $retryBaseline[$tunnel.Id] = $tunnel.DownstreamBytes }
        Check (Invoke-PortableButton $action $Target.Version) 'the same real history action starts a fresh retry'
    } else {
        $id = ConvertTo-Json -InputObject $choice.id -Compress
        Invoke-PortableEvaluation $session ("globalThis.__portableUpdate = { pending: true }; void window.ahkManager.installUpdate($id).then(value => globalThis.__portableUpdate = {pending:false,value}, error => globalThis.__portableUpdate = {pending:false,error:String(error)}); true") | Out-Null
    }
    $script:helperInFlight = $true
    $confirmed = $false
    $result = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($session.Process.HasExited) { break }
        if (-not $confirmed -and [AhkManager.TestSupport.PortableUpdaterUi]::Confirm($session.Process.Id, $session.Image, $session.Started, $Target.Version)) { $confirmed = $true; Trace "$Phase native consent confirmed for $($Target.Version)." }
        if ($RemoteCancel) {
            # The native handoff deliberately blocks IPC before the window closes.
            # Observe this exact process instead; receipts below must still prove
            # one authenticated transaction and the complete verified retry.
            if ($confirmed -and $session.Process.WaitForExit(100)) { break }
        } else {
            try { $result = Invoke-PortableEvaluation $session 'globalThis.__portableUpdate' }
            catch { if ($confirmed -and $session.Socket.State -ne [Net.WebSockets.WebSocketState]::Open -and $session.Process.WaitForExit(30000)) { break }; throw }
            if ($result -and -not $result.pending) { break }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Check $confirmed 'the actual portable update received exact-version native consent'
    if ($RemoteCancel) {
        $result = [ordered]@{
            observation = 'native-consent-and-process-exit'
            nativeConsentConfirmed = $confirmed
            ipcPolledAfterRetry = $false
            processExitedAtObservation = $session.Process.HasExited
        }
    }
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
    if ($RemoteCancel) {
        $retryTunnels = @($downloadProxy.Tunnels | Where-Object { $_.Id -ne $heldId -and $_.Host -ne 'api.github.com' -and ($_.DownstreamBytes - [long]$retryBaseline[$_.Id]) -ge $Target.ArchiveBytes })
        Check ($retryTunnels.Count -eq 1 -and -not $retryTunnels[0].HeldAtUtc) 'one distinct unthrottled TLS tunnel carries the full retried package'
        $cache = Join-Path $updatesRoot "installers/$($Target.Version)"
        $archive = Join-Path $cache 'archive.zip'
        $receipt = Get-Content -LiteralPath (Join-Path $cache 'receipt.json') -Raw | ConvertFrom-Json
        Write-Json 'cancel-retry-receipt.json' $receipt
        Check ($receipt.schemaVersion -eq 1 -and $receipt.updateProtocol -eq 2 -and $receipt.version -ceq $Target.Version -and $receipt.bytes -eq $Target.ArchiveBytes -and $receipt.sha256 -ceq $Target.Sha256 -and
            (Hash $archive) -ceq $Target.Sha256 -and
            [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash)) -ceq $Target.Sha512 -and
            (Hash (Join-Path $cache 'release.json')) -ceq (Hash (Join-Path $OutputRoot 'cancel-target-manifest.json'))) 'the retried cache, receipt, exact manifest and both archive digests bind the package sent to native apply'
    }
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
    foreach ($subdirectory in @('Profile', 'Logs')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $dataRoot $subdirectory) -File -Recurse -Force) { $sourceFiles[[IO.Path]::GetRelativePath($originalRoot, $file.FullName)] = Hash $file.FullName }
    }
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
        Check ($verified.Version -ceq $release.Version -and $verified.Commit -ceq $release.Commit -and $verified.Sha256 -ceq $release.Sha256 -and $verified.ArchiveBytes -eq $release.ArchiveBytes -and (Hash $archive) -ceq $release.Sha256 -and (Get-Item -LiteralPath $archive).Length -eq $release.ArchiveBytes -and (Hash $manifest) -ceq $release.ManifestSha256 -and (Hash (Join-Path $directory 'release.json.p7s')) -ceq (Hash $signature) -and $receipt.schemaVersion -eq 1 -and $receipt.updateProtocol -eq 2 -and $receipt.version -ceq $release.Version -and $receipt.sha256 -ceq $release.Sha256 -and $receipt.bytes -eq $release.ArchiveBytes) 'the moved version history retains each exact verified package, manifest, cache receipt and authenticated signature'
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
    $baselineCommit = if ($PublishedBaseline) { '343553fd108220562883e1aa65c356d6cdcf7ee3' } else { $SourceCommit }
    if ($PublishedBaseline -and $Scenario -cne 'default') { throw 'The pinned published baseline belongs only to the default migration scenario.' }
    $baseline = Read-PortableReleaseManifest -ArchivePath $BaselineArchive -ManifestPath $BaselineManifest -ExpectedCommit $baselineCommit
    if ($PublishedBaseline -and ($baseline.Version -cne '1.6.2' -or $baseline.ArchiveBytes -ne 234252955L -or $baseline.Sha256 -cne 'bee6fedaf2420bb5e63c34a83063b08b9f8f8b4f2562f0b5cacdafc7c732ed5c' -or $baseline.ManifestSha256 -cne '24c4b58edb4e49413f1e2bcad24c5ad2e48d7a1cb7b927d72937138164044fa0')) { throw 'The deployed baseline is not the pinned published portable package.' }
    $candidate = Read-PortableReleaseManifest -ArchivePath $CandidateArchive -ManifestPath $CandidateManifest -ExpectedCommit $SourceCommit
    Check ($baseline.Version -cne $candidate.Version) 'both exact portable package source identities and distinct versions are verified'
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $outputCreated = $true
    $parent = Join-Path $OutputRoot 'shared-parent'
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $acl = Get-Acl -LiteralPath $parent
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), [Security.AccessControl.FileSystemRights]::Modify, [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    Set-Acl -LiteralPath $parent -AclObject $acl
    $parentAcl = (Get-Acl -LiteralPath $parent).Sddl
    $portableRoot = Join-Path $parent 'Portable Manager'
    $clientArchive = if ($Scenario -eq 'download-cancel') { $CandidateArchive } else { $BaselineArchive }
    [IO.Compression.ZipFile]::ExtractToDirectory($clientArchive, $portableRoot)
    $originalBaselineFiles = @{}
    if ($Scenario -ne 'download-cancel') {
        foreach ($relative in @('Bezoro.AhkManager.exe', 'portable-package.json', 'App/Bezoro.AhkManager.exe', 'App/backend/AhkManager.Backend.exe', 'App/backend/update/AhkManager.UpdateHost.exe', 'App/resources/app.asar', 'App/resources/app-update.yml')) {
            $originalBaselineFiles[$relative] = Hash (Join-Path $portableRoot $relative)
        }
    }
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
    Check ($policy.schemaVersion -eq 2 -and $policy.sourceCommit -ceq $SourceCommit -and $policy.baselineSourceCommit -ceq $baselineCommit -and $policy.candidatePolicySha256 -ceq $policyHash) 'the exact candidate archive publisher policy matches the source-bound production parser metadata'
    if ($Scenario -eq 'download-cancel') {
        Check ($policy.names -is [array] -and $policy.names.Count -eq 0 -and
            (Hash (Join-Path $portableRoot 'App/resources/app-update.yml')) -ceq $policyHash) 'the exact extracted candidate uses the empty packaged publisher policy'
        $target = Read-PinnedPortableTarget
        $clientVersion = $candidate.Version
        Check ([version]($clientVersion.Split('-')[0]) -gt [version]$target.Version) 'the candidate has a distinct version newer than the pinned history target'
        Write-Json 'cancel-client-identity.json' @{ sourceCommit = $SourceCommit; version = $clientVersion; archiveSha256 = $candidate.Sha256; manifestSha256 = $candidate.ManifestSha256; publisherPolicySha256 = $policyHash; publisherNames = $policy.names }
        Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableDownloadProxy.cs')
        $downloadProxy = [AhkManager.TestSupport.PortableDownloadProxy]::new()
        Write-Json 'cancel-fixture-flags.json' @{ flags = @("--proxy-server=http://127.0.0.1:$($downloadProxy.Port)", '--disable-http2'); holdAfterBytes = [AhkManager.TestSupport.PortableDownloadProxy]::HoldAfterBytes }
    } else {
        $clientVersion = $baseline.Version
        $signingFixture = New-PortableSigningFixture -ApplicationRoot (Join-Path $portableRoot 'App') -CandidateManifestPath $CandidateManifest -BaselineManifestPath $BaselineManifest -PublisherPolicyPath $publisherPolicyPath -OutputRoot (Join-Path $OutputRoot 'signing-fixture')
        foreach ($relative in $originalBaselineFiles.Keys) { if ((Hash (Join-Path $portableRoot $relative)) -cne $originalBaselineFiles[$relative]) { throw 'Signing fixture preparation modified a baseline binary or configuration file.' } }
        Write-Json 'baseline-payload-proof.json' ([ordered]@{ fixture = $true; published = [bool]$PublishedBaseline; version = $baseline.Version; commit = $baseline.Commit; archiveSha256 = $baseline.Sha256; manifestSha256 = $baseline.ManifestSha256; payloadFiles = $originalBaselineFiles; payloadModified = $false; administrativeTestHost = $true })
        Check ($true) 'the baseline launcher, application, backend, helper, archive inventory and publisher policy remain byte-identical before launch'
        $signingFixtureCleanup = 'Pending'
        Write-Json 'portable-signing-proof.json' $signingFixture.Proof
        Check ($null -ne $signingFixture -and (Test-Path -LiteralPath $signingFixture.CandidateSignaturePath -PathType Leaf) -and (Test-Path -LiteralPath $signingFixture.BaselineSignaturePath -PathType Leaf)) 'both exact portable manifests have authenticated fixture signatures before any cache entry is seeded'
    }
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
        $startupReports = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Bezoro/AhkManagerElectronDiagnostics/Startup'
        $existingStartupReports = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        if (Test-Path -LiteralPath $startupReports) {
            $directory = Get-Item -LiteralPath $startupReports -Force
            if ($directory -isnot [IO.DirectoryInfo] -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The isolated startup diagnostic namespace is not a regular directory.' }
            foreach ($file in @(Get-ChildItem -LiteralPath $startupReports -File -Filter 'startup-*.json')) { [void]$existingStartupReports.Add($file.Name) }
        }
        $session = Start-PortableSession -Root $portableRoot -EvidenceRoot $OutputRoot -ViaLauncher -ExpectStartupRefusal
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $dismissed = $false
        do {
            if ([AhkManager.TestSupport.PortableStartupUi]::DismissRefusal($session.Process.Id, $session.Image, $session.Started, $readStartupDialog)) { $dismissed = $true; break }
            Start-Sleep -Milliseconds 100
        } while (-not $session.Process.HasExited -and [DateTime]::UtcNow -lt $deadline)
        Write-Json 'foreign-root-windows.json' ([AhkManager.TestSupport.PortableUpdaterUi]::Describe($session.Process.Id, $session.Image, $session.Started))
        Write-Json 'foreign-root-observation.json' ([ordered]@{ fixture = $true; dismissed = $dismissed; processExited = $session.Process.HasExited; observation = [AhkManager.TestSupport.PortableStartupUi]::LastObservation })
        Check ($dismissed -and $session.Process.WaitForExit(30000) -and $session.Process.ExitCode -eq 65) 'the actual portable app refuses an unknown root file and exits after its exact owned error dialog closes'
        Check (Test-Path -LiteralPath $startupReports -PathType Container) 'the actual refused portable launch preserves diagnostics in the independent per-user namespace'
        $newReports = @(Get-ChildItem -LiteralPath $startupReports -File -Filter 'startup-*.json' | Where-Object { -not $existingStartupReports.Contains($_.Name) })
        Check ($newReports.Count -eq 1) 'the exact failed application session creates one new startup report before exiting'
        $startupFile = $newReports[0]
        Check (($startupFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and $startupFile.Length -gt 0 -and $startupFile.Length -le 65536 -and -not (Same-Path $startupReports $portableRoot) -and -not ([IO.Path]::GetFullPath($startupReports)).StartsWith([IO.Path]::GetFullPath($portableRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'startup evidence is bounded and stored outside the refused portable container'
        $startupHash = Hash $startupFile.FullName
        $startupReport = [IO.File]::ReadAllText($startupFile.FullName, $utf8) | ConvertFrom-Json -Depth 35
        Check ($startupReport.schemaVersion -eq 1 -and $startupReport.applicationId -ceq 'Bezoro.AhkManager.Electron' -and $startupReport.kind -ceq 'startup-failure' -and $startupReport.version -ceq $baseline.Version -and (Same-Path $startupReport.executablePath $session.Image) -and (Same-Path $startupReport.deployment.portableRoot $portableRoot) -and (Same-Path $startupReport.deployment.dataDirectory $dataRoot) -and $startupReport.error.code -ceq 'invalid-package' -and $startupReport.error.diagnostic.origin -ceq 'native' -and $startupReport.error.diagnostic.phase -ceq 'portable-initialization' -and $startupReport.error.diagnostic.stage -ceq 'verify-root-members' -and $startupReport.error.diagnostic.resource -ceq 'root' -and $startupReport.error.diagnostic.exitCode -eq 65 -and $startupReport.error.diagnostic.hresult -cmatch '^0x[0-9A-F]{8}$' -and $startupReport.error.diagnostic.userSid -ceq $sid -and $startupReport.error.diagnostic.administrator -eq $true -and (Hash $startupFile.FullName) -ceq $startupHash) 'startup evidence binds the failed native check, version, portable paths and actual Windows token without a backend'
        Write-Json 'startup-refusal-diagnostic.json' $startupReport
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
        Wait-Ready $session $clientVersion
        if ($PublishedBaseline) {
            $initial = Invoke-PortableEvaluation $session 'window.ahkManager.getState()'
            Check ($initial.snapshot.settings.launchManagerAsAdministrator -eq $false) 'the actual published baseline exposes its seeded legacy false Manager privilege preference before migration'
        }
        Check-SearchShortcut 'initial'
        Capture-Preferences
        Check ((Is-Private $portableRoot) -and (Is-Private (Join-Path $portableRoot 'App')) -and (Is-Private $dataRoot)) 'first real launch secures only the dedicated portable runtime and Data folders'
        Check ((Get-Acl -LiteralPath $parent).Sddl -ceq $parentAcl) 'first launch preserves the permissive shared parent permissions'
        $profile = Join-Path $dataRoot 'Profile'
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $profile 'migration-backup') -Filter settings.json -File -Recurse)
        Check ($backups.Count -eq 1 -and (Hash $backups[0].FullName) -ceq $legacyHashes[(Join-Path $legacyRoot 'settings.json')]) 'automatic portable import preserves an exact backup of the original settings'
        Close-PortableSession $session; $session = $null
        if ($Scenario -eq 'default') { Test-InheritedAccessRepair }
        $updatesRoot = Join-Path $dataRoot 'Updates'
        Check (-not (Test-Path -LiteralPath (Join-Path $updatesRoot 'installers/1.3.2/installer.exe'))) 'legacy NSIS packages are not imported into portable package history'
        $stateSentinel = Join-Path $profile 'ci-state-sentinel.txt'
        [IO.File]::WriteAllText($stateSentinel, 'Portable user state survives update and rollback.', $utf8)
        $stateHash = Hash $stateSentinel
        if ($Scenario -eq 'download-cancel') {
            Seed-Package $candidate $CandidateArchive $CandidateManifest
            [IO.File]::WriteAllText((Join-Path $updatesRoot 'update-channel.json'), '{"schemaVersion":1,"channel":"stable"}', $utf8)
            $currentVersion = $clientVersion
            $phaseStarted = [DateTime]::UtcNow
            Run-Update 'download-cancel' $target -RemoteCancel
            $finalVersion = $target.Version
        } else {
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
            $finalVersion = $baseline.Version
        }
        $finalRoot = Join-Path $OutputRoot 'final-launch'
        [IO.Directory]::CreateDirectory($finalRoot) | Out-Null
        if ($Scenario -eq 'default') { $shortcutSharingProbe = [AhkManager.TestSupport.PortableShortcutSharingProbe]::new($searchShortcutPath, (Join-Path $dataRoot 'Logs')) }
        $session = Start-PortableSession -Root $portableRoot -EvidenceRoot $finalRoot -ViaLauncher
        Wait-Ready $session $finalVersion -PublishedRollback:($Scenario -eq 'download-cancel')
        if ($shortcutSharingProbe) {
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while (-not $shortcutSharingProbe.Observed -and -not $shortcutSharingProbe.Released -and [string]::IsNullOrEmpty($shortcutSharingProbe.Failure) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 10 }
            Save-SharingProbeEvidence $session.Process.Id
            Check ($shortcutSharingProbe.Observed -and $shortcutSharingProbe.Released -and $shortcutSharingProbe.ObservedPid -eq $session.Process.Id -and $shortcutSharingProbe.ObservedCode -in @('EACCES', 'EPERM', 'EBUSY')) 'an exact owned read handle forces a real rename refusal and is released on the same application process retry diagnostic'
            $shortcutSharingProbe.Dispose(); $shortcutSharingProbe = $null
        }
        Check-SearchShortcut 'final'
        if ($Scenario -eq 'default') { Check-RelocatedHistory }
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            $finalUpdate = Invoke-PortableEvaluation $session 'window.ahkManager.getUpdateState()'
            if ($finalUpdate.phase -in @('idle', 'error')) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Check ($finalUpdate.phase -in @('idle', 'error')) 'the final automatic update check settles before diagnostic evidence is collected'
        $logManagerPid = $session.Process.Id
        $backendSessionToken = Read-BackendSessionToken $logManagerPid
        Check (-not [string]::IsNullOrEmpty($backendSessionToken)) 'the exact owned backend session is observed in memory before verifying its exclusion from diagnostics'
        Close-PortableSession $session; $session = $null
        Save-CommandEvidence $logManagerPid $backendSessionToken
        $backendSessionToken = $null
        $complete = $true
    }
}
catch { $failure = $_.Exception.Message; Write-Error -ErrorAction Continue $failure }
finally {
    if ($downloadProxy) {
        try { $downloadProxy.Dispose() }
        catch { if (-not $failure) { $failure = 'The loopback CONNECT fixture did not stop: ' + $_.Exception.Message } }
        Write-Json 'cancel-proxy-tunnels.json' $downloadProxy.Tunnels
    }
    if ($shortcutSharingProbe) {
        try { if ($outputCreated) { Save-SharingProbeEvidence $(if ($session) { $session.Process.Id } else { 0 }) } }
        finally { $shortcutSharingProbe.Dispose(); $shortcutSharingProbe = $null }
    }
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
        Write-Json 'portable-update-summary.json' ([ordered]@{ fixture = $true; scenario = $Scenario; publishedBaseline = [bool]$PublishedBaseline; baselineCommit = $baselineCommit; commit = $SourceCommit; complete = $complete; unknownHelperOutcome = $helperInFlight; signingFixtureCleanup = $signingFixtureCleanup; checks = $checks.ToArray(); failure = $failure })
    }
}
if ($failure) { throw $failure }
if (-not $complete) { throw 'The portable update regression did not complete.' }
Write-Host "Portable updater passed: $($checks.Count) checks; scenario=$Scenario."
