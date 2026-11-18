#requires -Version 7.0
param([Parameter(Mandatory = $true)][long]$RunId)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TestReleaseContext.psm1') -Force
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_EVENT_NAME -ne 'workflow_run' -or $env:GITHUB_REF -ne 'refs/heads/main' -or $env:GITHUB_REPOSITORY -cne 'bezoro/ahk-manager' -or [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) { throw 'Preview discovery requires the trusted default-branch workflow_run job.' }
$repository = $env:GITHUB_REPOSITORY
$headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'Bezoro-AhkManager-Preview' }
$invoke = Get-Command Invoke-RestMethod
$request = { param($Uri) & $invoke -Uri $Uri -Headers $headers }.GetNewClosure()
try {
    Get-TestReleaseContext -Repository $repository -RunId $RunId -Request $request | Out-Null
    'eligible=true' >> $env:GITHUB_OUTPUT
}
catch {
    if ($_.Exception.Data['AhkTestIneligible'] -ne $true) { throw }
    Write-Host "Run $($RunId) is ineligible for preview publication. $($_.Exception.Message)"
    'eligible=false' >> $env:GITHUB_OUTPUT
}
