#requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use an absolute, fresh test output directory.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$commit = 'a' * 40
$installer = Join-Path $OutputRoot 'AHK-Manager-1.2.99-Setup-x64.exe'
$manifestPath = Join-Path $OutputRoot 'ahk-manager-release.json'
$publisher = Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1'
$savedEnvironment = @{}
foreach ($name in @('GITHUB_ACTIONS','GITHUB_EVENT_NAME','GITHUB_REF','GITHUB_REPOSITORY','GITHUB_SHA','GITHUB_TOKEN')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$global:AhkReleaseToolingFixture = @{ Release = $null; FailManifestUpload = $false; Calls = [Collections.Generic.List[string]]::new() }

function Assert-Check([bool]$Value, [string]$Name) { if (-not $Value) { throw $Name }; $checks.Add($Name); Write-Host "PASS $Name" }
function Invoke-RestMethod {
    param($Uri, $Method = 'Get', $Headers, $ContentType, $Body, $InFile)
    $fixture = $global:AhkReleaseToolingFixture
    $fixture.Calls.Add("$Method $Uri")
    if ($Method -eq 'Get' -and $Uri -match '/tags/') { return $fixture.Release }
    if ($Method -eq 'Post' -and $Uri -eq 'https://api.github.com/repos/bezoro/Bezoro.AhkManager/releases') {
        $request = $Body | ConvertFrom-Json
        if (-not $request.draft -or $request.make_latest -ne 'false') { throw 'Release must initially remain a draft.' }
        $fixture.Release = [pscustomobject]@{ id = 41; tag_name = 'v1.2.99'; target_commitish = ('a' * 40); draft = $true; assets = @(); html_url = 'https://github.com/bezoro/Bezoro.AhkManager/releases/tag/v1.2.99' }
        return $fixture.Release
    }
    if ($Method -eq 'Post' -and $Uri -match '^https://uploads.github.com/') {
        if (-not $fixture.Release.draft) { throw 'Assets must not be uploaded after publication.' }
        $file = Get-Item -LiteralPath $InFile
        if ($fixture.FailManifestUpload -and $file.Extension -ceq '.json') { throw 'Injected manifest upload failure.' }
        $asset = [pscustomobject]@{ name = $file.Name; size = $file.Length; state = 'uploaded'; digest = ('sha256:' + (Get-FileHash -LiteralPath $InFile -Algorithm SHA256).Hash.ToLowerInvariant()) }
        $fixture.Release.assets += $asset
        return $asset
    }
    if ($Method -eq 'Patch') {
        if ($fixture.Release.assets.Count -ne 2) { throw 'Both release assets must exist before publication.' }
        $fixture.Release.draft = $false
        return $fixture.Release
    }
    if ($Method -eq 'Get') { return $fixture.Release }
    throw 'Unexpected mock API request. No network fallback is allowed.'
}

try {
    Write-Host 'Release tooling checks use in-process HTTP mocks. No GitHub release is created.'
    $frontend = Join-Path $OutputRoot 'frontend'
    [IO.Directory]::CreateDirectory($frontend) | Out-Null
    [IO.File]::WriteAllText((Join-Path $frontend 'build-info.json'), '{"versions":{"node":"fixture"},"build":"production"}')
    & (Join-Path $PSScriptRoot 'Set-ReleaseBuildInfo.ps1') -FrontendRoot $frontend -Version '1.2.99' -Commit $commit
    $info = Get-Content -LiteralPath (Join-Path $frontend 'build-info.json') -Raw | ConvertFrom-Json
    Assert-Check ($info.version -eq '1.2.99' -and $info.commit -eq $commit -and $info.updateProtocol -eq 1 -and $info.versions.node -eq 'fixture') 'build provenance preserves version commit protocol and existing fields'
    [IO.File]::WriteAllBytes($installer, [byte[]](1..255))
    & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -InstallerPath $installer -OutputPath $manifestPath -Version '1.2.99' -Commit $commit -Notes 'Teste: atualização e recuperação.'
    & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository 'bezoro/Bezoro.AhkManager' -Commit $commit -ValidateOnly
    Assert-Check ($global:AhkReleaseToolingFixture.Calls.Count -eq 0) 'manifest validation checks SHA256 SHA512 and size without network access'
    $isoManifest = Join-Path $OutputRoot 'iso-notes/ahk-manager-release.json'
    & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -InstallerPath $installer -OutputPath $isoManifest -Version '1.2.99' -Commit $commit -Notes '2026-10-28T00:00:00Z'
    & $publisher -InstallerPath $installer -ManifestPath $isoManifest -Repository 'bezoro/Bezoro.AhkManager' -Commit $commit -ValidateOnly
    Assert-Check ($global:AhkReleaseToolingFixture.Calls.Count -eq 0) 'ISO-looking release notes retain their JSON string type'
    $original = [IO.File]::ReadAllBytes($installer)
    [IO.File]::WriteAllBytes($installer, [byte[]](1,2,3))
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository 'bezoro/Bezoro.AhkManager' -Commit $commit -ValidateOnly } catch { $refused = $true }
    Assert-Check $refused 'modified installer is rejected before publication'
    [IO.File]::WriteAllBytes($installer, $original)

    $env:GITHUB_ACTIONS = 'true'; $env:GITHUB_EVENT_NAME = 'pull_request'; $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_REPOSITORY = 'bezoro/Bezoro.AhkManager'; $env:GITHUB_SHA = $commit
    $env:GITHUB_TOKEN = 'fixture-only-not-a-credential'
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $global:AhkReleaseToolingFixture.Calls.Count -eq 0) 'pull requests cannot publish even with a supplied token'
    $env:GITHUB_EVENT_NAME = 'push'
    $global:AhkReleaseToolingFixture.FailManifestUpload = $true
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $global:AhkReleaseToolingFixture.Release.draft -and $global:AhkReleaseToolingFixture.Release.assets.Count -eq 1 -and $global:AhkReleaseToolingFixture.Calls[-1] -notmatch '^Patch') 'interrupted upload leaves an unpublished draft'
    $global:AhkReleaseToolingFixture.FailManifestUpload = $false
    & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit
    Assert-Check (-not $global:AhkReleaseToolingFixture.Release.draft -and $global:AhkReleaseToolingFixture.Release.assets.Count -eq 2 -and $global:AhkReleaseToolingFixture.Calls[-1] -match '^Patch') 'draft receives both assets before becoming the latest public release'
    $before = $global:AhkReleaseToolingFixture.Calls.Count
    & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit
    Assert-Check ($global:AhkReleaseToolingFixture.Calls.Count -eq $before + 1) 'identical published release is verified without mutation'
    $global:AhkReleaseToolingFixture.Release.assets[0].digest = 'sha256:' + ('0' * 64)
    $before = $global:AhkReleaseToolingFixture.Calls.Count
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $global:AhkReleaseToolingFixture.Calls.Count -eq $before + 1) 'published assets with different hashes are never overwritten'
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'summary.json'), (@{ passed = $true; checks = $checks.ToArray(); network = 'mocked'; published = $false } | ConvertTo-Json -Depth 4))
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    Remove-Variable AhkReleaseToolingFixture -Scope Global -ErrorAction SilentlyContinue
}
