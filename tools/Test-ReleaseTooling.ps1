#requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Limits.psm1') -Force
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use an absolute, fresh test output directory.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$commit = 'a' * 40
$installer = Join-Path $OutputRoot 'AHK-Manager-1.2.99-Setup-x64.exe'
$manifestPath = Join-Path $OutputRoot 'ahk-manager-release.json'
$publisher = Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1'
$publicationStatus = Join-Path $PSScriptRoot 'Get-ReleasePublicationStatus.ps1'
$savedEnvironment = @{}
foreach ($name in @('GITHUB_ACTIONS','GITHUB_EVENT_NAME','GITHUB_REF','GITHUB_REPOSITORY','GITHUB_SHA','GITHUB_TOKEN')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$global:AhkReleaseToolingFixture = @{ Release = $null; Tag = $null; AnnotatedTags = @{}; TagCreateRaceCommit = $null; TagAfterReleaseGet = $null; PublicationRequest = $null; StatusCode = 0; FailManifestUpload = $false; Calls = [Collections.Generic.List[string]]::new() }

function Assert-Check([bool]$Value, [string]$Name) { if (-not $Value) { throw $Name }; $checks.Add($Name); Write-Host "PASS $Name" }
function New-FixtureGitTag([string]$Sha, [string]$Type = 'commit') { return [pscustomobject]@{ ref = 'refs/tags/v1.2.99'; object = [pscustomobject]@{ type = $Type; sha = $Sha } } }
function Throw-FixtureHttpStatus([int]$StatusCode) {
    $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]$StatusCode)
    throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Injected HTTP response.', $response)
}
function Invoke-RestMethod {
    param($Uri, $Method = 'Get', $Headers, $ContentType, $Body, $InFile)
    $fixture = $global:AhkReleaseToolingFixture
    $fixture.Calls.Add("$Method $Uri")
    if ($Method -eq 'Get' -and $Uri -eq 'https://api.github.com/repos/bezoro/ahk-manager/git/ref/tags/v1.2.99') {
        if ($null -eq $fixture.Tag) { Throw-FixtureHttpStatus 404 }
        return $fixture.Tag
    }
    if ($Method -eq 'Get' -and $Uri -match '^https://api.github.com/repos/bezoro/ahk-manager/git/tags/([0-9a-f]{40})$') {
        if (-not $fixture.AnnotatedTags.ContainsKey($Matches[1])) { Throw-FixtureHttpStatus 404 }
        return $fixture.AnnotatedTags[$Matches[1]]
    }
    if ($Method -eq 'Post' -and $Uri -eq 'https://api.github.com/repos/bezoro/ahk-manager/git/refs') {
        $request = $Body | ConvertFrom-Json
        if ($request.ref -cne 'refs/tags/v1.2.99' -or $request.sha -cne ('a' * 40)) { throw 'Unexpected immutable Git tag request.' }
        if ($null -ne $fixture.TagCreateRaceCommit) {
            $fixture.Tag = New-FixtureGitTag $fixture.TagCreateRaceCommit
            Throw-FixtureHttpStatus 422
        }
        if ($null -ne $fixture.Tag) { Throw-FixtureHttpStatus 422 }
        $fixture.Tag = New-FixtureGitTag $request.sha
        return $fixture.Tag
    }
    if ($Method -eq 'Get' -and $Uri -match '/tags/') {
        if ($fixture.StatusCode -ne 0) { Throw-FixtureHttpStatus $fixture.StatusCode }
        if ($null -ne $fixture.TagAfterReleaseGet) { $fixture.Tag = New-FixtureGitTag $fixture.TagAfterReleaseGet }
        return $fixture.Release
    }
    if ($Method -eq 'Post' -and $Uri -eq 'https://api.github.com/repos/bezoro/ahk-manager/releases') {
        $request = $Body | ConvertFrom-Json
        if (-not $request.draft -or $request.make_latest -ne 'false') { throw 'Release must initially remain a draft.' }
        $fixture.Release = [pscustomobject]@{ id = 41; tag_name = 'v1.2.99'; target_commitish = ('a' * 40); draft = $true; assets = @(); html_url = 'https://github.com/bezoro/ahk-manager/releases/tag/v1.2.99' }
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
        $request = $Body | ConvertFrom-Json
        if ($request.draft -isnot [bool] -or $request.draft -ne $false -or $request.make_latest -cne 'legacy') { throw 'Publication must preserve GitHub semantic-version latest selection.' }
        $fixture.PublicationRequest = $request
        $fixture.Release.draft = $false
        return $fixture.Release
    }
    if ($Method -eq 'Get') { return $fixture.Release }
    throw 'Unexpected mock API request. No network fallback is allowed.'
}

try {
    Write-Host 'Release tooling checks use in-process HTTP mocks. No GitHub release is created.'
    foreach ($boundary in @(@{ bytes = -1L; accepted = $false }, @{ bytes = 0L; accepted = $false }, @{ bytes = 1L; accepted = $true }, @{ bytes = 1GB; accepted = $true }, @{ bytes = 1GB + 1L; accepted = $false })) {
        Assert-Check ((Test-ReleaseInstallerLength $boundary.bytes) -eq $boundary.accepted) "shared installer length bound: $($boundary.bytes) bytes"
    }
    $frontend = Join-Path $OutputRoot 'frontend'
    [IO.Directory]::CreateDirectory($frontend) | Out-Null
    [IO.File]::WriteAllText((Join-Path $frontend 'build-info.json'), '{"versions":{"node":"fixture"},"build":"production"}')
    & (Join-Path $PSScriptRoot 'Set-ReleaseBuildInfo.ps1') -FrontendRoot $frontend -Version '1.2.99' -Commit $commit
    $info = Get-Content -LiteralPath (Join-Path $frontend 'build-info.json') -Raw | ConvertFrom-Json
    Assert-Check ($info.version -eq '1.2.99' -and $info.commit -eq $commit -and $info.updateProtocol -eq 1 -and $info.versions.node -eq 'fixture') 'build provenance preserves version commit protocol and existing fields'
    [IO.File]::WriteAllBytes($installer, [byte[]](1..255))
    & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -InstallerPath $installer -OutputPath $manifestPath -Version '1.2.99' -Commit $commit -Notes 'Teste: atualização e recuperação.'
    & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository 'bezoro/ahk-manager' -Commit $commit -ValidateOnly
    Assert-Check ($global:AhkReleaseToolingFixture.Calls.Count -eq 0) 'manifest validation checks SHA256 SHA512 and size without network access'
    $isoManifest = Join-Path $OutputRoot 'iso-notes/ahk-manager-release.json'
    & (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -InstallerPath $installer -OutputPath $isoManifest -Version '1.2.99' -Commit $commit -Notes '2026-10-28T00:00:00Z'
    & $publisher -InstallerPath $installer -ManifestPath $isoManifest -Repository 'bezoro/ahk-manager' -Commit $commit -ValidateOnly
    Assert-Check ($global:AhkReleaseToolingFixture.Calls.Count -eq 0) 'ISO-looking release notes retain their JSON string type'
    $original = [IO.File]::ReadAllBytes($installer)
    [IO.File]::WriteAllBytes($installer, [byte[]](1,2,3))
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository 'bezoro/ahk-manager' -Commit $commit -ValidateOnly } catch { $refused = $true }
    Assert-Check $refused 'modified installer is rejected before publication'
    [IO.File]::WriteAllBytes($installer, $original)

    $env:GITHUB_ACTIONS = 'true'; $env:GITHUB_EVENT_NAME = 'pull_request'; $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_REPOSITORY = 'bezoro/ahk-manager'; $env:GITHUB_SHA = $commit
    $env:GITHUB_TOKEN = 'fixture-only-not-a-credential'
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $global:AhkReleaseToolingFixture.Calls.Count -eq 0) 'pull requests cannot publish even with a supplied token'

    $fixture = $global:AhkReleaseToolingFixture
    $fixture.Release = [pscustomobject]@{ tag_name = 'v1.2.99'; target_commitish = $commit; draft = $false; assets = @([pscustomobject]@{ name = [IO.Path]::GetFileName($installer); state = 'uploaded' }, [pscustomobject]@{ name = 'ahk-manager-release.json'; state = 'uploaded' }) }
    $completeRelease = $fixture.Release | ConvertTo-Json -Depth 5
    $fixture.Tag = New-FixtureGitTag $commit
    $before = $fixture.Calls.Count
    $status = & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit
    Assert-Check ($status -ceq 'Published' -and $fixture.Calls.Count -eq $before + 3 -and $fixture.Calls[$before] -cmatch '^Get .*/git/ref/' -and $fixture.Calls[-1] -cmatch '^Get .*/git/ref/') 'published release status verifies the real Git ref identity and complete assets using only GET'

    $fixture.Release.draft = $true
    $fixture.Release.assets = @()
    $fixture.Tag = $null
    $before = $fixture.Calls.Count
    $status = & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit
    Assert-Check ($status -ceq 'Draft' -and $fixture.Calls.Count -eq $before + 2 -and $fixture.Calls[-1] -cmatch '^Get ' -and $fixture.Release.draft -and $fixture.Release.assets.Count -eq 0) 'incomplete draft with an absent ref remains resumable and is never changed by the status query'

    $identityRefused = $true
    foreach ($field in @('tag_name', 'target_commitish')) {
        $originalField = $fixture.Release.$field
        $fixture.Release.$field = if ($field -ceq 'tag_name') { 'v1.2.98' } else { 'b' * 40 }
        $before = $fixture.Calls.Count
        $refused = $false
        try { & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit } catch { $refused = $true }
        $identityRefused = $identityRefused -and $refused -and $fixture.Calls.Count -eq $before + 2 -and $fixture.Calls[-1] -cmatch '^Get '
        $fixture.Release.$field = $originalField
    }
    Assert-Check $identityRefused 'release status refuses both mismatched tag and commit without publication effects'

    $fixture.Release.draft = $false
    $fixture.Tag = New-FixtureGitTag $commit
    $assetsRefused = $true
    foreach ($assets in @(@(), @([pscustomobject]@{ name = [IO.Path]::GetFileName($installer); state = 'uploaded' }, [pscustomobject]@{ name = 'ahk-manager-release.json'; state = 'new' }), @([pscustomobject]@{ name = [IO.Path]::GetFileName($installer); state = 'uploaded' }, [pscustomobject]@{ name = 'unexpected-manifest.json'; state = 'uploaded' }))) {
        $fixture.Release.assets = $assets
        $before = $fixture.Calls.Count
        $refused = $false
        try { & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit } catch { $refused = $true }
        $assetsRefused = $assetsRefused -and $refused -and $fixture.Calls.Count -eq $before + 2 -and $fixture.Calls[-1] -cmatch '^Get '
    }
    Assert-Check $assetsRefused 'published release status refuses missing pending or wrongly named assets without mutation'

    $fixture.StatusCode = 404
    $before = $fixture.Calls.Count
    $status = & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit
    Assert-Check ($status -ceq 'Missing' -and $fixture.Calls.Count -eq $before + 2 -and $fixture.Calls[-1] -cmatch '^Get ') 'HTTP 404 reports a missing release without creating it'

    $fixture.StatusCode = 500
    $before = $fixture.Calls.Count
    $refused = $false
    try { & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $fixture.Calls.Count -eq $before + 2 -and $fixture.Calls[-1] -cmatch '^Get ') 'HTTP 500 fails the release status query instead of inventing a missing release'
    $fixture.StatusCode = 0

    $fixture.Release = $completeRelease | ConvertFrom-Json
    $fixture.Tag = New-FixtureGitTag ('b' * 40)
    $before = $fixture.Calls.Count
    $refused = $false
    try { & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $fixture.Calls.Count -eq $before + 1 -and $fixture.Calls[-1] -cmatch '^Get .*/git/ref/') 'published release metadata cannot hide a Git tag that points to another commit'

    $fixture.Tag = $null
    $before = $fixture.Calls.Count
    $refused = $false
    try { & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $fixture.Calls.Count -eq $before + 2 -and $fixture.Calls[-1] -cmatch '^Get ') 'published release with a deleted Git ref cannot bypass publication checks'

    $fixture.Tag = New-FixtureGitTag $commit
    $fixture.TagAfterReleaseGet = 'b' * 40
    $before = $fixture.Calls.Count
    $refused = $false
    try { & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $fixture.Calls.Count -eq $before + 3 -and @($fixture.Calls.GetRange($before, 3) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'Git tag changes during the status query cannot produce a published no-op decision'
    $fixture.TagAfterReleaseGet = $null

    $outerTag = '1' * 40
    $innerTag = '2' * 40
    $fixture.Tag = New-FixtureGitTag $outerTag 'tag'
    $fixture.AnnotatedTags = @{
        $outerTag = [pscustomobject]@{ sha = $outerTag; object = [pscustomobject]@{ type = 'tag'; sha = $innerTag } }
        $innerTag = [pscustomobject]@{ sha = $innerTag; object = [pscustomobject]@{ type = 'commit'; sha = $commit } }
    }
    $before = $fixture.Calls.Count
    $status = & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit
    Assert-Check ($status -ceq 'Published' -and $fixture.Calls.Count -eq $before + 7 -and @($fixture.Calls.GetRange($before, 7) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'annotated release tags are resolved and revalidated through bounded read-only requests'

    $annotationRefused = $true
    foreach ($problem in @('cycle', 'depth', 'wrong-object-type', 'wrong-annotated-sha')) {
        $fixture.Tag = New-FixtureGitTag $outerTag 'tag'
        $fixture.AnnotatedTags = @{}
        switch ($problem) {
            'cycle' { $fixture.AnnotatedTags[$outerTag] = [pscustomobject]@{ sha = $outerTag; object = [pscustomobject]@{ type = 'tag'; sha = $outerTag } } }
            'depth' {
                for ($index = 1; $index -le 10; $index++) {
                    $sha = $index.ToString('x').PadLeft(40, '0')
                    $next = ($index + 1).ToString('x').PadLeft(40, '0')
                    $fixture.AnnotatedTags[$sha] = [pscustomobject]@{ sha = $sha; object = [pscustomobject]@{ type = 'tag'; sha = $next } }
                }
                $fixture.Tag = New-FixtureGitTag ((1).ToString('x').PadLeft(40, '0')) 'tag'
            }
            'wrong-object-type' { $fixture.Tag = New-FixtureGitTag $commit 'tree' }
            'wrong-annotated-sha' { $fixture.AnnotatedTags[$outerTag] = [pscustomobject]@{ sha = $innerTag; object = [pscustomobject]@{ type = 'commit'; sha = $commit } } }
        }
        $before = $fixture.Calls.Count
        $refused = $false
        try { & $publicationStatus -Repository $env:GITHUB_REPOSITORY -Version '1.2.99' -Commit $commit } catch { $refused = $true }
        $requestCount = $fixture.Calls.Count - $before
        $annotationRefused = $annotationRefused -and $refused -and $requestCount -ge 1 -and $requestCount -le 10 -and @($fixture.Calls.GetRange($before, $requestCount) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0
    }
    Assert-Check $annotationRefused 'cyclic deep or malformed Git tag objects fail within a bounded read-only request budget'

    $fixture.Release = $null
    $fixture.Tag = $null
    $fixture.AnnotatedTags = @{}
    $fixture.Calls.Clear()

    $env:GITHUB_EVENT_NAME = 'push'
    $global:AhkReleaseToolingFixture.FailManifestUpload = $true
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $global:AhkReleaseToolingFixture.Release.draft -and $global:AhkReleaseToolingFixture.Release.assets.Count -eq 1 -and $global:AhkReleaseToolingFixture.Calls[-1] -notmatch '^Patch') 'interrupted upload leaves an unpublished draft'
    $global:AhkReleaseToolingFixture.FailManifestUpload = $false
    & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit
    Assert-Check (-not $fixture.Release.draft -and $fixture.Release.assets.Count -eq 2 -and $fixture.Calls[-1] -match '^Patch' -and $fixture.PublicationRequest.draft -eq $false -and $fixture.PublicationRequest.make_latest -ceq 'legacy') 'draft receives both assets before publication with GitHub semantic-version latest selection'
    $before = $global:AhkReleaseToolingFixture.Calls.Count
    & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit
    Assert-Check ($fixture.Calls.Count -eq $before + 3 -and @($fixture.Calls.GetRange($before, 3) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'identical published release verifies its real Git ref without mutation'
    $global:AhkReleaseToolingFixture.Release.assets[0].digest = 'sha256:' + ('0' * 64)
    $before = $global:AhkReleaseToolingFixture.Calls.Count
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    Assert-Check ($refused -and $fixture.Calls.Count -eq $before + 2 -and @($fixture.Calls.GetRange($before, 2) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'published assets with different hashes are never overwritten'

    $fixture.Release = $null
    $fixture.Tag = New-FixtureGitTag ('b' * 40)
    $before = $fixture.Calls.Count
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    $requestCount = $fixture.Calls.Count - $before
    Assert-Check ($refused -and $null -eq $fixture.Release -and $fixture.Tag.object.sha -ceq ('b' * 40) -and $requestCount -ge 1 -and @($fixture.Calls.GetRange($before, $requestCount) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'an existing Git tag on another commit cannot be published or overwritten'

    $fixture.Tag = $null
    $fixture.TagCreateRaceCommit = 'b' * 40
    $before = $fixture.Calls.Count
    $refused = $false
    try { & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit } catch { $refused = $true }
    $raceCalls = @($fixture.Calls.GetRange($before, $fixture.Calls.Count - $before))
    Assert-Check ($refused -and $null -eq $fixture.Release -and $fixture.Tag.object.sha -ceq ('b' * 40) -and @($raceCalls | Where-Object { $_ -match '^Post .*/git/refs$' }).Count -eq 1 -and @($raceCalls | Where-Object { $_ -match '^Post ' -and $_ -notmatch '/git/refs$' }).Count -eq 0 -and $raceCalls[-1] -match '^Get .*/git/ref/') 'concurrent tag creation with another commit is rechecked after HTTP 422 before any release is created'

    $fixture.Tag = $null
    $fixture.TagCreateRaceCommit = $commit
    $before = $fixture.Calls.Count
    & $publisher -InstallerPath $installer -ManifestPath $manifestPath -Repository $env:GITHUB_REPOSITORY -Commit $commit
    $raceCalls = @($fixture.Calls.GetRange($before, $fixture.Calls.Count - $before))
    Assert-Check (-not $fixture.Release.draft -and $fixture.Tag.object.sha -ceq $commit -and @($raceCalls | Where-Object { $_ -match '^Post .*/git/refs$' }).Count -eq 1 -and $raceCalls[-1] -match '^Patch ') 'concurrent creation of the exact same Git tag is verified and publication completes once'
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'summary.json'), (@{ passed = $true; checks = $checks.ToArray(); network = 'mocked'; published = $false } | ConvertTo-Json -Depth 4))
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    Remove-Variable AhkReleaseToolingFixture -Scope Global -ErrorAction SilentlyContinue
}
