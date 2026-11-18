# In-process fixture for the create-only evidence reference and its Git blob.
function Invoke-ReleaseEvidenceFixture($fixture, $Method, $Uri, $Body) {
    if ($Method -ceq 'Get' -and $Uri -match '/git/ref/(tags/portable-evidence/v[^/]+)$') {
        if ($null -eq $fixture.EvidenceRef) {
            $missing = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Missing fixture evidence.', $missing)
        }
        return $fixture.EvidenceRef
    }
    if ($Method -ceq 'Post' -and $Uri.EndsWith('/git/blobs')) {
        $inputBlob = $Body | ConvertFrom-Json
        $fixture.EvidenceBlob = [pscustomobject]@{ sha = 'd' * 40; content = $inputBlob.content; encoding = 'base64'; size = [Convert]::FromBase64String($inputBlob.content).Length }
        return $fixture.EvidenceBlob
    }
    if ($Method -ceq 'Post' -and $Uri.EndsWith('/git/refs') -and ($Body | ConvertFrom-Json).ref.StartsWith('refs/tags/portable-evidence/')) {
        $inputRef = $Body | ConvertFrom-Json
        $fixture.EvidenceRef = [pscustomobject]@{ ref = $inputRef.ref; object = [pscustomobject]@{ type = 'blob'; sha = $inputRef.sha } }
        return $fixture.EvidenceRef
    }
    if ($Method -ceq 'Get' -and $Uri -match '/git/blobs/[0-9a-f]{40}$') { return $fixture.EvidenceBlob }
    throw 'Unexpected mocked release evidence route.'
}
