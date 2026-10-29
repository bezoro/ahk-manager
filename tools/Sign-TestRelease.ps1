#requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$certificate = $env:CSC_LINK
$publisher = $env:AHK_PUBLISHER_NAME
if ([string]::IsNullOrWhiteSpace($certificate) -and [string]::IsNullOrWhiteSpace($publisher)) { return $false }
if ([string]::IsNullOrWhiteSpace($certificate) -or [string]::IsNullOrWhiteSpace($publisher)) { throw 'Preview signing requires both the configured certificate and public publisher name.' }
if (-not $IsWindows -or -not [IO.Path]::IsPathFullyQualified($InstallerPath)) { throw 'Preview signing requires an absolute installer path on the trusted Windows publisher.' }
$tool = @(Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin') -Filter signtool.exe -Recurse | Where-Object { $_.Directory.Name -ceq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1)
if ($tool.Count -ne 1) { throw 'The Windows SDK signing tool is unavailable.' }
$directory = Join-Path $env:RUNNER_TEMP ('ahk-signing-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$certificatePath = Join-Path $directory 'publisher.pfx'
try {
    # This configuration is a protected main-branch secret. Never log it or its
    # download URL, and never pass it to code from the pull request.
    if ($certificate.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        try { Invoke-WebRequest -Uri $certificate -OutFile $certificatePath -MaximumRedirection 3 | Out-Null }
        catch { throw 'The configured signing certificate could not be downloaded.' }
    }
    elseif ([IO.Path]::IsPathFullyQualified($certificate) -and (Test-Path -LiteralPath $certificate -PathType Leaf)) { [IO.File]::Copy($certificate, $certificatePath) }
    else {
        try { [IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($certificate)) }
        catch { throw 'The configured signing certificate is invalid.' }
    }
    if ((Get-Item -LiteralPath $certificatePath).Length -gt 1MB) { throw 'The signing certificate exceeds its size limit.' }
    $arguments = @('sign', '/f', $certificatePath, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
    if (-not [string]::IsNullOrEmpty($env:CSC_KEY_PASSWORD)) { $arguments += @('/p', $env:CSC_KEY_PASSWORD) }
    $arguments += $InstallerPath
    & $tool[0].FullName @arguments *> $null
    if ($LASTEXITCODE -ne 0) { throw 'The trusted signing step did not complete.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne $publisher) { throw 'The signed preview does not match the configured trusted publisher.' }
    Write-Host 'Preview installer signed and its publisher verified.'
    return $true
}
finally { Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue }
