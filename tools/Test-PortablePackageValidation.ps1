#requires -Version 7.0
param([Parameter(Mandatory = $true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute package-validation fixture directory.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$version = '1.4.0-test.1.1.1'
$commit = 'b' * 40
$checks = [Collections.Generic.List[object]]::new()

function Check([bool]$Passed, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed })
    if (-not $Passed) { throw $Name }
    Write-Host "PASS $Name"
}

function New-InertArchive([string]$Kind) {
    $root = Join-Path $OutputRoot $Kind
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $files = [Collections.Generic.SortedDictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    foreach ($name in @('Bezoro.AhkManager.exe', 'App/Bezoro.AhkManager.exe', 'App/resources/app.asar', 'App/resources/app-update.yml', 'App/backend/AhkManager.Backend.exe', 'App/backend/update/AhkManager.UpdateHost.exe')) {
        if ($Kind -ceq 'missing-backend' -and $name -ceq 'App/backend/AhkManager.Backend.exe') { continue }
        $bytes = $utf8.GetBytes('Inert, unexecuted package validation fixture: ' + $name)
        if ($Kind -ceq 'empty-backend' -and $name -ceq 'App/backend/AhkManager.Backend.exe') { $bytes = [byte[]]::new(0) }
        $files.Add($name, $bytes)
    }
    $inventory = @(
        foreach ($file in $files.GetEnumerator()) {
            [ordered]@{ path = $file.Key; bytes = $file.Value.Length; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file.Value)).ToLowerInvariant() }
        }
    )
    $files.Add('portable.json', $utf8.GetBytes('{"schemaVersion":1,"applicationId":"Bezoro.AhkManager.Electron","dataDirectory":"Data"}'))
    $descriptor = [ordered]@{ schemaVersion = 1; applicationId = 'Bezoro.AhkManager.Electron'; updateProtocol = 2; version = $version; commit = $commit; files = $inventory }
    $files.Add('portable-package.json', $utf8.GetBytes(($descriptor | ConvertTo-Json -Depth 8)))
    $path = Join-Path $root "AHK-Manager-$version-Portable-x64.zip"
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files.GetEnumerator()) {
            $entry = $zip.CreateEntry($file.Key)
            $output = $entry.Open()
            try { $output.Write($file.Value, 0, $file.Value.Length) }
            finally { $output.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    $manifest = Join-Path $root 'ahk-manager-portable-release.json'
    & (Join-Path $PSScriptRoot 'New-PortableReleaseManifest.ps1') -ArchivePath $path -OutputPath $manifest -Version $version -Commit $commit -Notes 'Inert package validation fixture. Payload bytes are never executed.'
    return [pscustomobject]@{ Archive = $path; Manifest = $manifest; Evidence = Join-Path $root 'evidence' }
}

$valid = New-InertArchive 'complete'
& (Join-Path $PSScriptRoot 'Test-PortablePackage.ps1') -ArchivePath $valid.Archive -ManifestPath $valid.Manifest -OutputRoot $valid.Evidence
Check (Test-Path -LiteralPath (Join-Path $valid.Evidence 'portable-package-summary.json')) 'the metadata validator accepts a complete inert runtime inventory without executing its payload'
foreach ($kind in @('missing-backend', 'empty-backend')) {
    $fixture = New-InertArchive $kind
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Test-PortablePackage.ps1') -ArchivePath $fixture.Archive -ManifestPath $fixture.Manifest -OutputRoot $fixture.Evidence }
    catch { $rejected = $_.Exception.Message -ceq 'A critical portable runtime file is absent or empty.' }
    Check ($rejected -and -not (Test-Path -LiteralPath $fixture.Evidence)) "a correctly hashed ZIP with $kind is refused before package evidence can claim success"
}
$summary = [ordered]@{ fixture = $true; payloadExecuted = $false; passed = $true; checks = $checks.ToArray() }
[IO.File]::WriteAllText((Join-Path $OutputRoot 'portable-validation-summary.json'), ($summary | ConvertTo-Json -Depth 6), $utf8)
