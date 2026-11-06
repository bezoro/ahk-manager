#requires -Version 7.0
param([Parameter(Mandatory = $true)][long]$RunId)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
Import-Module (Join-Path $PSScriptRoot 'TestReleaseContext.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'TestReleaseArtifacts.psm1') -Force
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_EVENT_NAME -ne 'workflow_run' -or $env:GITHUB_REF -ne 'refs/heads/main' -or $env:GITHUB_REPOSITORY -cne 'bezoro/ahk-manager' -or [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) { throw 'Test publication requires the trusted default-branch workflow_run job.' }
$repository = $env:GITHUB_REPOSITORY
$headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'Bezoro-AhkManager-Preview' }
$request = { param($Uri) Invoke-RestMethod -Uri $Uri -Headers $headers }.GetNewClosure()
$context = Get-TestReleaseContext -Repository $repository -RunId $RunId -Request $request
$directory = Join-Path $env:RUNNER_TEMP ('ahk-preview-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
try {
    $archive = Join-Path $directory 'portable.zip'
    # Invoke-WebRequest drops Authorization on cross-origin redirects by default.
    # No credentials are preserved for GitHub's temporary artifact download URL.
    try { Invoke-WebRequest -Uri "https://api.github.com/repos/$repository/actions/artifacts/$($context.ArtifactId)/zip" -Headers $headers -OutFile $archive -MaximumRedirection 3 | Out-Null }
    catch { throw 'The approved preview artifact could not be downloaded.' }
    if ((Get-Item -LiteralPath $archive).Length -ne $context.ArtifactBytes -or ('sha256:' + (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()) -cne $context.ArtifactDigest) { throw 'The preview ZIP does not match the artifact digest approved by GitHub Actions.' }
    $identity = Read-TestReleaseArtifactIdentity -ArchivePath $archive
    $suffix = "-test.$($context.RunNumber).$($context.Attempt).$($context.PullRequest)"
    if ((Get-ReleaseVersionChannel $identity.Version) -cne 'test' -or -not $identity.Version.EndsWith($suffix, [StringComparison]::Ordinal) -or $identity.Commit -cne $context.Commit) { throw 'The preview version or source does not match its CI run.' }
    # Keep the version calculated at build time. Bootstrap publication can add a
    # stable tag before this backfill runs; rebuilding would change the identity.
    $root = Join-Path $directory 'package'
    Expand-TestReleaseArtifact -ArchivePath $archive -OutputRoot $root -Version $identity.Version
    $family = @{
        PortableArchivePath = Join-Path $root "AHK-Manager-$($identity.Version)-Portable-x64.zip"
        PortableManifestPath = Join-Path $root 'ahk-manager-portable-release.json'
        Repository = $repository
        Commit = $context.Commit
    }
    & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') @family -ValidateOnly
    $status = & (Join-Path $PSScriptRoot 'Get-ReleasePublicationStatus.ps1') -Repository $repository -Version $identity.Version -Commit $context.Commit -PortableArchivePath $family.PortableArchivePath -PortableManifestPath $family.PortableManifestPath
    if ($status -ceq 'Published') { Write-Host "Preview v$($identity.Version) is already published; its assets were preserved."; return }
    $portableSigned = & (Join-Path $PSScriptRoot 'Sign-PortableRelease.ps1') -ArchivePath $family.PortableArchivePath -ManifestPath $family.PortableManifestPath
    if ($portableSigned) { $family.PortableSignaturePath = Join-Path $root 'ahk-manager-portable-release.json.p7s' }
    & (Join-Path $PSScriptRoot 'Publish-GitHubRelease.ps1') @family -Channel test -TestRunId $RunId
}
finally { Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue }
