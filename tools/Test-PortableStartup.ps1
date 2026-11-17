#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$FixtureInterpreter,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'Portable startup smoke requires a disposable GitHub-hosted Windows runner.'
}

function Get-CanonicalLocalPath([string]$Path) {
    $localPath = $Path.Replace('/', '\')
    if ($localPath -cnotmatch '\A[A-Za-z]:\\[^\\]' -or
        $localPath.Substring(3) -match '(?:[<>:"|?*\[\]\x00-\x1f]|[ .](?:\\|$)|\\$)' -or
        $localPath.Substring(3) -match '(?:^|\\)(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:[.\\]|$)' -or
        [IO.Path]::GetFullPath($localPath) -ne $localPath) {
        throw "A canonical local Windows path is required: $Path"
    }
    return $localPath
}

function Assert-PortablePath([string]$Path, [ValidateSet('Directory', 'File', 'Fresh', 'Absent')][string]$Kind) {
    $ancestors = [Collections.Generic.Stack[string]]::new()
    for ($current = $Path; $null -ne $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $ancestors.Push($current)
    }
    while ($ancestors.Count) {
        $current = $ancestors.Pop()
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] {
            if ($Kind -eq 'Absent' -or ($Kind -eq 'Fresh' -and $current -eq $Path)) { return }
            throw
        }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "A redirected path is forbidden: $current" }
        $directory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        if ($ancestors.Count -or $Kind -eq 'Directory') {
            if (-not $directory) { throw "A regular directory is required: $current" }
        }
        elseif ($Kind -ne 'File' -or $directory) {
            throw "Unexpected filesystem entry: $current"
        }
    }
}

function Assert-NoPortableRegistration([string]$Sid) {
    $installId = '62d1c352-a9c4-556f-981c-2237ce4d0b68'
    foreach ($hive in @('CurrentUser', 'LocalMachine')) {
        foreach ($view in @('Registry64', 'Registry32')) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                foreach ($path in @("Software\$installId", "Software\Microsoft\Windows\CurrentVersion\Uninstall\$installId")) {
                    $key = $base.OpenSubKey($path)
                    if ($key) { $key.Dispose(); throw 'Manager installer registration was found.' }
                }
                $run = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
                try {
                    if ($run -and $run.GetValueNames() -contains 'Bezoro.AhkManager.WinUI') { throw 'Manager login registration was found.' }
                }
                finally { if ($run) { $run.Dispose() } }
            }
            finally { $base.Dispose() }
        }
    }
    if (@(Get-CimInstance -Namespace 'root/Microsoft/Windows/TaskScheduler' -ClassName MSFT_ScheduledTask -Filter "TaskName='Bezoro.AhkManager.Electron.$Sid'" -OperationTimeoutSec 5).Count) {
        throw 'Manager login task was found.'
    }
}

$runnerTemp = Get-CanonicalLocalPath $env:RUNNER_TEMP
$drive = [IO.Path]::GetPathRoot($runnerTemp)
if ([IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed -or
    -not (Get-CimInstance Win32_Volume -Filter "DriveLetter='$($drive.Substring(0, 2))'" -OperationTimeoutSec 5)) {
    throw 'RUNNER_TEMP must be on a local fixed volume.'
}
Assert-PortablePath $runnerTemp Directory
$ArchivePath = Get-CanonicalLocalPath $ArchivePath
$ManifestPath = Get-CanonicalLocalPath $ManifestPath
$OutputRoot = Get-CanonicalLocalPath $OutputRoot
foreach ($path in @($ArchivePath, $ManifestPath, $OutputRoot)) {
    if (-not $path.StartsWith($runnerTemp + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Use a strict descendant of RUNNER_TEMP: $path"
    }
}
Assert-PortablePath $ArchivePath File
Assert-PortablePath $ManifestPath File
Assert-PortablePath $OutputRoot Fresh

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Portable startup smoke requires an existing administrative token.'
    }
    $sid = $identity.User.Value
}
finally { $identity.Dispose() }

$null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputRoot)) -Name ([IO.Path]::GetFileName($OutputRoot))
Assert-PortablePath $OutputRoot Directory
$summary = [ordered]@{ fixture = $true; phase = 'admission'; version = $Version; commit = $SourceCommit; archiveSha256 = $null; manifestSha256 = $null; host = $null; reportedVersion = $null; storage = $null; shutdown = 'NotStarted'; failure = $null; shutdownFailure = $null }
$session = $null
try {
    $local = Get-CanonicalLocalPath ([Environment]::GetFolderPath('LocalApplicationData'))
    $roaming = Get-CanonicalLocalPath ([Environment]::GetFolderPath('ApplicationData'))
    if ($env:LOCALAPPDATA -and (Get-CanonicalLocalPath $env:LOCALAPPDATA) -ne $local) { throw 'LOCALAPPDATA is redirected.' }
    if ($env:APPDATA -and (Get-CanonicalLocalPath $env:APPDATA) -ne $roaming) { throw 'APPDATA is redirected.' }
    $FixtureInterpreter = Get-CanonicalLocalPath $FixtureInterpreter
    if ($FixtureInterpreter -ne (Join-Path $local 'Programs/AutoHotkey/v2/AutoHotkey64.exe')) {
        throw 'The pinned runner AutoHotkey interpreter is required.'
    }
    Assert-PortablePath $FixtureInterpreter File
    $interpreterHash = '2eab99848ebc07f8ab0cc448bf69af8c700b2fc7fcd1ff8794df743dcc58da51'
    if ((Get-FileHash -LiteralPath $FixtureInterpreter -Algorithm SHA256).Hash -ne $interpreterHash) {
        throw 'The pinned AutoHotkey interpreter hash does not match.'
    }
    Assert-PortablePath (Join-Path $local 'Bezoro/AhkManagerElectron') Absent
    Assert-PortablePath (Join-Path $roaming 'Bezoro/AhkManagerElectron/Chromium') Absent
    Assert-PortablePath (Join-Path $roaming 'Microsoft/Windows/Start Menu/Programs/AHK Manager Portable.lnk') Absent
    if (@(Get-Process -Name 'Bezoro.AhkManager', 'AhkManager.Backend', 'AhkManager.UpdateHost' -ErrorAction SilentlyContinue).Count) {
        throw 'An existing Manager process prevents the startup fixture.'
    }
    Assert-NoPortableRegistration $sid

    $summary.phase = 'verify-and-extract'
    Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'test-support/PortableUpdaterSession.psm1') -Force
    Add-Type -Path (Join-Path $PSScriptRoot 'test-support/PortableUpdaterUi.cs')
    $portableRoot = Join-Path $OutputRoot 'Portable Manager'
    Assert-PortablePath $portableRoot Fresh
    $archiveLease = [IO.File]::Open($ArchivePath, 'Open', 'Read', 'Read')
    try {
        $release = Read-PortableReleaseManifest -ArchivePath $ArchivePath -ManifestPath $ManifestPath -ExpectedVersion $Version -ExpectedCommit $SourceCommit
        $summary.archiveSha256 = $release.Sha256
        $summary.manifestSha256 = $release.ManifestSha256
        [IO.Compression.ZipFile]::ExtractToDirectory($archiveLease, $portableRoot)
    }
    finally { $archiveLease.Dispose() }

    $summary.phase = 'inert-profile'
    $dataRoot = Join-Path $portableRoot 'Data'
    Assert-PortablePath $dataRoot Fresh
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($sid))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($trusted in @($sid, 'S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($trusted), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($dataRoot), $acl)
    $profileRoot = Join-Path $dataRoot 'Profile'
    Assert-PortablePath $profileRoot Fresh
    $null = [IO.Directory]::CreateDirectory($profileRoot)
    $profile = [ordered]@{
        Schema = 1
        Scripts = @()
        Collections = @()
        LastLoginKey = ''
        Settings = [ordered]@{
            InterpreterPath = $FixtureInterpreter
            EditorArguments = @()
            LaunchManagerAtLogin = $false
            LaunchManagerAsAdministrator = $true
            StartInTray = $false
            CloseBehavior = 1
            Window = [ordered]@{ X = 100; Y = 100; Width = 1200; Height = 780; Maximized = $false; Dpi = 96 }
        }
    }
    [IO.File]::WriteAllText((Join-Path $profileRoot 'settings.json'), ($profile | ConvertTo-Json -Depth 5))

    $summary.phase = 'launch-and-connect'
    Assert-PortablePath (Join-Path $portableRoot 'Bezoro.AhkManager.exe') File
    Assert-PortablePath (Join-Path $portableRoot 'App/Bezoro.AhkManager.exe') File
    Assert-PortablePath (Join-Path $portableRoot 'App/backend/AhkManager.Backend.exe') File
    Assert-PortablePath $profileRoot Directory
    $summary.shutdown = 'Unknown'
    $null = Start-PortableSession -Root $portableRoot -EvidenceRoot $OutputRoot -ViaLauncher -CaptureChromiumLog -OwnedSession ([ref]$session)
    $summary.phase = 'renderer-and-backend'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    do {
        if ($session.Process.HasExited) { throw "The portable application exited before readiness: $($session.Process.ExitCode)." }
        $summary.host = Invoke-PortableEvaluation $session @'
window.ahkManager ? window.ahkManager.getState().then(state => ({
  visible: document.readyState === 'complete' && document.visibilityState === 'visible' &&
    document.querySelector('#root .app-shell > main.workspace')?.checkVisibility({
      visibilityProperty: true, opacityProperty: true
    }) === true,
  connection: state.connection, error: state.error, busy: state.busy, authority: state.authorityMode,
  readOnly: state.snapshot?.readOnly ?? null, scripts: state.snapshot?.scripts.length ?? null,
  collections: state.snapshot?.collections.length ?? null,
  launchAtLogin: state.snapshot?.settings.launchManagerAtLogin ?? null
})) : null
'@
        if ($summary.host -and $summary.host.connection -ceq 'failed') { throw "The portable backend failed: $($summary.host.error)" }
        $ready = $summary.host -and $summary.host.visible -and $summary.host.connection -ceq 'ready' -and $summary.host.busy -eq $false
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } while (-not $ready -and $clock.Elapsed.TotalSeconds -lt 45)
    if (-not $ready -or $clock.Elapsed.TotalSeconds -ge 45) { throw 'The visible Manager workspace and backend did not become ready within 45 seconds.' }
    if ($summary.host.authority -cne 'Administrator' -or $summary.host.readOnly -ne $false -or $summary.host.scripts -ne 0 -or
        $summary.host.collections -ne 0 -or $summary.host.launchAtLogin -ne $false) {
        throw 'The portable backend did not load the isolated writable administrative profile.'
    }

    $summary.phase = 'runtime-identity'
    $update = Invoke-PortableEvaluation $session 'window.ahkManager.getUpdateState()'
    $summary.reportedVersion = $update.currentVersion
    if ($summary.reportedVersion -cne $release.Version) { throw "The running application reported version $($summary.reportedVersion); expected $($release.Version)." }
    $summary.storage = Invoke-PortableEvaluation $session 'window.ahkManager.getStorageState()'
    if ($summary.storage.mode -cne 'portable' -or $summary.storage.currentDataDirectory -ne $dataRoot -or
        $summary.storage.defaultDataDirectory -ne $dataRoot -or $null -ne $summary.storage.pendingDataDirectory -or $null -ne $summary.storage.error) {
        throw 'The running application did not report the exact isolated portable Data directory.'
    }
}
catch { $summary.failure = $_.Exception.Message }
finally {
    if ($session) {
        if (-not $summary.failure) { $summary.phase = 'shutdown' }
        try {
            if ($session.Socket) { $session.Socket.Dispose() }
            $ui = [AhkManager.TestSupport.PortableUpdaterUi]
            if (-not $ui::Matches($session.Process.Id, $session.Image, $session.Started) -or
                -not $ui::CloseAndWait($session.Process.Id, $session.Image, $session.Started)) {
                throw 'The exact owned application did not acknowledge WM_CLOSE and exit normally.'
            }
            if ($session.Process.ExitCode -ne 0) { throw "The owned portable application exited with code $($session.Process.ExitCode)." }
            $summary.shutdown = 'Confirmed'
        }
        catch { $summary.shutdownFailure = $_.Exception.Message }
        finally { $session.Process.Dispose() }
    }
    if (-not $summary.failure -and -not $summary.shutdownFailure -and $summary.shutdown -ceq 'Confirmed') {
        $summary.phase = 'logon-bootstrap'
        try {
            Import-Module (Join-Path $PSScriptRoot 'test-support/PortableAgentCli.psm1') -Force
            Assert-PortablePath (Join-Path $portableRoot 'portable.json') File
            Test-PortableLogonBootstrap -Root $portableRoot -EvidenceRoot $OutputRoot -Version $Version -DataRoot $dataRoot
            $summary.phase = 'agent-cli'
            $pipeName = Test-PortableAgentCli -Root $portableRoot -EvidenceRoot $OutputRoot -Version $Version
            $summary.phase = 'mcp'
            Test-PortableMcp -Root $portableRoot -EvidenceRoot $OutputRoot -Version $Version -PipeName $pipeName -Interpreter $FixtureInterpreter
            $summary.phase = 'verify-isolation'
            Assert-NoPortableRegistration $sid
            $summary.phase = 'complete'
        }
        catch { $summary.failure = $_.Exception.Message }
    }
    $summaryPath = Join-Path $OutputRoot 'portable-startup-summary.json'
    Assert-PortablePath $summaryPath Fresh
    [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 8))
}
if ($summary.phase -cne 'complete') { throw "Portable startup smoke failed at $($summary.phase). $($summary.failure) Shutdown: $($summary.shutdown). $($summary.shutdownFailure)" }
Write-Host "Portable startup confirmed through the root launcher: $Version, ready renderer and backend, normal shutdown."
