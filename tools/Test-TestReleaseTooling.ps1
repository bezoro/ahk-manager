#requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
Import-Module (Join-Path $PSScriptRoot 'TestReleaseContext.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'TestReleaseArtifacts.psm1') -Force
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute preview test directory.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Assert-Preview([bool]$Value, [string]$Name) { if (-not $Value) { throw $Name }; $checks.Add($Name); Write-Host "PASS $Name" }
function Assert-Refused([scriptblock]$Action, [string]$Name) { $refused = $false; try { & $Action | Out-Null } catch { $refused = $true }; Assert-Preview $refused $Name }
$version = '1.3.0-test.20.1.2'
foreach ($accepted in @('1.2.16', $version, '2147483647.0.0-test.2147483647.1.1')) { Assert-Preview (Test-ReleaseVersion $accepted) "bounded release identity accepted: $accepted" }
foreach ($rejected in @('1.3.0-beta.1', '1.3.0-test.0.1.2', '1.3.0-test.01.1.2', '1.3.0-test.20.1.2+metadata', '2147483648.0.0', '1.3.0-test.2147483648.1.2', "1.3.0-test.20.1.2`n")) { Assert-Preview (-not (Test-ReleaseVersion $rejected)) 'noncanonical or unbounded release identity refused' }

$repository = [pscustomobject]@{ full_name = 'bezoro/ahk-manager'; id = 1430333503 }
$run = [pscustomobject]@{ id = 7L; status = 'completed'; conclusion = 'success'; event = 'pull_request'; name = 'Windows CI'; path = '.github/workflows/windows-ci.yml'; repository = $repository; head_repository = $repository; head_sha = ('a' * 40); run_number = 20; run_attempt = 1; head_branch = 'feature' }
$pull = [pscustomobject]@{ number = 2; state = 'open'; head = [pscustomobject]@{ sha = $run.head_sha; repo = $repository }; base = [pscustomobject]@{ ref = 'main'; repo = $repository } }
$artifact = [pscustomobject]@{ id = 9L; name = 'windows-installer-test-1'; expired = $false; size_in_bytes = 1000L; digest = ('sha256:' + ('b' * 64)); workflow_run = [pscustomobject]@{ id = 7L; head_sha = $run.head_sha; head_repository_id = $repository.id } }
$base = 'https://api.github.com/repos/bezoro/ahk-manager'
$responses = @{
    "$base/actions/runs/7" = $run
    "$base/commits/$($run.head_sha)/pulls?per_page=100" = @($pull)
    "$base/pulls/2" = $pull
    "$base/actions/runs/7/artifacts?per_page=100" = [pscustomobject]@{ total_count = 1; artifacts = @($artifact) }
}
$request = { param($Uri) if (-not $responses.ContainsKey($Uri)) { throw 'Unexpected mock request. No network fallback is allowed.' }; return $responses[$Uri] }.GetNewClosure()
$context = Get-TestReleaseContext -Repository $repository.full_name -RunId 7 -Request $request
Assert-Preview ($context.Commit -ceq $run.head_sha -and $context.PullRequest -eq 2 -and $context.ArtifactId -eq 9 -and $context.RunNumber -eq 20 -and $context.Attempt -eq 1) 'successful current same-repository PR and exact artifact are admitted'

foreach ($case in @(
    @{ object = $run; field = 'conclusion'; value = 'failure'; name = 'failed CI run' },
    @{ object = $run; field = 'event'; value = 'pull_request_target'; name = 'privileged PR event' },
    @{ object = $run; field = 'path'; value = '.github/workflows/unrelated.yml'; name = 'unrelated workflow' },
    @{ object = $run; field = 'head_repository'; value = [pscustomobject]@{ id = 1; full_name = 'fork/ahk-manager' }; name = 'fork source' },
    @{ object = $run; field = 'head_repository'; value = $null; name = 'deleted fork source' },
    @{ object = $pull; field = 'state'; value = 'closed'; name = 'closed PR' },
    @{ object = $pull.head; field = 'sha'; value = ('c' * 40); name = 'stale PR head' },
    @{ object = $artifact; field = 'expired'; value = $true; name = 'expired artifact' },
    @{ object = $artifact; field = 'name'; value = 'windows-installer-test-2'; name = 'another run attempt artifact' },
    @{ object = $artifact; field = 'size_in_bytes'; value = (1GB + 1MB + 1L); name = 'oversized archive' },
    @{ object = $artifact.workflow_run; field = 'head_sha'; value = ('c' * 40); name = 'artifact from another source' },
    @{ object = $artifact; field = 'digest'; value = 'sha256:invalid'; name = 'missing artifact integrity' }
)) {
    $original = $case.object.($case.field)
    try {
        $case.object.($case.field) = $case.value
        Assert-Refused { Get-TestReleaseContext -Repository $repository.full_name -RunId 7 -Request $request } "preview provenance rejects $($case.name)"
    }
    finally { $case.object.($case.field) = $original }
}
$selected = @(Get-TestReleaseCandidates -Repository $repository.full_name -RunId 7 -Request $request)
Assert-Preview ($selected.Count -eq 1 -and $selected[0] -eq 7) 'successful PR trigger selects its current tested build'
$run.head_repository = [pscustomobject]@{ id = 1; full_name = 'fork/ahk-manager' }
$selected = @(Get-TestReleaseCandidates -Repository $repository.full_name -RunId 7 -Request $request)
Assert-Preview ($selected.Count -eq 0) 'successful fork CI is normally excluded without failing the publisher'
$run.head_repository = $repository
$mainRun = $run | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$mainRun.id = 8; $mainRun.event = 'push'; $mainRun.head_branch = 'main'
$responses["$base/actions/runs/8"] = $mainRun
$deletedForkRun = $run | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$deletedForkRun.head_repository = $null
$responses["$base/actions/workflows/windows-ci.yml/runs?event=pull_request&status=success&per_page=100"] = [pscustomobject]@{ workflow_runs = @($deletedForkRun, $run, $run) }
$selected = @(Get-TestReleaseCandidates -Repository $repository.full_name -RunId 8 -Request $request)
Assert-Preview ($selected.Count -eq 1 -and $selected[0] -eq 7) 'bootstrap main trigger backfills current PR builds and deduplicates them'
$pull.state = 'closed'
$selected = @(Get-TestReleaseCandidates -Repository $repository.full_name -RunId 8 -Request $request)
Assert-Preview ($selected.Count -eq 0) 'bootstrap backfill skips PRs closed since their CI completed'
$pull.state = 'open'

$installerName = "AHK-Manager-$version-Setup-x64.exe"
$installer = Join-Path $OutputRoot $installerName
[IO.File]::WriteAllBytes($installer, [byte[]](1..255))
$manifestPath = Join-Path $OutputRoot 'ahk-manager-release.json'
& (Join-Path $PSScriptRoot 'New-ReleaseManifest.ps1') -InstallerPath $installer -OutputPath $manifestPath -Version $version -Commit $run.head_sha -Notes 'Prévia do canal de testes.'
$manifestText = [IO.File]::ReadAllText($manifestPath)
$entries = @(@{ name = $installerName; value = [IO.File]::ReadAllBytes($installer) }, @{ name = 'ahk-manager-release.json'; value = [Text.Encoding]::UTF8.GetBytes($manifestText) }, @{ name = 'SHA256.txt'; value = [Text.Encoding]::UTF8.GetBytes('fixture') })
function New-PreviewZip([string]$Name, [object[]]$Entries) {
    $path = Join-Path $OutputRoot ($Name + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $Entries) {
            $entry = $archive.CreateEntry($item.name)
            $stream = $entry.Open()
            try { $stream.Write($item.value, 0, $item.value.Length) } finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return $path
}
$validZip = New-PreviewZip 'valid' $entries
$identity = Read-TestReleaseArtifactIdentity $validZip
$extracted = Join-Path $OutputRoot 'extracted'
Expand-TestReleaseArtifact -ArchivePath $validZip -OutputRoot $extracted -Version $version
& (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') -InstallerPath (Join-Path $extracted $installerName) -ManifestPath (Join-Path $extracted 'ahk-manager-release.json') -Repository $repository.full_name -Commit $run.head_sha -ValidateOnly
Assert-Preview ($identity.Version -ceq $version -and $identity.Commit -ceq $run.head_sha -and (Get-FileHash (Join-Path $extracted $installerName)).Hash -ceq (Get-FileHash $installer).Hash) 'preview manifest and extracted executable retain their exact hash and source identity without execution'
foreach ($name in @('../escape.exe', '/absolute.exe', 'folder/installer.exe', 'SHA256.TXT')) {
    $bad = @($entries[0], $entries[1], @{ name = $name; value = [byte[]](1) })
    $zip = New-PreviewZip ('unsafe-' + [Guid]::NewGuid().ToString('N')) $bad
    Assert-Refused { Expand-TestReleaseArtifact -ArchivePath $zip -OutputRoot (Join-Path $OutputRoot ([Guid]::NewGuid().ToString('N'))) -Version $version } 'preview ZIP refuses traversal, absolute, nested or case-conflicting names'
}
$duplicate = New-PreviewZip 'duplicate' @($entries[0], $entries[1], $entries[1])
Assert-Refused { Read-TestReleaseArtifactIdentity $duplicate } 'duplicate manifests are rejected before extraction'
$duplicateFields = $manifestText.Replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1')
$zip = New-PreviewZip 'duplicate-fields' @($entries[0], @{ name = 'ahk-manager-release.json'; value = [Text.Encoding]::UTF8.GetBytes($duplicateFields) }, $entries[2])
Assert-Refused { Read-TestReleaseArtifactIdentity $zip } 'duplicate JSON identity fields are rejected'

# Exercise the actual publisher, including its last provenance check, with no
# network fallback and no executable launched from the fixture artifact.
$global:AhkPreviewPublisherFixture = @{ Responses = $responses; Release = $null; Tag = $null; Calls = [Collections.Generic.List[string]]::new(); Created = $null; Published = $null; ChangeHead = $false }
function Invoke-RestMethod {
    param($Uri, $Method = 'Get', $Headers, $ContentType, $Body, $InFile)
    $fixture = $global:AhkPreviewPublisherFixture
    $fixture.Calls.Add("$Method $Uri")
    if ($Method -eq 'Get' -and $fixture.Responses.ContainsKey($Uri)) { return $fixture.Responses[$Uri] }
    if ($Method -eq 'Get' -and $Uri -match '/git/ref/tags/') {
        if ($null -ne $fixture.Tag) { return $fixture.Tag }
        $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound)
        throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Mock 404.', $response)
    }
    if ($Method -eq 'Post' -and $Uri.EndsWith('/git/refs')) {
        $bodyObject = $Body | ConvertFrom-Json
        $fixture.Tag = [pscustomobject]@{ ref = $bodyObject.ref; object = [pscustomobject]@{ sha = $bodyObject.sha; type = 'commit' } }
        return $fixture.Tag
    }
    if ($Method -eq 'Get' -and $Uri -match '/releases/tags/') {
        if ($null -ne $fixture.Release) { return $fixture.Release }
        $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound)
        throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Mock 404.', $response)
    }
    if ($Method -eq 'Post' -and $Uri.EndsWith('/releases')) {
        $fixture.Created = $Body | ConvertFrom-Json
        $fixture.Release = [pscustomobject]@{ id = 41; tag_name = $fixture.Created.tag_name; target_commitish = $fixture.Created.target_commitish; prerelease = $fixture.Created.prerelease; draft = $true; assets = @(); html_url = 'https://github.com/bezoro/ahk-manager/releases/tag/fixture' }
        return $fixture.Release
    }
    if ($Method -eq 'Post' -and $Uri.StartsWith('https://uploads.github.com/')) {
        $file = Get-Item -LiteralPath $InFile
        $asset = [pscustomobject]@{ name = $file.Name; size = $file.Length; state = 'uploaded'; digest = ('sha256:' + (Get-FileHash -LiteralPath $InFile -Algorithm SHA256).Hash.ToLowerInvariant()) }
        $fixture.Release.assets += $asset
        return $asset
    }
    if ($Method -eq 'Get' -and $Uri.EndsWith('/releases/41')) {
        if ($fixture.ChangeHead) { $fixture.Responses['https://api.github.com/repos/bezoro/ahk-manager/pulls/2'].head.sha = 'c' * 40 }
        return $fixture.Release
    }
    if ($Method -eq 'Patch' -and $Uri.EndsWith('/releases/41')) {
        $fixture.Published = $Body | ConvertFrom-Json
        $fixture.Release.draft = $fixture.Published.draft
        return $fixture.Release
    }
    throw 'Unexpected mock API route; no network fallback is allowed.'
}
$saved = @{}
foreach ($name in @('GITHUB_ACTIONS', 'GITHUB_EVENT_NAME', 'GITHUB_REF', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'GITHUB_TOKEN')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:GITHUB_ACTIONS = 'true'; $env:GITHUB_EVENT_NAME = 'workflow_run'; $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_REPOSITORY = 'bezoro/ahk-manager'; $env:GITHUB_SHA = 'b' * 40; $env:GITHUB_TOKEN = 'fixture-only'
    $parameters = @{ InstallerPath = $installer; ManifestPath = $manifestPath; Repository = $env:GITHUB_REPOSITORY; Commit = $run.head_sha; Channel = 'test'; TestRunId = 7 }
    & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') @parameters
    $fixture = $global:AhkPreviewPublisherFixture
    Assert-Preview ($fixture.Created.prerelease -eq $true -and $fixture.Created.make_latest -ceq 'false' -and $fixture.Published.prerelease -eq $true -and $fixture.Published.make_latest -ceq 'false' -and $fixture.Release.draft -eq $false) 'production preview publisher creates a prerelease and never changes stable Latest'
    $before = $fixture.Calls.Count
    & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') @parameters
    Assert-Preview (@($fixture.Calls.GetRange($before, $fixture.Calls.Count - $before) | Where-Object { $_ -notmatch '^Get ' }).Count -eq 0) 'identical published preview is an immutable read-only no-op'
    $fixture.Release.prerelease = $false
    Assert-Refused { & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') @parameters } 'a stable-marked release cannot be reused as a preview'
    $fixture.Release.prerelease = $true
    $fixture.Release.assets[0].digest = 'sha256:' + ('0' * 64)
    Assert-Refused { & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') @parameters } 'published preview assets with different bytes are never overwritten'
    $fixture.Release = $null; $fixture.Tag = $null; $fixture.Published = $null; $fixture.ChangeHead = $true
    Assert-Refused { & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') @parameters } 'PR head changes during upload prevent the final publication'
    Assert-Preview ($null -eq $fixture.Published -and $fixture.Release.draft) 'a stale preview remains a draft and never reaches the test channel'
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    Remove-Variable AhkPreviewPublisherFixture -Scope Global -ErrorAction SilentlyContinue
}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'summary.json'), (@{ passed = $true; checks = $checks.ToArray(); network = 'mocked'; published = $false; executedInstaller = $false } | ConvertTo-Json -Depth 4))
