#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Limits.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Release-GitTags.psm1') -Force -DisableNameChecking
if (-not [IO.Path]::IsPathFullyQualified($InstallerPath) -or -not [IO.Path]::IsPathFullyQualified($ManifestPath)) { throw 'Release paths must be absolute.' }
if ([IO.Path]::GetFileName($ManifestPath) -cne 'ahk-manager-release.json') { throw 'Unexpected release manifest name.' }
$manifestFile = Get-Item -LiteralPath $ManifestPath
if ($manifestFile.Length -gt 65536) { throw 'Release manifest exceeds the limit.' }
$document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($ManifestPath))
try {
    $root = $document.RootElement
    $fileInfo = $root.GetProperty('installer')
    if ((@($root.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'commit,installer,notes,publishedAt,schemaVersion,updateProtocol,version') { throw 'Unexpected manifest fields.' }
    if ((@($fileInfo.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'bytes,name,sha256,sha512') { throw 'Unexpected installer fields.' }
    # JsonDocument preserves timestamp and notes as strings; ConvertFrom-Json
    # versions can otherwise convert ISO-looking strings into DateTime objects.
    $manifest = @{
        schemaVersion = $root.GetProperty('schemaVersion').GetInt32()
        updateProtocol = $root.GetProperty('updateProtocol').GetInt32()
        version = $root.GetProperty('version').GetString()
        commit = $root.GetProperty('commit').GetString()
        publishedAt = $root.GetProperty('publishedAt').GetString()
        notes = $root.GetProperty('notes').GetString()
        installer = @{
            name = $fileInfo.GetProperty('name').GetString()
            sha256 = $fileInfo.GetProperty('sha256').GetString()
            sha512 = $fileInfo.GetProperty('sha512').GetString()
            bytes = $fileInfo.GetProperty('bytes').GetInt64()
        }
    }
}
finally { $document.Dispose() }
if ($manifest.schemaVersion -ne 1 -or $manifest.updateProtocol -ne 1 -or $manifest.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or $manifest.commit -cne $Commit.ToLowerInvariant() -or $manifest.notes -isnot [string] -or $manifest.notes.Length -gt 4000 -or $manifest.notes.Contains([char]0)) { throw 'Invalid release identity.' }
$date = [DateTimeOffset]::MinValue
if ($manifest.publishedAt -isnot [string] -or $manifest.publishedAt -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$' -or -not [DateTimeOffset]::TryParse($manifest.publishedAt, [ref]$date)) { throw 'Invalid publication timestamp.' }
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.PSIsContainer -or $installer.Name -cne $manifest.installer.name -or $installer.Name -cne "AHK-Manager-$($manifest.version)-Setup-x64.exe" -or $installer.Length -ne $manifest.installer.bytes -or -not (Test-ReleaseInstallerLength $installer.Length)) { throw 'Release installer identity or size mismatch; installers must be at most 1 GiB.' }
$sha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sha512 = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA512).Hash))
if ($manifest.installer.sha256 -cne $sha256 -or $manifest.installer.sha512 -cne $sha512) { throw 'Release installer hash mismatch.' }
if ($ValidateOnly) { Write-Host "Release $($manifest.version) validated; no network or publication performed."; return }

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_EVENT_NAME -ne 'push' -or $env:GITHUB_REF -ne 'refs/heads/main' -or $env:GITHUB_REPOSITORY -cne $Repository -or $env:GITHUB_SHA -cne $Commit) { throw 'Publication requires the approved main-branch GitHub Actions job.' }
if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { throw 'The release job token is missing.' }
$headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'Bezoro-AhkManager-Release' }
$invoke = Get-Command Invoke-RestMethod
$request = {
    param($Method, $Uri, $Body)
    $parameters = @{ Method = $Method; Uri = $Uri; Headers = $headers }
    if ($null -ne $Body) { $parameters.ContentType = 'application/json'; $parameters.Body = $Body }
    & $invoke @parameters
}.GetNewClosure()
$base = "https://api.github.com/repos/$Repository/releases"
$tag = 'v' + $manifest.version
$release = $null
$alreadyPublished = $false
try { $release = Invoke-RestMethod -Uri "$base/tags/$tag" -Headers $headers }
catch {
    if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
}
if ($null -ne $release) {
    # Published releases are never patched, replaced, deleted or re-uploaded.
    if ($release.target_commitish -cne $Commit -or $release.tag_name -cne $tag) { throw 'Existing draft belongs to a different commit.' }
    $alreadyPublished = -not $release.draft
    if ($alreadyPublished) {
        $actual = Get-ReleaseGitTagCommit -Repository $Repository -TagName $tag -Request $request
        if ($null -eq $actual -or $actual -cne $Commit) { throw 'The published release tag no longer identifies the approved commit.' }
    }
    else { $null = Ensure-ReleaseGitTag -Repository $Repository -TagName $tag -Commit $Commit -Request $request }
}
else {
    $null = Ensure-ReleaseGitTag -Repository $Repository -TagName $tag -Commit $Commit -Request $request
    $body = @{ tag_name = $tag; target_commitish = $Commit; name = "AHK Manager $($manifest.version)"; body = $manifest.notes; draft = $true; prerelease = $false; make_latest = 'false' } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri $base -Headers $headers -ContentType 'application/json' -Body $body
}

foreach ($file in @($installer, $manifestFile)) {
    $existing = @($release.assets | Where-Object name -CEQ $file.Name)
    if ($existing.Count -gt 0) {
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing.Count -ne 1 -or $existing[0].size -ne $file.Length -or $existing[0].state -cne 'uploaded' -or $null -eq $existing[0].PSObject.Properties['digest'] -or $existing[0].digest -cne $digest) { throw "Existing asset $($file.Name) differs from this build. Immutable release assets are never overwritten." }
        continue
    }
    if ($alreadyPublished) { throw 'An immutable published release is incomplete. No asset will be added or changed.' }
    $upload = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($file.Name))"
    $contentType = if ($file.Extension -ceq '.json') { 'application/json' } else { 'application/octet-stream' }
    $asset = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType $contentType -InFile $file.FullName
    if ($asset.name -cne $file.Name -or $asset.size -ne $file.Length -or $asset.state -cne 'uploaded') { throw 'GitHub did not confirm the complete asset upload; the release remains a draft.' }
}
$actual = Get-ReleaseGitTagCommit -Repository $Repository -TagName $tag -Request $request
if ($null -eq $actual -or $actual -cne $Commit) { throw 'The release tag changed before publication; no release will be published.' }
if ($alreadyPublished) { Write-Host "Release $tag is already published with identical assets; no changes were made."; return }
$release = Invoke-RestMethod -Uri "$base/$($release.id)" -Headers $headers
foreach ($name in @($installer.Name, $manifestFile.Name)) {
    if (@($release.assets | Where-Object { $_.name -ceq $name -and $_.state -ceq 'uploaded' }).Count -ne 1) { throw 'The release is missing an uploaded asset and remains a draft.' }
}
if (-not $release.draft) { Write-Host "Release $tag was published concurrently; verified assets were preserved without further changes."; return }
$actual = Get-ReleaseGitTagCommit -Repository $Repository -TagName $tag -Request $request
if ($null -eq $actual -or $actual -cne $Commit) { throw 'The release tag changed before publication; the release remains a draft.' }
$published = Invoke-RestMethod -Method Patch -Uri "$base/$($release.id)" -Headers $headers -ContentType 'application/json' -Body (@{ draft = $false; make_latest = 'legacy' } | ConvertTo-Json)
if ($published.draft) { throw 'GitHub did not confirm release publication.' }
Write-Host "Published $($published.html_url) with its manifest and installer."
