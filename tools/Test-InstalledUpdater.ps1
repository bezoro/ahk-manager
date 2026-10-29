#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$BaselineInstaller,
    [Parameter(Mandatory = $true)][string]$TargetInstaller,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateSet('PublishedPreview', 'Candidate')][string]$BaselineKind = 'PublishedPreview',
    [string]$BaselineManifest,
    [string]$BaselineCommit,
    [string]$InstallationParent,
    [switch]$PermissiveParent,
    [switch]$UnregisteredCopy,
    [string]$AdmissionHelperOverride,
    [ValidateSet('Installed', 'Refused')][string]$ExpectedOutcome = 'Installed',
    [ValidateRange(60, 900)][int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'The installed updater test requires a disposable GitHub-hosted Windows runner.'
}
Add-Type -Path (Join-Path $PSScriptRoot 'test-support/InstalledUpdaterUi.cs')
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this regression with an administrative token to match the affected installed app.' }

$utf8 = [Text.UTF8Encoding]::new($false)
$profileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Bezoro/AhkManagerElectron'
$chromiumRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Bezoro/AhkManagerElectron/Chromium'
$updatesRoot = Join-Path $profileRoot 'Updates'
$productGuid = '62d1c352-a9c4-556f-981c-2237ce4d0b68'
$installKey = 'Software\' + $productGuid
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $productGuid
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'AhkManager.CI.UpdatePreservation.' + [Guid]::NewGuid().ToString('N')
$registry = $null
$manager = $null
$inert = $null
$socket = $null
$capture = $null
$managerStarted = $null
$installRoot = $null
$managerPath = $null
$startupCommand = $null
$script:sequence = 0
$script:checks = [Collections.Generic.List[object]]::new()
$script:trace = [Collections.Generic.List[string]]::new()
$script:failure = $null
$script:outputCreated = $false
$script:settled = $false
$script:startupWritten = $false
$script:operation = $null
$script:confirmed = $false

function Trace([string]$Message) {
    $line = [DateTimeOffset]::UtcNow.ToString('O') + ' ' + $Message
    $script:trace.Add($line)
    Write-Host $line
}

function Assert-Check([bool]$Condition, [string]$Name) {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw $Name }
    Write-Host "PASS $Name"
}

function Local-Path([string]$Path, [switch]$File) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '["\x00\r\n]') { throw 'Use absolute local paths without command-line delimiters.' }
    $full = [IO.Path]::GetFullPath($Path)
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Redirected test path refused: $current" }
        }
    }
    if ($File -and -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "File missing: $full" }
    return $full
}

function Write-Json([string]$Name, [object]$Value) {
    [IO.File]::WriteAllText((Join-Path $OutputRoot $Name), ($Value | ConvertTo-Json -Depth 35), $utf8)
}

function Hash([string]$Path) { return (Get-FileHash -LiteralPath (Local-Path $Path -File) -Algorithm SHA256).Hash.ToLowerInvariant() }

function Version([string]$Path) {
    $raw = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
    if (Test-ReleaseVersion $raw) { return $raw }
    if ($raw -match '\A(\d+\.\d+\.\d+)\.0\z' -and (Test-ReleaseVersion $Matches[1])) { return $Matches[1] }
    throw "Unexpected installer version: $raw"
}

function Assert-CandidateIdentity {
    if ($BaselineCommit -cnotmatch '\A[0-9a-f]{40}\z' -or -not $BaselineManifest) { throw 'A candidate baseline requires its exact CI commit and release manifest.' }
    $manifestPath = Local-Path $BaselineManifest -File
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 65536) { throw 'The candidate release manifest exceeds its limit.' }
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($manifestPath))
    try {
        $root = $document.RootElement
        if ((@($root.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'commit,installer,notes,publishedAt,schemaVersion,updateProtocol,version') { throw 'Unexpected or duplicate candidate release fields.' }
        $package = $root.GetProperty('installer')
        if ((@($package.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'bytes,name,sha256,sha512') { throw 'Unexpected or duplicate candidate installer fields.' }
        $sha512 = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $BaselineInstaller -Algorithm SHA512).Hash))
        Assert-Check ($root.GetProperty('schemaVersion').GetInt32() -eq 1 -and $root.GetProperty('updateProtocol').GetInt32() -eq 1 -and $root.GetProperty('commit').GetString() -ceq $BaselineCommit -and $root.GetProperty('version').GetString() -ceq $baselineVersion) 'the candidate release manifest pins its schema, protocol, version and exact CI commit'
        Assert-Check ($package.GetProperty('name').GetString() -ceq [IO.Path]::GetFileName($BaselineInstaller) -and $package.GetProperty('name').GetString() -ceq "AHK-Manager-$baselineVersion-Setup-x64.exe" -and $package.GetProperty('bytes').GetInt64() -eq (Get-Item -LiteralPath $BaselineInstaller).Length -and $package.GetProperty('sha256').GetString() -ceq (Hash $BaselineInstaller) -and $package.GetProperty('sha512').GetString() -ceq $sha512) 'the candidate baseline bytes match both manifest checksums, length and exact filename'
        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $OutputRoot 'candidate-baseline-release.json')
    }
    finally { $document.Dispose() }
}

function Save-AdmissionDiagnostics([string]$Name) {
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($installRoot, $(if ($installRoot) { [IO.Path]::GetDirectoryName($installRoot) }), $profileRoot, $updatesRoot)) {
        if ($path) { $paths.Add($path) | Out-Null }
    }
    foreach ($folder in @('host', 'operations', 'installers')) {
        $root = Join-Path $updatesRoot $folder
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($item in Get-ChildItem -LiteralPath $root -Directory -Recurse -Force | Select-Object -First 100) { $paths.Add($item.FullName) | Out-Null }
    }
    $acls = foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        try {
            $acl = Get-Acl -LiteralPath (Local-Path $path)
            [ordered]@{ path = $path; owner = $acl.Owner; sddl = $acl.Sddl; rules = @($acl.Access | Select-Object IdentityReference, FileSystemRights, AccessControlType, IsInherited, InheritanceFlags, PropagationFlags) }
        }
        catch { [ordered]@{ path = $path; error = $_.Exception.Message } }
    }
    $images = foreach ($path in @($managerPath, $(if ($installRoot) { Join-Path $installRoot 'backend/update/AhkManager.UpdateHost.exe' }))) {
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        [ordered]@{ path = $path; sha256 = Hash $path; bytes = (Get-Item -LiteralPath $path).Length; productVersion = $info.ProductVersion; fileVersion = $info.FileVersion }
    }
    Write-Json "$Name-admission.json" ([ordered]@{ sid = $identity.User.Value; administrativeToken = $true; managerPid = $(if ($manager) { $manager.Id }); managerStartedAtUtc = $managerStarted; images = @($images); directories = @($acls) })
    $operations = Join-Path $updatesRoot 'operations'
    if (Test-Path -LiteralPath $operations -PathType Container) {
        foreach ($directory in Get-ChildItem -LiteralPath $operations -Directory | Select-Object -First 10) {
            $destination = Join-Path $OutputRoot (Join-Path 'operations' $directory.Name)
            [IO.Directory]::CreateDirectory($destination) | Out-Null
            foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File -Force) {
                if ($file.Name -notmatch '\A(?:plan|ready|commit|cancel|outcome|registry-before|backup-manifest|helper-refusal)\.json\z|\A(?:helper|host-stderr)\.log\z' -or $file.Length -gt 8MB) { continue }
                try { Copy-Item -LiteralPath (Local-Path $file.FullName -File) -Destination (Join-Path $destination $file.Name) -Force }
                catch { Trace "Operation evidence unavailable: $($file.Name); $($_.Exception.GetType().Name)" }
            }
        }
    }
}

function Invoke-Cdp([string]$Method, [hashtable]$Parameters = @{}) {
    $script:sequence++
    $commandId = $script:sequence
    $bytes = $utf8.GetBytes((@{ id = $commandId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 15 -Compress))
    $deadline = [Threading.CancellationTokenSource]::new(10000)
    try {
        $socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $deadline.Token).GetAwaiter().GetResult() | Out-Null
        while ($true) {
            $message = [IO.MemoryStream]::new()
            try {
                do {
                    $buffer = [byte[]]::new(65536)
                    $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $deadline.Token).GetAwaiter().GetResult()
                    if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'The installed app closed its debugging transport.' }
                    if ($message.Length + $received.Count -gt 8MB) { throw 'The debugging response exceeded its fixture limit.' }
                    $message.Write($buffer, 0, $received.Count)
                } while (-not $received.EndOfMessage)
                $reply = $utf8.GetString($message.ToArray()) | ConvertFrom-Json -Depth 40
            }
            finally { $message.Dispose() }
            if ($reply.PSObject.Properties['id'] -and $reply.id -eq $commandId) {
                if ($reply.PSObject.Properties['error']) { throw "CDP command failed: $($reply.error.message)" }
                return $reply.result
            }
        }
    }
    finally { $deadline.Dispose() }
}

function Evaluate([string]$Expression) {
    $answer = Invoke-Cdp 'Runtime.evaluate' @{ expression = $Expression; awaitPromise = $true; returnByValue = $true }
    if ($answer.PSObject.Properties['exceptionDetails']) { throw "Installed renderer evaluation failed: $($answer.exceptionDetails.text)" }
    if (-not $answer.result.PSObject.Properties['value']) { return $null }
    return $answer.result.value
}

function Save-WindowDiagnostics([string]$Name) {
    if (-not $manager -or $manager.HasExited) { return }
    $rendered = $null
    try { $rendered = Evaluate '({ readyState: document.readyState, visibility: document.visibilityState, focused: document.hasFocus(), mainMounted: !!document.querySelector("main"), dialogOpen: !!document.querySelector("[role=dialog]"), text: document.body.innerText.slice(0,4096) })' }
    catch { Trace "Renderer readiness diagnostics unavailable: $($_.Exception.GetType().Name)" }
    Write-Json "$Name-windows.json" ([ordered]@{ rendered = $rendered; windows = [AhkManager.TestSupport.InstalledUpdaterUi]::Describe($manager.Id, $managerPath, $managerStarted) })
}

function Installed-Version {
    $key = $registry.OpenSubKey($uninstallKey)
    try { return $(if ($key) { $key.GetValue('DisplayVersion') }) }
    finally { if ($key) { $key.Dispose() } }
}

function Remove-FreshFixtureRegistration {
    # The initial guards established that neither key existed before installing
    # this verified fixture. Revalidate its exact identity immediately before
    # removing only those two keys to model a copied, unregistered application.
    $installation = $registry.OpenSubKey($installKey)
    $uninstall = $registry.OpenSubKey($uninstallKey)
    try {
        Assert-Check ($installation -and $uninstall -and $installation.GetValue('InstallLocation') -ceq $installRoot -and $installation.GetValue('UpdateProtocol') -eq 1 -and $uninstall.GetValue('DisplayVersion') -ceq $baselineVersion) 'only the freshly installed fixture registration may be removed'
        $command = $uninstall.GetValue('UninstallString')
        if ($command -isnot [string] -or $command -notmatch '^"([^"]+)" /currentuser$') { throw 'The fresh fixture uninstaller command is unexpected.' }
        $uninstaller = Local-Path $Matches[1] -File
        Assert-Check ([IO.Path]::GetDirectoryName($uninstaller) -ceq $installRoot -and [IO.Path]::GetFileName($uninstaller) -ceq 'Uninstall Bezoro.AhkManager.exe') 'the fixture registration identifies its exact installed uninstaller'
        Write-Json 'unregistered-fixture-registration.json' ([ordered]@{ installationKey = $installKey; uninstallKey = $uninstallKey; installLocation = $installRoot; version = $baselineVersion; uninstallCommand = $command; uninstallerSha256 = Hash $uninstaller; removedForFixtureOnly = $true })
    }
    finally {
        if ($installation) { $installation.Dispose() }
        if ($uninstall) { $uninstall.Dispose() }
    }
    $registry.DeleteSubKeyTree($uninstallKey, $true)
    $registry.DeleteSubKeyTree($installKey, $true)
}

function Assert-NoFixtureRegistration {
    foreach ($path in @($installKey, $uninstallKey)) {
        $key = $registry.OpenSubKey($path)
        try { Assert-Check ($null -eq $key) 'an unregistered copy does not create an installation or uninstall registration' }
        finally { if ($key) { $key.Dispose() } }
    }
}

try {
    $BaselineInstaller = Local-Path $BaselineInstaller -File
    $TargetInstaller = Local-Path $TargetInstaller -File
    if ($UnregisteredCopy -and ($ExpectedOutcome -ne 'Refused' -or $BaselineKind -ne 'Candidate' -or $AdmissionHelperOverride)) {
        throw 'The unregistered-copy fixture requires a verified candidate and an explicit refusal expectation, without a helper override.'
    }
    if ($AdmissionHelperOverride) {
        $AdmissionHelperOverride = Local-Path $AdmissionHelperOverride -File
        if ($ExpectedOutcome -ne 'Refused' -or -not $PermissiveParent -or [IO.Path]::GetFileName($AdmissionHelperOverride) -cne 'AhkManager.UpdateHost.exe') {
            throw 'A candidate helper is allowed only in the explicit permissive-parent refusal diagnostic; it cannot claim an unmodified baseline or authorize installation.'
        }
    }
    $OutputRoot = Local-Path $OutputRoot
    if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a fresh output directory.' }
    foreach ($path in @($profileRoot, $chromiumRoot)) { if (Test-Path -LiteralPath $path) { throw 'An operational profile already exists; the installed app test will not open it.' } }
    if (@(Get-Process -Name 'Bezoro.AhkManager' -ErrorAction SilentlyContinue).Count) { throw 'A Manager is already running; the test will not control it.' }
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                foreach ($path in @($installKey, $uninstallKey)) {
                    $key = $base.OpenSubKey($path)
                    try { if ($key) { throw 'An installation is already registered; no installed-app test is allowed.' } }
                    finally { if ($key) { $key.Dispose() } }
                }
            }
            finally { $base.Dispose() }
        }
    }
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $registry.OpenSubKey($runKey)
    try {
        if ($key -and ($null -ne $key.GetValue('Bezoro.AhkManager.WinUI') -or $null -ne $key.GetValue($runValue))) { throw 'An existing startup entry will not be replaced.' }
    }
    finally { if ($key) { $key.Dispose() } }
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $script:outputCreated = $true
    $baselineVersion = Version $BaselineInstaller
    $targetVersion = Version $TargetInstaller
    if ($BaselineKind -eq 'PublishedPreview') {
        Assert-Check ((Get-Item -LiteralPath $BaselineInstaller).Length -eq 165631742 -and (Hash $BaselineInstaller) -ceq '232ad40675af1eeb8f9b32867482c38766b79275139f9e0784f26ad1714a913f') 'the published preview baseline matches its pinned bytes and SHA256'
        Assert-Check ($baselineVersion -ceq '1.3.1-test.29.1.2') 'the test uses the affected immutable published preview'
    }
    else { Assert-CandidateIdentity }
    Assert-Check ($targetVersion -cne $baselineVersion) 'baseline and target have distinct release identities'
    if (-not $InstallationParent) { $InstallationParent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs' }
    $InstallationParent = Local-Path $InstallationParent
    if ($PermissiveParent) {
        if (Test-Path -LiteralPath $InstallationParent) { throw 'The permissive ACL fixture requires a new parent directory; existing directories will not be modified.' }
        [IO.Directory]::CreateDirectory($InstallationParent) | Out-Null
        $acl = Get-Acl -LiteralPath $InstallationParent
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), [Security.AccessControl.FileSystemRights]::Modify, [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $InstallationParent -AclObject $acl
    }
    $installRoot = Local-Path (Join-Path $InstallationParent ('AHK Manager CI ' + [Guid]::NewGuid().ToString('N')))
    $managerPath = Join-Path $installRoot 'Bezoro.AhkManager.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($BaselineInstaller)
    $start.UseShellExecute = $false
    $start.Arguments = '/S /D=' + $installRoot
    $setup = [Diagnostics.Process]::Start($start)
    try {
        if (-not $setup.WaitForExit(180000)) { throw 'Baseline setup exit is unknown. No setup or application will be terminated.' }
        Assert-Check ($setup.ExitCode -eq 0) 'the verified baseline installs successfully'
    }
    finally { $setup.Dispose() }
    Assert-Check ((Installed-Version) -ceq $baselineVersion) 'the verified baseline is registered'
    if ($UnregisteredCopy) {
        Remove-FreshFixtureRegistration
        Assert-NoFixtureRegistration
    }
    if ($AdmissionHelperOverride) {
        $helperPath = Join-Path $installRoot 'backend/update/AhkManager.UpdateHost.exe'
        [IO.File]::Copy($AdmissionHelperOverride, $helperPath, $true)
        Write-Json 'candidate-helper-override.json' ([ordered]@{ diagnosticOnly = $true; baselineModified = $true; source = $AdmissionHelperOverride; sha256 = Hash $helperPath })
        Trace 'The refusal-only diagnostic explicitly substitutes the candidate helper; this is not an unmodified-baseline update proof.'
    }
    [IO.Directory]::CreateDirectory($profileRoot) | Out-Null
    $settings = [ordered]@{
        Schema = 1
        Scripts = @()
        Collections = @()
        Settings = [ordered]@{
            InterpreterPath = $null
            EditorPath = $null
            EditorArguments = @()
            LaunchManagerAtLogin = $false
            LaunchManagerAsAdministrator = $false
            ShowWindowAtLogin = $true
            StartInTray = $false
            Theme = 0
            CloseBehavior = 1
            Window = [ordered]@{ X = 80; Y = 80; Width = 1200; Height = 780; Maximized = $false; Dpi = 96 }
        }
        LastLoginKey = ''
    }
    [IO.File]::WriteAllText((Join-Path $profileRoot 'settings.json'), ($settings | ConvertTo-Json -Depth 12), $utf8)
    $sentinel = Join-Path $profileRoot 'ci-profile-sentinel.txt'
    $foreign = Join-Path $installRoot 'ci-foreign-sentinel.txt'
    $scriptFile = Join-Path $OutputRoot 'inert-preserved.ahk'
    foreach ($path in @($sentinel, $foreign, $scriptFile)) { [IO.File]::WriteAllText($path, '; Inert isolated CI preservation fixture. Never executed.', $utf8) }
    $preserved = @{}
    foreach ($path in @($sentinel, $foreign, $scriptFile)) { $preserved[$path] = Hash $path }
    $cached = Join-Path $updatesRoot "installers/$targetVersion/installer.exe"
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($cached)) | Out-Null
    [IO.File]::Copy($TargetInstaller, $cached, $false)
    $targetHash = Hash $TargetInstaller
    $receipt = [ordered]@{ schemaVersion = 1; updateProtocol = 1; version = $targetVersion; sha256 = $targetHash; bytes = (Get-Item -LiteralPath $TargetInstaller).Length }
    [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($cached)) 'receipt.json'), ($receipt | ConvertTo-Json -Compress), $utf8)
    [IO.File]::WriteAllText((Join-Path $updatesRoot 'update-channel.json'), '{"schemaVersion":1,"channel":"test"}', $utf8)
    # Preserve a genuinely unrelated, inert Run entry. Pointing another value at
    # the Manager would itself introduce an external startup conflict.
    $startupCommand = '"' + (Join-Path $env:WINDIR 'System32/cmd.exe') + '" /d /c exit 0'
    $key = $registry.CreateSubKey($runKey, $true)
    try { $key.SetValue($runValue, $startupCommand, [Microsoft.Win32.RegistryValueKind]::String); $script:startupWritten = $true }
    finally { $key.Dispose() }
    $beforeImages = @{}
    foreach ($path in @($managerPath, (Join-Path $installRoot 'resources/app.asar'), (Join-Path $installRoot 'backend/update/AhkManager.UpdateHost.exe'))) { $beforeImages[$path] = Hash $path }
    Save-AdmissionDiagnostics 'before-launch'

    # A harmless unrelated process waits for EOF. The updater must leave it alive;
    # the test releases it by closing its input, never by terminating a process.
    $waiter = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $waiter.UseShellExecute = $false
    $waiter.RedirectStandardInput = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', '[Console]::In.ReadLine() | Out-Null')) { $waiter.ArgumentList.Add($argument) }
    $inert = [Diagnostics.Process]::Start($waiter)

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $start = [Diagnostics.ProcessStartInfo]::new($managerPath)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $installRoot
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--foreground', '--remote-debugging-address=127.0.0.1', "--remote-debugging-port=$port")) { $start.ArgumentList.Add($argument) }
    $manager = [Diagnostics.Process]::Start($start)
    $managerStarted = $manager.StartTime.ToUniversalTime()
    $capture = [AhkManager.TestSupport.InstalledUpdaterUi]::Capture($manager, (Join-Path $OutputRoot 'manager.stdout.log'), (Join-Path $OutputRoot 'manager.stderr.log'))
    Trace "Started installed baseline=$BaselineKind; version=$baselineVersion; PID=$($manager.Id); creationUtc=$($managerStarted.ToString('O')); CDP loopback port=$port"
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(2)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        $target = $null
        do {
            if ($manager.HasExited) { throw "The installed baseline exited before its interface was available; exit=$($manager.ExitCode)." }
            try {
                $pages = $http.GetStringAsync("http://127.0.0.1:$port/json/list").GetAwaiter().GetResult() | ConvertFrom-Json
                $target = @($pages | Where-Object { $_.type -eq 'page' -and $_.url -like 'ahk-manager://*' }) | Select-Object -First 1
            }
            catch { $target = $null }
            if (-not $target) { Start-Sleep -Milliseconds 200 }
        } while (-not $target -and [DateTime]::UtcNow -lt $deadline)
        if (-not $target) { throw 'The packaged app did not expose its loopback CDP target. No production test hook or replacement entry will be used.' }
    }
    finally { $http.Dispose() }
    $address = [Uri]$target.webSocketDebuggerUrl
    Assert-Check ($address.Host -in @('127.0.0.1', 'localhost') -and $address.Port -eq $port) 'debugging connects only to the isolated app loopback endpoint'
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    $connect = [Threading.CancellationTokenSource]::new(10000)
    try { $socket.ConnectAsync($address, $connect.Token).GetAwaiter().GetResult() | Out-Null }
    finally { $connect.Dispose() }
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $hostState = Evaluate 'window.ahkManager ? window.ahkManager.getState() : null'
        if ($hostState -and $hostState.connection -eq 'ready' -and -not $hostState.busy) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json 'installed-host-state.json' $hostState
    Assert-Check ($hostState -and $hostState.connection -eq 'ready' -and $hostState.authorityMode -eq 'Administrator' -and $hostState.snapshot.scripts.Count -eq 0 -and -not $hostState.snapshot.readOnly) 'the installed baseline has a real administrative backend and an empty writable fixture profile'
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $updateState = Evaluate 'window.ahkManager.getUpdateState()'
        $choice = @($updateState.releases | Where-Object { $_.version -ceq $targetVersion -and $_.canInstall -and $_.cached }) | Select-Object -First 1
        if ($choice -and $updateState.phase -notin @('checking', 'downloading', 'installing')) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json 'installed-update-before.json' $updateState
    Assert-Check ($null -ne $choice) 'the real updater exposes the verified target through version history'
    Invoke-Cdp 'Page.bringToFront' | Out-Null
    $readiness = [Collections.Generic.List[object]]::new()
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $activated = [AhkManager.TestSupport.InstalledUpdaterUi]::Focus($manager.Id, $managerPath, $managerStarted)
        $rendered = Evaluate '({ complete: document.readyState === "complete", visible: document.visibilityState === "visible", focused: document.hasFocus(), mainMounted: !!document.querySelector("main"), dialogOpen: !!document.querySelector("[role=dialog]") })'
        $readiness.Add([ordered]@{ at = [DateTimeOffset]::UtcNow.ToString('O'); activationAccepted = $activated; rendered = $rendered })
        if ($rendered.complete -and $rendered.visible -and $rendered.focused -and $rendered.mainMounted -and -not $rendered.dialogOpen) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Json 'installed-renderer-readiness.json' $readiness.ToArray()
    Save-WindowDiagnostics 'before-consent'
    Assert-Check ($rendered.complete -and $rendered.visible -and $rendered.focused -and $rendered.mainMounted -and -not $rendered.dialogOpen) 'the actual rendered interface is mounted, visible and focused before update preflight'
    # Let React publish its live busy/draft guard after the direct backend poll.
    Evaluate 'new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))' | Out-Null
    $choiceJson = ConvertTo-Json -InputObject $choice.id -Compress
    Evaluate ("globalThis.__installedUpdaterResult = { pending: true }; void window.ahkManager.installUpdate($choiceJson).then(value => { globalThis.__installedUpdaterResult = { pending: false, value }; }, error => { globalThis.__installedUpdaterResult = { pending: false, error: String(error) }; }); true") | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $windowDiagnosticAt = [DateTime]::UtcNow.AddSeconds(3)
    $result = $null
    do {
        $manager.Refresh()
        if ($manager.HasExited) { break }
        if (-not $script:confirmed -and [AhkManager.TestSupport.InstalledUpdaterUi]::Confirm($manager.Id, $managerPath, $managerStarted, $targetVersion)) {
            $script:confirmed = $true
            Trace 'Confirmed the exact target through the installed app native dialog.'
        }
        try { $result = Evaluate 'globalThis.__installedUpdaterResult' }
        catch { if ($ExpectedOutcome -eq 'Installed' -and $script:confirmed -and $socket.State -ne [Net.WebSockets.WebSocketState]::Open -and $manager.WaitForExit(30000)) { break }; throw }
        if ($result -and -not $result.pending) { break }
        if ([DateTime]::UtcNow -ge $windowDiagnosticAt) {
            Save-WindowDiagnostics 'pending-consent'
            $windowDiagnosticAt = [DateTime]::MaxValue
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Save-AdmissionDiagnostics 'after-handoff'
    Save-WindowDiagnostics 'after-handoff'
    Write-Json 'installed-update-result.json' $result
    Assert-Check $script:confirmed 'the real native update consent was obtained for the exact target'
    $operations = @(Get-ChildItem -LiteralPath (Join-Path $updatesRoot 'operations') -Directory -ErrorAction SilentlyContinue)
    Assert-Check ($operations.Count -eq 1) 'the installed app dispatched exactly one operation without replay'
    $script:operation = $operations[0].FullName
    $plan = Get-Content -LiteralPath (Join-Path $script:operation 'plan.json') -Raw | ConvertFrom-Json
    Assert-Check ($plan.parentPid -eq $manager.Id -and ([DateTimeOffset]$plan.parentStartedAtUtc).ToUnixTimeMilliseconds() -eq [DateTimeOffset]::new($managerStarted).ToUnixTimeMilliseconds() -and $plan.installDirectory -eq $installRoot -and $plan.installerSha256 -ceq $targetHash) 'the production plan binds the actual installed parent session, exact installation and target bytes'
    if ($ExpectedOutcome -eq 'Refused') {
        Assert-Check (-not $manager.HasExited -and $result -and -not $result.pending -and $result.value.phase -eq 'error' -and $result.value.error) 'the installed baseline reports its refusal and stays open'
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $script:operation 'ready.json')) -and -not (Test-Path -LiteralPath (Join-Path $script:operation 'commit.json'))) 'native rejection occurs before readiness and shutdown authorization'
        if ($UnregisteredCopy) {
            Assert-NoFixtureRegistration
            $refusal = Get-Content -LiteralPath (Join-Path $script:operation 'helper-refusal.json') -Raw | ConvertFrom-Json
            Assert-Check ($refusal.schemaVersion -eq 1 -and $refusal.operationId -eq $plan.operationId -and $refusal.exitCode -eq 65 -and $refusal.refusal.schemaVersion -eq 1 -and $refusal.refusal.phase -ceq 'registration' -and $refusal.refusal.code -ceq 'invalid-data') 'the candidate reports an unregistered copy through the native registration refusal phase'
            Assert-Check (-not (Test-Path -LiteralPath (Join-Path $script:operation 'registry-before.json')) -and -not (Test-Path -LiteralPath (Join-Path $script:operation 'backup-installation'))) 'an unregistered copy never enters backup or installation stages'
            $outcome = Get-Content -LiteralPath (Join-Path $script:operation 'outcome.json') -Raw | ConvertFrom-Json
            Assert-Check ($outcome.outcome -ceq 'Failed' -and $null -eq $outcome.installerExitCode -and $null -eq $outcome.fallbackExitCode) 'the refused unregistered copy records no installer dispatch'
        }
        else {
            Assert-Check ((Installed-Version) -ceq $baselineVersion) 'refusal retains the registered baseline version'
            if ($BaselineKind -eq 'Candidate' -and $PermissiveParent) {
                $refusal = Get-Content -LiteralPath (Join-Path $script:operation 'helper-refusal.json') -Raw | ConvertFrom-Json
                Assert-Check ($refusal.schemaVersion -eq 1 -and $refusal.operationId -eq $plan.operationId -and $refusal.exitCode -eq 65 -and $refusal.refusal.schemaVersion -eq 1 -and $refusal.refusal.phase -cin @('installation-ownership', 'installation-parent-ownership') -and $refusal.refusal.code -ceq 'access-denied') 'the candidate refuses the deliberate unsafe installation permissions through its native ownership checks'
            }
        }
        foreach ($path in $beforeImages.Keys) { Assert-Check ((Hash $path) -ceq $beforeImages[$path]) "refusal preserves $([IO.Path]::GetFileName($path))" }
        $script:settled = $true
    }
    else {
        Assert-Check ($manager.WaitForExit(30000) -and $manager.ExitCode -eq 0) 'the authenticated installed Manager exits normally after its backend drains'
        $ready = Get-Content -LiteralPath (Join-Path $script:operation 'ready.json') -Raw | ConvertFrom-Json
        $commit = Get-Content -LiteralPath (Join-Path $script:operation 'commit.json') -Raw | ConvertFrom-Json
        Assert-Check ($ready.operationId -eq $plan.operationId -and $commit.operationId -eq $plan.operationId -and $ready.parentPid -eq $manager.Id -and $commit.parentPid -eq $manager.Id) 'the native ready and shutdown commit identify the single installed-app operation'
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $outcomePath = Join-Path $script:operation 'outcome.json'
        while (-not (Test-Path -LiteralPath $outcomePath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $outcomePath -PathType Leaf)) { throw 'The helper outcome is unknown. The test will preserve every process and recovery copy.' }
        $outcome = Get-Content -LiteralPath $outcomePath -Raw | ConvertFrom-Json
        Write-Json 'installed-outcome.json' $outcome
        Assert-Check ($outcome.outcome -eq 'Installed' -and $outcome.operationId -eq $plan.operationId -and $outcome.version -ceq $targetVersion -and $outcome.installerExitCode -eq 0) 'the real helper installs the exact target successfully'
        Assert-Check ((Installed-Version) -ceq $targetVersion) 'the actual target is registered after the app-driven update'
        $image = [Diagnostics.FileVersionInfo]::GetVersionInfo($managerPath)
        Assert-Check ($image.FileVersion -ceq $targetVersion -and [version]$image.ProductVersion -eq [version]($targetVersion.Split('-')[0] + '.0')) 'the installed target image retains exact SemVer and numeric Windows resources'
        $script:settled = $true
    }
    foreach ($path in $preserved.Keys) { Assert-Check ((Hash $path) -ceq $preserved[$path]) "the app-driven update preserves $([IO.Path]::GetFileName($path))" }
    $key = $registry.OpenSubKey($runKey)
    try { Assert-Check ($key -and $key.GetValue($runValue) -ceq $startupCommand) 'the update preserves the exact unrelated startup entry' }
    finally { if ($key) { $key.Dispose() } }
    Assert-Check (-not $inert.HasExited) 'the unrelated inert process keeps running throughout update admission and installation'
    Save-AdmissionDiagnostics 'completed'
    Trace "Installed app regression confirmed expected outcome: $ExpectedOutcome."
}
catch { $script:failure = $_.Exception.Message; Write-Error -ErrorAction Continue $script:failure }
finally {
    if ($script:outputCreated) {
        try { Save-AdmissionDiagnostics 'final' } catch { Trace "Final diagnostics unavailable: $($_.Exception.Message)" }
    }
    if ($socket) { $socket.Dispose() }
    # Unknown helper/install outcomes retain all processes and state. Normal
    # completion uses only the Manager's own CloseBehavior=Exit path.
    if ($script:settled -and $managerPath) {
        foreach ($candidate in @(Get-Process -Name 'Bezoro.AhkManager' -ErrorAction SilentlyContinue)) {
            try {
                $created = $candidate.StartTime.ToUniversalTime()
                if ($created -ge $managerStarted -and [AhkManager.TestSupport.InstalledUpdaterUi]::Matches($candidate.Id, $managerPath, $created) -and [AhkManager.TestSupport.InstalledUpdaterUi]::Close($candidate.Id, $managerPath, $created)) {
                    if (-not $candidate.WaitForExit(30000)) { throw 'The test Manager did not close normally. It was preserved.' }
                    if ($candidate.ExitCode -ne 0) { throw "The test Manager close returned $($candidate.ExitCode)." }
                }
            }
            catch { $script:failure = "Graceful Manager close: $($_.Exception.Message)" }
            finally { $candidate.Dispose() }
        }
        if ($script:startupWritten -and $registry) {
            $key = $registry.OpenSubKey($runKey, $true)
            try { if ($key -and $key.GetValue($runValue) -ceq $startupCommand) { $key.DeleteValue($runValue, $false) } }
            finally { if ($key) { $key.Dispose() } }
        }
    }
    if ($inert) {
        $inert.StandardInput.Close()
        if (-not $inert.WaitForExit(10000)) { $script:failure = 'The inert test process did not exit after input closed; it was preserved.' }
        $inert.Dispose()
    }
    if ($capture) { $capture.Dispose() }
    if ($manager) { $manager.Dispose() }
    if ($registry) { $registry.Dispose() }
    if ($script:outputCreated) {
        [IO.File]::WriteAllLines((Join-Path $OutputRoot 'installed-update.log'), $script:trace.ToArray(), $utf8)
        Write-Json 'installed-update-summary.json' ([ordered]@{ fixture = $true; baselineKind = $BaselineKind; expectedOutcome = $ExpectedOutcome; baselineModified = [bool]$AdmissionHelperOverride; permissiveParent = [bool]$PermissiveParent; unregisteredCopy = [bool]$UnregisteredCopy; checks = $script:checks.ToArray(); failure = $script:failure; settled = $script:settled; installation = $installRoot; operation = $script:operation; confirmed = $script:confirmed })
    }
    $identity.Dispose()
}
if ($script:failure) { throw $script:failure }
if (-not $script:settled) { throw 'The installed update outcome was not confirmed.' }
Write-Host "Installed updater regression passed: $($script:checks.Count) checks; outcome=$ExpectedOutcome."
