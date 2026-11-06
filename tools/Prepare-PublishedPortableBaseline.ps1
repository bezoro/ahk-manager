#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$CandidateArchive,
    [Parameter(Mandatory = $true)][string]$CandidateManifest,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Prepare the published baseline only on a disposable Windows CI runner.' }
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute baseline output directory.' }
Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1')
$baselineCommit = '343553fd108220562883e1aa65c356d6cdcf7ee3'
$version = '1.6.2'
$expected = @(
    @{ name = 'AHK-Manager-1.6.2-Portable-x64.zip'; bytes = 234252955L; sha256 = 'bee6fedaf2420bb5e63c34a83063b08b9f8f8b4f2562f0b5cacdafc7c732ed5c' },
    @{ name = 'ahk-manager-portable-release.json'; bytes = 873L; sha256 = '24c4b58edb4e49413f1e2bcad24c5ad2e48d7a1cb7b927d72937138164044fa0' }
)
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
foreach ($file in $expected) {
    $target = Join-Path $OutputRoot $file.name
    Invoke-WebRequest -Uri ("https://github.com/bezoro/ahk-manager/releases/download/v$version/" + $file.name) -OutFile $target -MaximumRedirection 3
    if ((Get-Item -LiteralPath $target).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw 'The published portable baseline differs from its pinned bytes.' }
}
$baselineArchive = Join-Path $OutputRoot $expected[0].name
$baselineManifest = Join-Path $OutputRoot $expected[1].name
$null = Read-PortableReleaseManifest -ArchivePath $baselineArchive -ManifestPath $baselineManifest -ExpectedVersion $version -ExpectedCommit $baselineCommit
$null = Read-PortableReleaseManifest -ArchivePath $CandidateArchive -ManifestPath $CandidateManifest -ExpectedCommit $SourceCommit

function Copy-ArchivePolicy([string]$ArchivePath, [string]$TargetPath) {
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'App/resources/app-update.yml' })
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 65536) { throw 'The portable archive publisher policy is absent, duplicate or oversized.' }
        $inputStream = $entries[0].Open()
        $outputStream = [IO.File]::Open($TargetPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = [byte[]]::new(4096)
            $written = 0
            while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $written += $read
                if ($written -gt 65536 -or $written -gt $entries[0].Length) { throw 'The portable publisher policy exceeded its declared size.' }
                $outputStream.Write($buffer, 0, $read)
            }
            if ($written -ne $entries[0].Length) { throw 'The portable publisher policy was incomplete.' }
        }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
    finally { $archive.Dispose() }
}

$candidatePolicy = Join-Path $OutputRoot 'candidate-policy.yml'
$baselinePolicy = Join-Path $OutputRoot 'baseline-policy.yml'
Copy-ArchivePolicy $CandidateArchive $candidatePolicy
Copy-ArchivePolicy $baselineArchive $baselinePolicy
$repository = Split-Path -Parent $PSScriptRoot
& node (Join-Path $repository 'Presentation.Electron/scripts/portable-test-policy.mjs') --candidate $candidatePolicy --baseline $baselinePolicy --output (Join-Path $OutputRoot 'publisher-policy.json') --commit $SourceCommit --baseline-commit $baselineCommit
if ($LASTEXITCODE -ne 0) { throw 'The published baseline publisher policy could not be bound to both exact source identities.' }
[IO.File]::WriteAllText((Join-Path $OutputRoot 'published-baseline.json'), (@{ version = $version; commit = $baselineCommit; archive = $expected[0]; manifest = $expected[1]; payloadModified = $false } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host 'Prepared the exact published 1.6.2 portable baseline without modifying its payload.'
