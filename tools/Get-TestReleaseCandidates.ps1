#requires -Version 7.0
param([Parameter(Mandatory = $true)][long]$TriggerRunId)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TestReleaseContext.psm1') -Force
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_EVENT_NAME -ne 'workflow_run' -or $env:GITHUB_REF -ne 'refs/heads/main' -or $env:GITHUB_REPOSITORY -cne 'bezoro/ahk-manager' -or [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) { throw 'Preview discovery requires the trusted default-branch workflow_run job.' }
$repository = $env:GITHUB_REPOSITORY
$headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'Bezoro-AhkManager-Preview' }
$request = { param($Uri) Invoke-RestMethod -Uri $Uri -Headers $headers }.GetNewClosure()
$selected = @(Get-TestReleaseCandidates -Repository $repository -RunId $TriggerRunId -Request $request)
$json = ConvertTo-Json -InputObject @($selected) -Compress
if ($env:GITHUB_OUTPUT) { "runs=$json" >> $env:GITHUB_OUTPUT }
Write-Host "Eligible preview runs: $json"
