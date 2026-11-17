# Loaded by PortableAgentCli.psm1.
function Test-PortableMcp {
    param([string]$Root, [string]$EvidenceRoot, [string]$Version, [string]$PipeName, [string]$Interpreter)
    if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
        $PipeName -cnotmatch '\ABezoro\.AhkManager\.Agent\.v1\.[0-9A-F]{64}\z') {
        throw 'The MCP fixture requires the verified disposable hosted Windows smoke session.'
    }

    $cli = Join-Path $Root 'App/backend/agent/ahk-manager.exe'
    $scriptPath = Join-Path $EvidenceRoot ('mcp-' + [Guid]::NewGuid().ToString('N') + '.ahk')
    $evidence = [ordered]@{
        protocolVersion = $null; tools = @(); calls = [Collections.Generic.List[object]]::new()
        rejectedPeerReceivedBytes = $null; scriptPid = $null; scriptExitObserved = $false
        mcpExitCode = $null; appMovedAndRestored = $false; stderr = $null; failure = $null; cleanupFailure = $null
    }
    $process = $stderr = $scriptProcess = $null
    $ownedHost = $scriptMayRun = $stopSent = $false
    $session = @{ nextId = 0 }
    $scriptId = $null

    function Send-McpMessage([hashtable]$Message) {
        $Message.jsonrpc = '2.0'
        $process.StandardInput.WriteLine(($Message | ConvertTo-Json -Compress -Depth 20))
        $process.StandardInput.Flush()
    }

    function Read-McpReply([string]$Method, [hashtable]$Parameters, [int]$TimeoutSeconds = 25) {
        $id = ++$session.nextId
        Send-McpMessage @{ id = $id; method = $Method; params = $Parameters }
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
        try { $line = $process.StandardOutput.ReadLineAsync($deadline.Token).GetAwaiter().GetResult() }
        catch { throw "No MCP reply for $Method within $TimeoutSeconds seconds. Inspect the fixture before retrying an effect." }
        finally { $deadline.Dispose() }
        if ($null -eq $line) { throw "MCP closed stdout before replying to $Method." }
        $reply = $line | ConvertFrom-Json -AsHashtable
        if ($reply.jsonrpc -cne '2.0' -or $reply.id -ne $id -or ($reply.Contains('result') -eq $reply.Contains('error'))) {
            throw "Invalid JSON-RPC reply for $Method."
        }
        return $reply
    }

    function Invoke-McpTool([string]$Name, [hashtable]$Arguments = @{}, [string]$Code = 'Ok', [string]$Outcome = 'Confirmed', [int]$TimeoutSeconds = 25) {
        $message = Read-McpReply 'tools/call' @{ name = $Name; arguments = $Arguments } $TimeoutSeconds
        if ($message.Contains('error')) { throw "MCP rejected tool $Name with JSON-RPC code $($message.error.code)." }
        $call = $message.result
        $response = $call.structuredContent
        $evidence.calls.Add([ordered]@{ tool = $Name; outcome = $response.outcome; code = $response.code; requestId = $response.requestId })
        if ($response.protocol -ne 1 -or $response.version -cne $Version -or -not $response.requestId -or
            $response.outcome -cne $Outcome -or $response.code -cne $Code -or
            [bool]$call.isError -ne ($Outcome -cne 'Confirmed') -or $call.content.Count -ne 1 -or
            $call.content[0].type -cne 'text') {
            throw "Unexpected MCP result for $Name, $($response.outcome)/$($response.code)."
        }
        $text = $call.content[0].text | ConvertFrom-Json -AsHashtable
        if ($text.requestId -cne $response.requestId -or $text.outcome -cne $response.outcome -or $text.code -cne $response.code) {
            throw "MCP text and structured replies differ for $Name."
        }
        return $response.result
    }

    function Invoke-FixtureCli([string[]]$CommandLine, [string]$Code = 'Ok', [int]$TimeoutSeconds = 25) {
        $io = Invoke-AgentFixtureProcess -Exe $cli -Arguments $CommandLine -TimeoutSeconds $TimeoutSeconds
        $reply = $io.Output | ConvertFrom-Json -AsHashtable
        if ($io.Exit -ne 0 -or $reply.protocol -ne 1 -or $reply.version -cne $Version -or
            $reply.outcome -cne 'Confirmed' -or $reply.code -cne $Code -or -not $reply.requestId) {
            throw "Fixture CLI command $($CommandLine[0]) failed, exit $($io.Exit), $($reply.outcome)/$($reply.code)."
        }
        return $reply.result
    }

    function Get-McpFixtureRow($Catalog) {
        $rows = @($Catalog.scripts | Where-Object { $_.path -eq $scriptPath })
        if ($rows.Count -ne 1 -or ($scriptId -and $rows[0].id -cne $scriptId)) {
            throw 'The command did not return the exact registered MCP fixture script.'
        }
        return $rows[0]
    }

    function Get-McpFixtureProcess($Row) {
        $identity = $Row.process.identity
        if ($Row.status -cne 'Running' -or $Row.process.origin -cne 'Manager' -or
            $identity.scriptPath -ne $scriptPath -or $identity.executablePath -ne $Interpreter -or
            $identity.scriptHash -ne $scriptHash -or $identity.interpreterHash -ne $interpreterHash) {
            throw 'The MCP fixture script process identity could not be verified.'
        }
        $handle = [Diagnostics.Process]::GetProcessById([int]$identity.pid)
        try {
            $null = $handle.SafeHandle
            $created = [DateTime]::FromFileTimeUtc([long]$identity.creationFileTime)
            $ui = [AhkManager.TestSupport.PortableUpdaterUi]
            if ($ui::CreationTime($handle) -ne $created -or -not $ui::Matches($handle.Id, $Interpreter, $created)) {
                throw 'The MCP fixture process was replaced before verification.'
            }
            return $handle
        }
        catch { $handle.Dispose(); throw }
    }

    try {
        $start = [Diagnostics.ProcessStartInfo]::new($cli)
        $start.WorkingDirectory = [IO.Path]::GetDirectoryName($cli)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $start.RedirectStandardOutput = $start.RedirectStandardError = $true
        $start.StandardInputEncoding = $start.StandardOutputEncoding = $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
        $start.ArgumentList.Add('mcp')
        $process = [Diagnostics.Process]::Start($start)
        $stderr = $process.StandardError.ReadToEndAsync()

        $initial = Read-McpReply 'initialize' @{
            protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'portable-ci'; version = '1' }
        }
        if ($initial.result.protocolVersion -cne '2025-06-18' -or $initial.result.serverInfo.version -cne $Version -or
            -not $initial.result.capabilities.Contains('tools')) { throw 'The packaged MCP initialization reply is invalid.' }
        $evidence.protocolVersion = $initial.result.protocolVersion
        Send-McpMessage @{ method = 'notifications/initialized' }
        $listed = Read-McpReply 'tools/list' @{}
        $tools = @($listed.result.tools)
        $evidence.tools = @($tools.name)
        foreach ($name in @('app_start', 'app_quit', 'scripts_list', 'scripts_addFiles', 'scripts_operate', 'commands_preview', 'operations_get')) {
            $match = @($tools | Where-Object { $_.name -ceq $name })
            if ($match.Count -ne 1 -or $match[0].inputSchema.type -cne 'object' -or $match[0].outputSchema.type -cne 'object') {
                throw "Missing or invalid MCP tool schema: $name."
            }
        }
        $published = @(Invoke-FixtureCli @('commands') | Where-Object { $_.surface -ceq 'management' })
        if ($published.Count -ne $tools.Count) { throw 'The published CLI and MCP management catalogs differ.' }
        foreach ($command in $published) {
            $name = $command.name.Replace('.', '_')
            $tool = @($tools | Where-Object { $_.name -ceq $name })
            if ($tool.Count -ne 1 -or
                ($tool[0].inputSchema | ConvertTo-Json -Compress -Depth 40) -cne
                ($command.inputSchema | ConvertTo-Json -Compress -Depth 40)) {
                throw "The CLI and MCP argument schemas differ for $($command.name)."
            }
        }
        $addFiles = $tools | Where-Object { $_.name -ceq 'scripts_addFiles' }
        $pathPatterns = @($addFiles.inputSchema.properties.paths.items.anyOf.pattern)
        if (-not ($pathPatterns | Where-Object { 'C:\Scripts\Demo.ahk' -cmatch $_ }) -or
            -not ($pathPatterns | Where-Object { '\\server\share\Demo.ahk' -cmatch $_ }) -or
            ($pathPatterns | Where-Object { 'Scripts\Demo.ahk' -cmatch $_ })) {
            throw 'The packaged schema altered its Windows path patterns.'
        }
        $unknown = Read-McpReply 'tools/call' @{ name = '--server'; arguments = @{} }
        if ($unknown.error.code -ne -32602) { throw 'MCP accepted a private CLI dispatch as a tool.' }
        $null = Invoke-McpTool 'scripts_list' @{} 'HostUnavailable' 'Failed'

        $spoof = [IO.Pipes.NamedPipeServerStream]::new($PipeName, 'InOut', 1, 'Byte', 'Asynchronous')
        $probe = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
        try {
            $connected = $spoof.WaitForConnectionAsync($probe.Token)
            $null = Invoke-McpTool 'app_start' @{} 'PeerUnverified' 'Failed' 15
            $null = $connected.GetAwaiter().GetResult()
            $buffer = [byte[]]::new(4)
            $evidence.rejectedPeerReceivedBytes = $spoof.ReadAsync($buffer, 0, $buffer.Length, $probe.Token).GetAwaiter().GetResult()
            if ($evidence.rejectedPeerReceivedBytes -ne 0) { throw 'MCP sent command bytes to an unauthenticated peer.' }
        }
        finally { $probe.Dispose(); $spoof.Dispose() }

        $hostState = Invoke-McpTool 'app_start' @{} 'Ok' 'Confirmed' 65
        $ownedHost = $true
        if (-not $hostState.ready -or $hostState.windowOpen -or $hostState.windowVisible) {
            throw 'MCP did not start the authenticated Manager headlessly.'
        }
        $existing = Invoke-McpTool 'scripts_list'
        if (@($existing.scripts).Count -ne 1 -or $existing.scripts[0].path -ne (Join-Path $EvidenceRoot 'agent-inert-fixture.ahk')) {
            throw 'MCP did not list the persisted CLI fixture script.'
        }
        $null = Invoke-McpTool 'operations_get' @{ operationId = [Guid]::NewGuid().ToString() } 'OperationNotFound' 'Failed'

        # The pinned interpreter runs code generated only for this disposable test.
        [IO.File]::WriteAllText($scriptPath, "#Requires AutoHotkey v2.0`n#SingleInstance Off`n#NoTrayIcon`nPersistent`n")
        $scriptHash = (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash
        $interpreterHash = (Get-FileHash -LiteralPath $Interpreter -Algorithm SHA256).Hash
        $null = Invoke-McpTool 'scripts_addFiles' @{ paths = @($scriptPath) } 'None'
        $catalog = Invoke-FixtureCli @('scripts.list')
        $row = Get-McpFixtureRow $catalog
        $scriptId = $row.id
        $metadata = @{
            friendlyName = 'Inert portable MCP fixture'; description = ''; collectionId = $null
            runAtLogin = $false; runAsAdministrator = $true; launchBlocked = $false; manualHotkeys = @()
        }
        $null = Invoke-McpTool 'scripts_saveMetadata' @{ scriptId = $scriptId; revision = $catalog.revision; metadata = $metadata } 'None'
        $catalog = Invoke-McpTool 'scripts_list'
        $row = Get-McpFixtureRow $catalog
        if ($row.status -cne 'Stopped' -or $row.runAtLogin -or -not $row.runAsAdministrator) {
            throw 'The MCP fixture script was not registered as stopped, elevated, and excluded from login.'
        }
        $target = @{ scriptId = $scriptId; revision = $catalog.revision; operation = 'Start' }
        $scriptMayRun = $true
        $null = Invoke-McpTool 'scripts_operate' $target 'ConfirmationRequired' 'Failed'
        $row = Get-McpFixtureRow (Invoke-FixtureCli @('scripts.list'))
        if ($row.status -cne 'Stopped' -or $null -ne $row.process) { throw 'An unconfirmed MCP start changed the script state.' }
        $scriptMayRun = $false

        $reviewRequest = @{ command = 'scripts.operate'; arguments = $target } | ConvertTo-Json -Compress -Depth 6
        $review = Invoke-FixtureCli @('commands.preview', '--json', $reviewRequest)
        $scriptMayRun = $true
        $null = Invoke-McpTool 'scripts_operate' ($target + @{ confirmationToken = $review.confirmationToken }) 'None'
        $catalog = Invoke-McpTool 'scripts_list'
        $scriptProcess = Get-McpFixtureProcess (Get-McpFixtureRow $catalog)
        $evidence.scriptPid = $scriptProcess.Id
        $target = @{ scriptId = $scriptId; revision = $catalog.revision; operation = 'Stop' }
        $review = Invoke-McpTool 'commands_preview' @{ command = 'scripts.operate'; arguments = $target }
        $stopSent = $true
        $null = Invoke-McpTool 'scripts_operate' ($target + @{ confirmationToken = $review.confirmationToken }) 'None'
        $row = Get-McpFixtureRow (Invoke-FixtureCli @('scripts.list'))
        if (-not $scriptProcess.WaitForExit(5000) -or $row.status -cne 'Stopped' -or $null -ne $row.process) {
            throw 'The confirmed MCP stop did not release the original fixture process.'
        }
        $scriptMayRun = $false
        $evidence.scriptExitObserved = $true
        $ownedHost = $false
        $null = Invoke-McpTool 'app_quit' @{} 'ShutdownConfirmed' 'Confirmed' 90
    }
    catch { $evidence.failure = $_.Exception.Message }
    finally {
        try {
            if ($process) {
                $process.StandardInput.Close()
                if (-not $process.WaitForExit(10000)) { throw 'The MCP process did not exit after stdin closed.' }
                $evidence.mcpExitCode = $process.ExitCode
                $evidence.stderr = $stderr.GetAwaiter().GetResult()
                if ($evidence.mcpExitCode -ne 0 -or $process.StandardOutput.ReadToEnd() -ne '') {
                    throw 'MCP did not finish with exit zero and a clean stdout stream.'
                }
            }
        }
        catch { if (-not $evidence.failure) { $evidence.failure = $_.Exception.Message } }
        if ($ownedHost) {
            try {
                if ($scriptMayRun) {
                    $current = Invoke-FixtureCli @('scripts.list')
                    $row = Get-McpFixtureRow $current
                    if ($row.status -cne 'Stopped' -or $null -ne $row.process) {
                        if ($stopSent) { throw 'Stop was dispatched but its result is uncertain. The fixture was preserved.' }
                        $owned = Get-McpFixtureProcess $row
                        try {
                            $target = @{ scriptId = $scriptId; revision = $current.revision; operation = 'Stop' }
                            $reviewRequest = @{ command = 'scripts.operate'; arguments = $target } | ConvertTo-Json -Compress -Depth 6
                            $review = Invoke-FixtureCli @('commands.preview', '--json', $reviewRequest)
                            $stopRequest = ($target + @{ confirmationToken = $review.confirmationToken }) | ConvertTo-Json -Compress
                            $null = Invoke-FixtureCli @('scripts.operate', '--json', $stopRequest) 'None'
                            if (-not $owned.WaitForExit(5000)) { throw 'Normal fixture script cleanup was not observed.' }
                        }
                        finally { $owned.Dispose() }
                    }
                }
                $null = Invoke-FixtureCli @('app.quit') 'ShutdownConfirmed' 90
            }
            catch { $evidence.cleanupFailure = $_.Exception.Message }
        }
        if ($scriptProcess) { $scriptProcess.Dispose() }
        if ($process) { $process.Dispose() }
        if (-not $evidence.failure -and -not $evidence.cleanupFailure) {
            try {
                Assert-PortableAgentHandoff $Root
                $evidence.appMovedAndRestored = $true
            }
            catch { $evidence.failure = $_.Exception.Message }
        }
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'agent-mcp-evidence.json'), ($evidence | ConvertTo-Json -Depth 10))
    }
    if ($evidence.failure -or $evidence.cleanupFailure) {
        throw "Portable MCP smoke failed. $($evidence.failure) $($evidence.cleanupFailure)"
    }
    Write-Host 'MCP verified: offline schemas, peer refusal, headless startup, shared CLI catalog and confirmation, exact script exit, normal Manager shutdown, and released application files.'
}
