#requires -Version 7.0
Set-StrictMode -Version Latest

# These bounded versions are shared by the feed, cache, native apply protocol and
# Windows package identity. Preview identifiers are run number, attempt and PR.
function Test-ReleaseVersion {
    param([AllowNull()][AllowEmptyString()][string]$Version)
    if ($null -eq $Version -or $Version.Length -gt 64 -or $Version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-test\.([1-9][0-9]*)\.([1-9][0-9]*)\.([1-9][0-9]*))?\z') { return $false }
    foreach ($part in ($Version -split '\.|-test\.')) {
        $number = 0L
        if (-not [long]::TryParse($part, [ref]$number) -or $number -gt [int]::MaxValue) { return $false }
    }
    return $true
}

function Get-ReleaseVersionChannel {
    param([Parameter(Mandatory = $true)][string]$Version)
    if (-not (Test-ReleaseVersion $Version)) { throw 'The release version is invalid.' }
    if ($Version.Contains('-test.')) { return 'test' }
    return 'stable'
}

Export-ModuleMember -Function Test-ReleaseVersion, Get-ReleaseVersionChannel
