#requires -Version 7.0
Set-StrictMode -Version Latest

function Invoke-PortableCdp {
    param($Session, [string]$Method, [hashtable]$Parameters = @{})
    $Session.Sequence++
    $id = $Session.Sequence
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{ id = $id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 15 -Compress))
    $deadline = [Threading.CancellationTokenSource]::new(10000)
    try {
        $Session.Socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $deadline.Token).GetAwaiter().GetResult() | Out-Null
        while ($true) {
            $stream = [IO.MemoryStream]::new()
            try {
                do {
                    $buffer = [byte[]]::new(65536)
                    $received = $Session.Socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $deadline.Token).GetAwaiter().GetResult()
                    if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'The portable app closed its debugging transport.' }
                    if ($stream.Length + $received.Count -gt 8MB) { throw 'The portable debugging response exceeded its limit.' }
                    $stream.Write($buffer, 0, $received.Count)
                } while (-not $received.EndOfMessage)
                $reply = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json -Depth 40
            }
            finally { $stream.Dispose() }
            if ($reply.PSObject.Properties['id'] -and $reply.id -eq $id) {
                if ($reply.PSObject.Properties['error']) { throw "Portable CDP failed: $($reply.error.message)" }
                return $reply.result
            }
        }
    }
    finally { $deadline.Dispose() }
}

function Invoke-PortableEvaluation {
    param($Session, [string]$Expression)
    $reply = Invoke-PortableCdp $Session 'Runtime.evaluate' @{ expression = $Expression; awaitPromise = $true; returnByValue = $true }
    if ($reply.PSObject.Properties['exceptionDetails']) { throw "Portable renderer evaluation failed: $($reply.exceptionDetails.text)" }
    if (-not $reply.result.PSObject.Properties['value']) { return $null }
    return $reply.result.value
}

function Start-PortableSession {
    param([string]$Root, [string]$EvidenceRoot, [switch]$ViaLauncher, [switch]$ExpectStartupRefusal)
    $application = Join-Path $Root 'App/Bezoro.AhkManager.exe'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $startedBefore = [DateTime]::UtcNow
    $image = if ($ViaLauncher) { Join-Path $Root 'Bezoro.AhkManager.exe' } else { $application }
    $start = [Diagnostics.ProcessStartInfo]::new($image)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Root
    $start.RedirectStandardOutput = -not $ViaLauncher
    $start.RedirectStandardError = -not $ViaLauncher
    foreach ($argument in @('--foreground', '--remote-debugging-address=127.0.0.1', "--remote-debugging-port=$port")) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($ViaLauncher) {
        try { if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'The portable root launcher did not dispatch normally.' } }
        finally { $process.Dispose() }
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $process = $null
        do {
            $browserProcesses = @(Get-CimInstance Win32_Process -Filter "Name='Bezoro.AhkManager.exe'" | Where-Object { $_.ExecutablePath -eq $application -and $_.CommandLine -match ('--remote-debugging-port=' + $port + '(?:\s|$)') -and $_.CommandLine -notmatch '(?:^|\s)--type=' })
            if ($browserProcesses.Count -eq 1) { try { $process = [Diagnostics.Process]::GetProcessById($browserProcesses[0].ProcessId); $null = $process.SafeHandle } catch [ArgumentException] { $process = $null } }
            if (-not $process) { Start-Sleep -Milliseconds 100 }
        } while (-not $process -and [DateTime]::UtcNow -lt $deadline)
        if (-not $process) { throw 'The root launcher did not create its exact portable application session.' }
    }
    $created = $process.StartTime.ToUniversalTime()
    $matches = [AhkManager.TestSupport.InstalledUpdaterUi]::Matches($process.Id, $application, $created)
    # Kernel process creation timestamps can precede precise UtcNow by a clock
    # tick. Identity still requires this retained PID, exact image and creation.
    $inCreationWindow = $created -ge $startedBefore.AddSeconds(-1) -and $created -le [DateTime]::UtcNow.AddSeconds(1)
    $identity = [ordered]@{ fixture = $true; pid = $process.Id; image = $application; launchRequestedAtUtc = $startedBefore.ToString('O'); processCreatedAtUtc = $created.ToString('O'); creationDeltaMilliseconds = ($created - $startedBefore).TotalMilliseconds; exactIdentityMatches = $matches; withinCreationWindow = $inCreationWindow }
    [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'session-identity.json'), ($identity | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    if (-not $inCreationWindow -or -not $matches) { throw 'The portable application session identity changed.' }
    $session = [pscustomobject]@{ Process = $process; Started = $created; Image = $application; Socket = $null; Sequence = 0; Capture = $null; Port = $port }
    if (-not $ViaLauncher) { $session.Capture = [AhkManager.TestSupport.InstalledUpdaterUi]::Capture($process, (Join-Path $EvidenceRoot 'manager.stdout.log'), (Join-Path $EvidenceRoot 'manager.stderr.log')) }
    if ($ExpectStartupRefusal) { return $session }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(2)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        $page = $null
        do {
            if ($process.HasExited) { throw "The portable application exited before readiness: $($process.ExitCode)." }
            try {
                $pages = $http.GetStringAsync("http://127.0.0.1:$port/json/list").GetAwaiter().GetResult() | ConvertFrom-Json
                $page = @($pages | Where-Object { $_.type -eq 'page' -and $_.url -like 'ahk-manager://*' }) | Select-Object -First 1
            }
            catch { $page = $null }
            if (-not $page) { Start-Sleep -Milliseconds 100 }
        } while (-not $page -and [DateTime]::UtcNow -lt $deadline)
        if (-not $page) { throw 'The real portable application did not expose its scoped page.' }
    }
    finally { $http.Dispose() }
    $address = [Uri]$page.webSocketDebuggerUrl
    if ($address.Scheme -ne 'ws' -or $address.Host -notin @('127.0.0.1', 'localhost') -or $address.Port -ne $port) { throw 'The portable debugging target is outside its loopback endpoint.' }
    $session.Socket = [Net.WebSockets.ClientWebSocket]::new()
    $connection = [Threading.CancellationTokenSource]::new(10000)
    try { $session.Socket.ConnectAsync($address, $connection.Token).GetAwaiter().GetResult() | Out-Null }
    finally { $connection.Dispose() }
    return $session
}

function Close-PortableSession {
    param($Session)
    if ($Session.Socket) { $Session.Socket.Dispose() }
    if (-not $Session.Process.HasExited) { $null = [AhkManager.TestSupport.InstalledUpdaterUi]::CloseAndWait($Session.Process.Id, $Session.Image, $Session.Started) }
    if (-not $Session.Process.WaitForExit(30000) -or $Session.Process.ExitCode -ne 0) { throw 'The owned portable application did not close normally.' }
    if ($Session.Capture) { $Session.Capture.Dispose() }
    $Session.Process.Dispose()
}

function Close-ReopenedPortableSession {
    param([string]$Root, [DateTime]$Earliest)
    $application = Join-Path $Root 'App/Bezoro.AhkManager.exe'
    $launcher = Join-Path $Root 'Bezoro.AhkManager.exe'
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $empty = 0
    do {
        $running = 0
        foreach ($candidate in @(Get-Process -Name 'Bezoro.AhkManager' -ErrorAction SilentlyContinue)) {
            try {
                try {
                    $null = $candidate.SafeHandle
                    if ($candidate.HasExited) { continue }
                    $created = $candidate.StartTime.ToUniversalTime()
                }
                catch [ArgumentException] { continue }
                catch [InvalidOperationException] { continue }
                catch [ComponentModel.Win32Exception] { if ($_.Exception.NativeErrorCode -eq 87) { continue }; throw }
                if ($created -lt $Earliest) { continue }
                if ([AhkManager.TestSupport.InstalledUpdaterUi]::Matches($candidate.Id, $launcher, $created)) { $running++; if (-not $candidate.WaitForExit(10000) -or $candidate.ExitCode -ne 0) { throw 'The reopened portable launcher did not dispatch normally.' } }
                elseif ([AhkManager.TestSupport.InstalledUpdaterUi]::Matches($candidate.Id, $application, $created)) { $running++; $null = [AhkManager.TestSupport.InstalledUpdaterUi]::CloseAndWait($candidate.Id, $application, $created) }
            }
            finally { $candidate.Dispose() }
        }
        if ($running -eq 0) { $empty++ } else { $empty = 0 }
        if ($empty -lt 3) { Start-Sleep -Milliseconds 250 }
    } while ($empty -lt 3 -and [DateTime]::UtcNow -lt $deadline)
    if ($empty -lt 3) { throw 'A reopened portable application remains active; it was preserved.' }
}

Export-ModuleMember -Function Invoke-PortableCdp, Invoke-PortableEvaluation, Start-PortableSession, Close-PortableSession, Close-ReopenedPortableSession
