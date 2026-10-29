#requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')

function Test-ReleaseTagArguments {
    param([string]$Repository, [string]$TagName)
    if ($Repository -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$' -or -not $TagName.StartsWith('v') -or -not (Test-ReleaseVersion $TagName.Substring(1))) {
        throw 'A canonical GitHub repository and release SemVer tag are required.'
    }
}

function Get-ReleaseTagHttpStatus {
    param([System.Management.Automation.ErrorRecord]$Record)
    $response = $Record.Exception.PSObject.Properties['Response']
    if ($null -ne $response -and $null -ne $response.Value) {
        $status = $response.Value.PSObject.Properties['StatusCode']
        if ($null -ne $status -and $null -ne $status.Value) { return [int]$status.Value }
    }
    $status = $Record.Exception.PSObject.Properties['StatusCode']
    if ($null -ne $status -and $null -ne $status.Value) { return [int]$status.Value }
    return $null
}

# The caller supplies HTTP explicitly. This module never resolves credentials or
# follows API-provided URLs, and isolated tests can supply a network-free callback.
function Get-ReleaseGitTagCommit {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$TagName,
        [Parameter(Mandatory = $true)][scriptblock]$Request
    )
    Test-ReleaseTagArguments $Repository $TagName
    $base = "https://api.github.com/repos/$Repository/git"
    try { $reference = & $Request 'Get' "$base/ref/tags/$TagName" $null }
    catch {
        if ((Get-ReleaseTagHttpStatus $_) -eq 404) { return $null }
        throw
    }
    if ($null -eq $reference -or $reference.ref -isnot [string] -or $reference.ref -cne "refs/tags/$TagName") { throw 'GitHub did not return the requested tag reference.' }
    $target = $reference.object
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $depth = 0
    while ($true) {
        if ($null -eq $target -or $target.type -isnot [string] -or $target.sha -isnot [string] -or $target.sha -cnotmatch '^[0-9a-f]{40}$') { throw 'GitHub returned an invalid tag target.' }
        if ($target.type -ceq 'commit') { return $target.sha }
        if ($target.type -cne 'tag' -or $depth -ge 8 -or -not $seen.Add($target.sha)) { throw 'The release tag does not resolve to a commit within the annotation limit.' }
        $sha = $target.sha
        $annotation = & $Request 'Get' "$base/tags/$sha" $null
        if ($null -eq $annotation -or $annotation.sha -isnot [string] -or $annotation.sha -cne $sha) { throw 'GitHub returned a different annotated tag object.' }
        $target = $annotation.object
        $depth++
    }
}

function Ensure-ReleaseGitTag {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$TagName,
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
        [Parameter(Mandatory = $true)][scriptblock]$Request
    )
    Test-ReleaseTagArguments $Repository $TagName
    $actual = Get-ReleaseGitTagCommit -Repository $Repository -TagName $TagName -Request $Request
    if ($null -ne $actual) {
        if ($actual -cne $Commit) { throw 'The immutable release tag already identifies another commit.' }
        return $actual
    }
    # GitHub creates this ref only if absent. A concurrent reservation returns
    # 422; rereading the ref must prove the same commit before publication.
    $body = @{ ref = "refs/tags/$TagName"; sha = $Commit } | ConvertTo-Json -Compress
    try { $created = & $Request 'Post' "https://api.github.com/repos/$Repository/git/refs" $body }
    catch {
        if ((Get-ReleaseTagHttpStatus $_) -ne 422) { throw }
    }
    $actual = Get-ReleaseGitTagCommit -Repository $Repository -TagName $TagName -Request $Request
    if ($null -eq $actual -or $actual -cne $Commit) { throw 'GitHub did not confirm the release tag at the approved commit.' }
    return $actual
}

Export-ModuleMember -Function Get-ReleaseGitTagCommit, Ensure-ReleaseGitTag
