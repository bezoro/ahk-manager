#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$FrontendRoot,
    [Parameter(Mandatory = $true)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($FrontendRoot)) { throw 'FrontendRoot must be absolute.' }
$path = Join-Path $FrontendRoot 'build-info.json'
$info = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
$info['version'] = $Version
$info['commit'] = $Commit.ToLowerInvariant()
$info['updateProtocol'] = 1
$temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
[IO.File]::WriteAllText($temporary, ($info | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
[IO.File]::Move($temporary, $path, $true)
