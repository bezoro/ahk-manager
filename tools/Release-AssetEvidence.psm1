#requires -Version 7.0
Set-StrictMode -Version Latest

# The release job creates a Git blob with the facts verified before publication.
# Its dedicated tag is create-only. It survives the Actions artifact retention period.
function Confirm-ReleaseAssetEvidence {
    param(
        [string]$Repository, [string]$Version, [string]$Commit, [object[]]$Assets,
        [scriptblock]$Request, [switch]$Create
    )
    $base = "https://api.github.com/repos/$Repository/git"
    $tag = "tags/portable-evidence/v$Version"
    $readRef = {
        try { & $Request 'Get' "$base/ref/$tag" $null }
        catch {
            $response = $_.Exception.PSObject.Properties['Response']
            if ($null -eq $response -or [int]$response.Value.StatusCode -ne 404) { throw }
            return $null
        }
    }
    $ref = & $readRef
    if ($null -eq $ref -and -not $Create) { return $false }

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("portable-assets-v1 $Version $Commit")
    # Canonical ordinal byte order must be independent of runner culture.
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($asset in $Assets) {
        if ($asset.name -cnotmatch '\A[A-Za-z0-9.-]+\z' -or $null -eq $asset.PSObject.Properties['digest'] -or $asset.digest -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or $asset.size -le 0) { throw 'A published asset has incomplete digest evidence.' }
        $rows.Add("$($asset.name) $($asset.size) $($asset.digest)")
    }
    $rows.Sort([StringComparer]::Ordinal)
    $lines.AddRange($rows)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    $encoded = [Convert]::ToBase64String($bytes)
    if ($null -eq $ref) {
        $blob = & $Request 'Post' "$base/blobs" (@{ content = $encoded; encoding = 'base64' } | ConvertTo-Json -Compress)
        if ($blob.sha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'GitHub did not confirm the release evidence blob.' }
        try { $null = & $Request 'Post' "$base/refs" (@{ ref = "refs/$tag"; sha = $blob.sha } | ConvertTo-Json -Compress) }
        catch {
            $response = $_.Exception.PSObject.Properties['Response']
            if ($null -eq $response -or [int]$response.Value.StatusCode -ne 422) { throw }
        }
        $ref = & $readRef
    }
    if ($null -eq $ref -or $ref.ref -cne "refs/$tag" -or $ref.object.type -cne 'blob' -or $ref.object.sha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'The release evidence reference is missing or invalid.' }
    $blob = & $Request 'Get' "$base/blobs/$($ref.object.sha)" $null
    if ($blob.sha -cne $ref.object.sha -or $blob.encoding -cne 'base64' -or $blob.size -ne $bytes.Length -or ($blob.content -replace '[\r\n]', '') -cne $encoded) { throw 'Published asset digests differ from the original release evidence.' }
    return $true
}
Export-ModuleMember -Function Confirm-ReleaseAssetEvidence
