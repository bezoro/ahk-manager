#requires -Version 7.0
Set-StrictMode -Version Latest

function Test-ReleaseInstallerLength {
    param([Parameter(Mandatory = $true)][long]$Bytes)
    # Update client, download cache and native apply host use the same 1 GiB bound.
    return $Bytes -gt 0 -and $Bytes -le 1GB
}

Export-ModuleMember -Function Test-ReleaseInstallerLength
