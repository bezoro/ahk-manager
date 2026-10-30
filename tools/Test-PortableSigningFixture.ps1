#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ApplicationRoot,
    [Parameter(Mandatory = $true)][string]$PublisherPolicyPath,
    [Parameter(Mandatory = $true)][string]$CandidateManifestPath,
    [Parameter(Mandatory = $true)][string]$BaselineManifestPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'test-support/PortableSigningFixture.psm1') -Force
$fixture = $null
try {
    $fixture = New-PortableSigningFixture -ApplicationRoot $ApplicationRoot -PublisherPolicyPath $PublisherPolicyPath -CandidateManifestPath $CandidateManifestPath -BaselineManifestPath $BaselineManifestPath -OutputRoot $OutputRoot
    $proof = $fixture.Proof
}
finally { if ($fixture) { Remove-PortableSigningFixture -Fixture $fixture } }
$proof | ConvertTo-Json -Depth 8
