#requires -Version 7.0
Set-StrictMode -Version Latest

function Invoke-AgentFixtureProcess {
    param([string]$Exe, [string[]]$Arguments, [int]$TimeoutSeconds = 25)
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    try {
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync($deadline.Token)
        $stderr = $process.StandardError.ReadToEndAsync($deadline.Token)
        try { $null = $process.WaitForExitAsync($deadline.Token).GetAwaiter().GetResult() }
        catch { throw "CLI result is unknown after $TimeoutSeconds seconds: $($Arguments[0])" }
        return [pscustomobject]@{
            Exit = $process.ExitCode
            Output = $stdout.GetAwaiter().GetResult().Trim()
            Error = $stderr.GetAwaiter().GetResult().Trim()
        }
    }
    finally { $deadline.Dispose(); $process.Dispose() }
}

function Test-PortableAgentCli {
    param([string]$Root, [string]$EvidenceRoot, [string]$Version)
    if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
        throw 'Agent CLI fixture commands require the disposable hosted Windows smoke session.'
    }
    $cli = Join-Path $Root 'App/backend/agent/ahk-manager.exe'
    $evidence = [Collections.Generic.List[object]]::new()
    $owned = $false
    function Call([string[]]$Arguments, [int]$Exit = 0, [string]$Code = '', [int]$TimeoutSeconds = 25) {
        $io = Invoke-AgentFixtureProcess -Exe $cli -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
        $row = [ordered]@{ command = $Arguments[0]; exit = $io.Exit; stderr = $io.Error; reply = $null }
        $evidence.Add($row)
        $reply = $io.Output | ConvertFrom-Json -AsHashtable
        $row.reply = $reply
        if ($io.Exit -ne $Exit -or $reply.protocol -ne 1 -or
            ($Exit -eq 0 -and $reply.outcome -cne 'Confirmed') -or
            ($Code -and $reply.code -cne $Code) -or
            $reply.version -cne $Version -or -not $reply.requestId) {
            throw "Unexpected Agent CLI result: $($Arguments[0]), exit $($io.Exit), $($reply.outcome)/$($reply.code)"
        }
        return $reply.result
    }

    try {
        $versionInfo = Call @('version') 0 'Ok'
        if ($versionInfo.clientVersion -cne $Version) { throw 'The packaged portable CLI version is incorrect.' }
        $inventory = @(Call @('commands'))
        foreach ($name in @('app.status', 'app.quit', 'app.stopScriptsAndQuit', 'app.cancelStopExit', 'scripts.list', 'collections.save')) {
            if ($name -cnotin $inventory.name) { throw "The packaged command catalog lacks $name." }
        }
        $private = Invoke-AgentFixtureProcess -Exe $cli -Arguments @('--server')
        $evidence.Add([ordered]@{ command = '--server'; exit = $private.Exit; stderr = $private.Error; reply = $private.Output })
        if ($private.Exit -ne 3 -or $private.Output) { throw 'An untrusted parent was able to start the private CLI sidecar.' }
        $null = Call @('app.status') 4 'HostUnavailable'

        # An identically named local pipe must receive no command bytes if the peer cannot be authenticated.
        $testCore = Join-Path $EvidenceRoot 'agent-test-core.dll'
        [IO.File]::Copy((Join-Path $Root 'App/backend/AhkManager.Core.dll'), $testCore, $false)
        $null = [Reflection.Assembly]::LoadFrom($testCore)
        $identity = [Bezoro.AhkManager.Core.Infrastructure.NativeProcessIdentity]::new($PID)
        try {
            $stamp = [Bezoro.AhkManager.Core.Infrastructure.ProcessStamp]::new(0, 0, '', '', 0)
            $access = [Bezoro.AhkManager.Core.Infrastructure.ProcessSecurity]::new($false, 0)
            if (-not $identity.TryRead([ref]$stamp) -or -not $identity.TryReadSecurity([ref]$access) -or -not $access.Elevated) {
                throw 'The fixture could not inspect its own Windows token.'
            }
        }
        finally { $identity.Dispose() }
        $key = @($Root.ToUpperInvariant(), $stamp.Sid, $stamp.Session, $access.IntegrityRid) -join [char]10
        $name = 'Bezoro.AhkManager.Agent.v1.' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
        $spoof = [IO.Pipes.NamedPipeServerStream]::new($name, 'InOut', 1, 'Byte', 'Asynchronous')
        $probeDeadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
        try {
            $connected = $spoof.WaitForConnectionAsync($probeDeadline.Token)
            $null = Call @('app.start') 3 'PeerUnverified'
            $null = $connected.GetAwaiter().GetResult()
            $buffer = [byte[]]::new(4)
            if ($spoof.ReadAsync($buffer, 0, $buffer.Length, $probeDeadline.Token).GetAwaiter().GetResult() -ne 0) {
                throw 'The CLI sent command data to an unauthenticated pipe.'
            }
        }
        finally { $probeDeadline.Dispose(); $spoof.Dispose() }

        $started = Call @('app.start') 0 'Ok' 65
        $owned = $true
        if (-not $started.ready -or $started.windowOpen -or $started.windowVisible) {
            throw 'The CLI did not start the closed application in a ready windowless state.'
        }
        $label = 'Inert portable CLI fixture'
        $script = Join-Path $EvidenceRoot 'agent-inert-fixture.ahk'
        [IO.File]::WriteAllText($script, '; Inert CI fixture. Registered only, never executed.')
        $null = Call @('collections.save', '--json', (@{ collectionId = $null; name = $label } | ConvertTo-Json -Compress))
        $null = Call @('scripts.addFiles', '--json', (@{ paths = @($script) } | ConvertTo-Json -Compress))
        $list = Call @('scripts.list')
        if (@($list.scripts).Count -ne 1 -or $list.scripts[0].path -ne $script) {
            throw 'The backend did not persist the isolated CLI script registration.'
        }
        $shown = Call @('app.show') 0 'Ok'
        if (-not $shown.windowOpen) { throw 'The Agent CLI did not create the window on request.' }
        $hidden = Call @('app.hide') 0 'Ok'
        if ($hidden.windowVisible) { throw 'The Agent CLI did not hide the window before tray exit.' }
        $owned = $false
        $null = Call @('app.quit') 0 'ShutdownConfirmed' 90

        $release = [Diagnostics.Stopwatch]::StartNew()
        do {
            $remaining = @(Get-CimInstance Win32_Process -Filter (
                "Name='ahk-manager.exe' OR Name='Bezoro.AhkManager.exe' OR " +
                "Name='AhkManager.Backend.exe' OR Name='AhkManager.UpdateHost.exe'"
            ) -OperationTimeoutSec 5 | Where-Object {
                [string]::IsNullOrWhiteSpace($_.ExecutablePath) -or
                $_.ExecutablePath.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
            })
            if ($remaining.Count) { Start-Sleep -Milliseconds 150 }
        } while ($remaining.Count -and $release.Elapsed.TotalSeconds -lt 10)
        if ($remaining.Count) { throw 'A packaged Manager process is still running in the fixture or its image could not be verified. It was preserved.' }

        $app = Join-Path $Root 'App'
        $probe = Join-Path $Root 'App-handoff-probe'
        $appEntry = Get-Item -LiteralPath $app -Force
        if (-not $appEntry.PSIsContainer -or ($appEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Test-Path -LiteralPath $probe)) {
            throw 'The isolated application is unavailable for the handoff check.'
        }
        [IO.Directory]::Move($app, $probe)
        try {
            foreach ($image in @('Bezoro.AhkManager.exe', 'backend/agent/ahk-manager.exe', 'backend/AhkManager.Backend.exe', 'backend/update/AhkManager.UpdateHost.exe')) {
                if (-not (Test-Path -LiteralPath (Join-Path $probe $image) -PathType Leaf)) {
                    throw "The handoff check did not move the packaged image: $image"
                }
            }
        }
        finally { [IO.Directory]::Move($probe, $app) }
        $evidence.Add([ordered]@{ command = 'verifyAppHandoff'; noFixtureProcesses = $true; appMovedAndRestored = $true })
        Write-Host 'Agent CLI verified: isolated peer refusal, windowless startup, persistent fixture commands, confirmed tray exit, and released application files.'
    }
    finally {
        if ($owned) {
            # Request normal cleanup only for a host whose authenticated start was confirmed.
            try { $null = Call @('app.quit') 0 'ShutdownConfirmed' 90 }
            catch { $evidence.Add([ordered]@{ command = 'cleanup'; error = $_.Exception.Message }) }
        }
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'agent-cli-evidence.json'), (ConvertTo-Json -InputObject @($evidence.ToArray()) -Depth 30))
    }
}

Export-ModuleMember -Function Test-PortableAgentCli
