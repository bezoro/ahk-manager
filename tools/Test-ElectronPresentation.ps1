#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$FrontendRoot,
    [Parameter(Mandatory = $true)][string]$HarnessRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [ValidateRange(10, 120)][int]$StartupTimeoutSeconds = 45,
    [ValidateRange(10, 180)][int]$CommandTimeoutSeconds = 90,
    [ValidateRange(5, 60)][int]$ShutdownTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The presentation harness requires Windows.' }

# FrontendRoot is an existing production bundle. The other two roots must be
# absent: this script builds the fixture host and that host creates OutputRoot.
foreach ($path in @($FrontendRoot, $HarnessRoot, $OutputRoot)) {
    if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'All staging paths must be absolute.' }
}
$FrontendRoot = [IO.Path]::GetFullPath($FrontendRoot)
$HarnessRoot = [IO.Path]::GetFullPath($HarnessRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
foreach ($path in @($HarnessRoot, $OutputRoot)) {
    if (Test-Path -LiteralPath $path) { throw "Use a fresh staging directory: $path" }
}
if ($HarnessRoot.TrimEnd('\', '/') -eq $OutputRoot.TrimEnd('\', '/')) { throw 'HarnessRoot and OutputRoot must be different.' }
foreach ($asset in @('assets.json', 'renderer/index.html', 'renderer/app.js')) {
    if (-not (Test-Path -LiteralPath (Join-Path $FrontendRoot $asset) -PathType Leaf)) { throw "Frontend asset missing: $asset" }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'Presentation.Electron'
$electron = Join-Path $sourceRoot 'node_modules/electron/dist/electron.exe'
if (-not (Test-Path -LiteralPath $electron -PathType Leaf)) { throw 'Install the pinned Electron runtime before running this script.' }
$node = (Get-Command node.exe -ErrorAction Stop).Source
$utf8 = [Text.UTF8Encoding]::new($false)
$script:commandId = 0
$script:checks = [Collections.Generic.List[object]]::new()
$script:captures = [Collections.Generic.List[object]]::new()
$script:failures = [Collections.Generic.List[string]]::new()
$script:trace = [Collections.Generic.List[string]]::new()
$process = $null
$started = $false
$shutdownConfirmed = $false
$stdout = $null
$stderr = $null
$buildLog = @()
$exitCode = $null

function Write-Trace([string]$Message) {
    $line = [DateTimeOffset]::UtcNow.ToString('O') + ' ' + $Message
    $script:trace.Add($line)
    Write-Host $line
}

function Write-AtomicJson([string]$Path, [object]$Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 30), $utf8)
    [IO.File]::Move($temporary, $Path, $true)
}

function Wait-HarnessReceipt([string]$Name, [int]$TimeoutSeconds, [int]$CommandId = 0) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $target = Join-Path $OutputRoot $Name
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        foreach ($errorName in @('startup-error.json', 'watch-error.json', "command-$CommandId.error.json")) {
            $errorPath = Join-Path $OutputRoot $errorName
            if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
                throw "Harness error in ${errorName}: $([IO.File]::ReadAllText($errorPath))"
            }
        }
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            return ([IO.File]::ReadAllText($target) | ConvertFrom-Json -Depth 30)
        }
        if ($process.HasExited) { throw "Harness exited before $Name; exit code $($process.ExitCode)." }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Name. No process will be terminated or command replayed."
}

function Send-HarnessCommand([hashtable]$Command, [switch]$WithoutReceipt) {
    if ($process.HasExited) { throw 'The presentation harness has exited.' }
    $script:commandId++
    $id = $script:commandId
    $payload = @{ id = $id }
    foreach ($key in $Command.Keys) { $payload[$key] = $Command[$key] }
    Write-Trace "Command $id $($Command.action)"
    Write-AtomicJson (Join-Path $OutputRoot 'command.json') $payload
    if (-not $WithoutReceipt) {
        $receipt = Wait-HarnessReceipt "command-$id.done.json" $CommandTimeoutSeconds $id
        if ($receipt.action -ne $Command.action) { throw "Command receipt mismatch for $id." }
    }
    return $id
}

function Invoke-Assertions([string]$Action, [string]$ReceiptPrefix = 'assertions', [hashtable]$Options = @{}) {
    try {
        $command = @{ action = $Action }
        foreach ($key in $Options.Keys) { $command[$key] = $Options[$key] }
        $id = Send-HarnessCommand $command
        $receipt = Wait-HarnessReceipt "$ReceiptPrefix-$id.json" 10 $id
        if ($receipt.fixture -ne $true -or @($receipt.assertions).Count -eq 0) { throw "Empty or untrusted assertion result for $Action." }
        foreach ($assertion in @($receipt.assertions)) {
            $script:checks.Add([pscustomobject]@{ action = $Action; name = $assertion.name; passed = $assertion.passed })
            if ($assertion.passed -ne $true) {
                $script:failures.Add("${Action}: $($assertion.name). Full result: $ReceiptPrefix-$id.json")
            }
        }
    }
    catch { $script:failures.Add("${Action}: $($_.Exception.Message)"); Write-Trace $script:failures[$script:failures.Count - 1] }
}

function Set-Presentation([string]$Theme, [int]$Width = 1240, [int]$Height = 820, [int]$Zoom = 1) {
    $null = Send-HarnessCommand @{ action = 'theme'; value = $Theme }
    $null = Send-HarnessCommand @{ action = 'resize'; width = $Width; height = $Height }
    $null = Send-HarnessCommand @{ action = 'zoom'; factor = $Zoom }
    Start-Sleep -Milliseconds 300
}

function Save-Capture([string]$Name) {
    $id = Send-HarnessCommand @{ action = 'capture'; name = $Name }
    $receipt = Wait-HarnessReceipt "$Name.done.json" 10 $id
    $path = Join-Path $OutputRoot "$Name.internal.png"
    $signature = [byte[]]::new(8)
    $file = [IO.File]::OpenRead($path)
    try {
        if ($file.Read($signature, 0, 8) -ne 8 -or [Convert]::ToHexString($signature) -ne '89504E470D0A1A0A') { throw "Invalid PNG for $Name." }
    }
    finally { $file.Dispose() }
    if ($receipt.fixture -ne $true -or $receipt.dom.width -le 0 -or $receipt.dom.height -le 0) { throw "Invalid capture metadata for $Name." }
    $script:captures.Add([pscustomobject]@{ name = $Name; file = "$Name.internal.png"; width = $receipt.dom.width; height = $receipt.dom.height; theme = $receipt.theme })
    Write-Trace "Captured $Name"
}

try {
    Write-Trace 'Building the isolated presentation fixture; no production backend or AHK process is started.'
    Push-Location -LiteralPath $sourceRoot
    try {
        $buildLog = @(& $node 'scripts/build-harness.cjs' '--out' $HarnessRoot 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Presentation harness build failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }

    $start = [Diagnostics.ProcessStartInfo]::new($electron)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $sourceRoot
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @((Join-Path $HarnessRoot 'presentation-harness.cjs'), '--frontend', $FrontendRoot, '--output', $OutputRoot, '--scenarios', (Join-Path $HarnessRoot 'scenarios.js'))) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'The presentation fixture did not start.' }
    $started = $true
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $ready = Wait-HarnessReceipt 'ready.json' $StartupTimeoutSeconds
    if ($ready.fixture -ne $true -or $null -ne $ready.backend -or $ready.sandbox -ne $true -or $ready.nodeIntegration -ne $false) { throw 'The fixture did not report its isolated execution boundary.' }
    # ready.json precedes fs.watch installation in the fixture by a few statements.
    Start-Sleep -Milliseconds 500

    foreach ($theme in @('light', 'dark')) {
        Set-Presentation $theme
        Save-Capture "$theme-library-1240"
    }

    # runScenarios already includes the startInTray preference scenario.
    Invoke-Assertions 'assertions'
    Invoke-Assertions 'deleteScopes'

    foreach ($theme in @('light', 'dark')) {
        Set-Presentation $theme
        $null = Send-HarnessCommand @{ action = 'overview' }
        Save-Capture "$theme-selected-overview-1240"
        Invoke-Assertions 'hotkeyVisual' 'visual' @{ missing = $false }
        Invoke-Assertions 'checkPrimary' 'primary'
        Save-Capture "$theme-hotkeys-1240"
        Invoke-Assertions 'hotkeyVisual' 'visual' @{ missing = $true }
        Save-Capture "$theme-missing-file-1240"

        Set-Presentation $theme 740 820
        Invoke-Assertions 'hotkeyVisual' 'visual' @{ missing = $true }
        Save-Capture "$theme-narrow-740"

        Set-Presentation $theme 1240 820 2
        Invoke-Assertions 'focusRecovery' 'recovery'
        Save-Capture "$theme-zoom-200-missing-file"
    }

    # Preferences remain open for these two captures, after the script scenarios.
    Set-Presentation 'light'
    $null = Send-HarnessCommand @{ action = 'overview' }
    $null = Send-HarnessCommand @{ action = 'preferences' }
    Start-Sleep -Milliseconds 300
    Save-Capture 'light-preferences'
    $null = Send-HarnessCommand @{ action = 'theme'; value = 'dark' }
    Start-Sleep -Milliseconds 300
    Save-Capture 'dark-preferences'

    foreach ($theme in @('light', 'dark')) {
        Set-Presentation $theme
        Invoke-Assertions 'dataLocation' 'dataLocation'
        Save-Capture "$theme-data-location"
    }


    # Update history is independent of the production backend. Exercise the
    # normal page and cached rollback recovery in both themes and smaller views.
    foreach ($theme in @('light', 'dark')) {
        Set-Presentation $theme
        Invoke-Assertions 'updates' 'updates'
        Save-Capture "$theme-updates-1240"
        Invoke-Assertions 'updates' 'updates' @{ testChannel = $true }
        Save-Capture "$theme-updates-test-1240"
        Invoke-Assertions 'updates' 'updates' @{ recovery = $true }
        Save-Capture "$theme-updates-recovery-1240"

        Set-Presentation $theme 740 820
        Invoke-Assertions 'updates' 'updates'
        Save-Capture "$theme-updates-narrow-740"
        Invoke-Assertions 'updates' 'updates' @{ testChannel = $true }
        Save-Capture "$theme-updates-test-narrow-740"

        Set-Presentation $theme 1240 820 2
        Invoke-Assertions 'updates' 'updates' @{ recovery = $true }
        Save-Capture "$theme-updates-zoom-200"
    }

    foreach ($theme in @('light', 'dark')) {
        Set-Presentation $theme
        Invoke-Assertions 'help' 'help'
        Save-Capture "$theme-help-diagnostics-1240"
        Set-Presentation $theme 740 600
        Invoke-Assertions 'help' 'help'
        Save-Capture "$theme-help-diagnostics-narrow-740"
    }
}
catch { $script:failures.Add($_.Exception.Message); Write-Trace $_.Exception.Message }
finally {
    if ($started) {
        try {
            if (-not $process.HasExited) {
                if (Test-Path -LiteralPath $OutputRoot -PathType Container) { $null = Send-HarnessCommand @{ action = 'close' } -WithoutReceipt }
                if (-not $process.WaitForExit($ShutdownTimeoutSeconds * 1000)) {
                    $script:failures.Add('Graceful shutdown was not observed before the deadline. The harness was not killed.')
                }
            }
            if ($process.HasExited) {
                $exitCode = $process.ExitCode
                $shutdownConfirmed = $exitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $OutputRoot 'closed.json'))
                if (-not $shutdownConfirmed) { $script:failures.Add("Graceful shutdown receipt missing or exit code nonzero: $exitCode.") }
            }
        }
        catch { $script:failures.Add("Shutdown: $($_.Exception.Message)") }
    }

    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    [IO.File]::WriteAllLines((Join-Path $OutputRoot 'harness-build.log'), [string[]]$buildLog, $utf8)
    [IO.File]::WriteAllLines((Join-Path $OutputRoot 'orchestration.log'), $script:trace.ToArray(), $utf8)
    foreach ($entry in @(@{ name = 'electron.stdout.log'; task = $stdout }, @{ name = 'electron.stderr.log'; task = $stderr })) {
        if ($null -ne $entry.task) {
            try {
                if ($entry.task.Wait(5000)) { [IO.File]::WriteAllText((Join-Path $OutputRoot $entry.name), $entry.task.GetAwaiter().GetResult(), $utf8) }
                else { [IO.File]::WriteAllText((Join-Path $OutputRoot $entry.name), 'Output stream did not close; the process was not killed.', $utf8) }
            }
            catch { $script:failures.Add("Log collection: $($_.Exception.Message)") }
        }
    }
    Write-AtomicJson (Join-Path $OutputRoot 'presentation-summary.json') ([ordered]@{
        fixture = $true
        backend = $null
        checks = $script:checks.ToArray()
        captures = $script:captures.ToArray()
        failures = $script:failures.ToArray()
        shutdownConfirmed = $shutdownConfirmed
        exitCode = $exitCode
    })
    if ($null -ne $process) { $process.Dispose() }
}

if ($script:failures.Count -gt 0) { throw "Presentation checks failed ($($script:failures.Count)). See $OutputRoot/presentation-summary.json." }
Write-Host "Presentation checks passed: $($script:checks.Count) assertions, $($script:captures.Count) captures, graceful exit confirmed."
