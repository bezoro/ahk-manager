#requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
Import-Module (Join-Path $PSScriptRoot 'PortableRelease.psm1')

function Read-PreviewManifestEntry {
    param([IO.Compression.ZipArchive]$Archive, [string]$Name)
    $entries = @($Archive.Entries | Where-Object { $_.FullName -ceq $Name })
    if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 65536) { throw 'The preview manifest is absent, duplicate or oversized.' }
    $stream = $entries[0].Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(4096)
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt 65536) { throw 'The preview manifest exceeded its limit.' }
            $memory.Write($buffer, 0, $read)
        }
        return [Text.UTF8Encoding]::new($false, $true).GetString($memory.ToArray())
    }
    finally { $memory.Dispose(); $stream.Dispose() }
}

function Get-PreviewArtifactEntries {
    param([IO.Compression.ZipArchive]$Archive, [string]$Version)
    $expected = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
    $expected.Add("AHK-Manager-$Version-Setup-x64.exe", 1GB)
    $expected.Add('ahk-manager-release.json', 65536L)
    $expected.Add('SHA256.txt', 16384L)
    if ($Archive.Entries.Count -eq 5) {
        $expected.Add("AHK-Manager-$Version-Portable-x64.zip", 1GB)
        $expected.Add('ahk-manager-portable-release.json', 65536L)
    }
    if ($Archive.Entries.Count -ne $expected.Count) { throw 'Unexpected preview ZIP entry count.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    foreach ($entry in $Archive.Entries) {
        $type = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if (-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName) -or $entry.FullName -cne $entry.Name -or ($type -ne 0 -and $type -ne 0x8000) -or $entry.Length -le 0 -or $entry.Length -gt $expected[$entry.FullName]) { throw 'The preview ZIP contains an unsafe or oversized entry.' }
        $total += $entry.Length
        if ($total -gt 2GB) { throw 'The combined preview artifact exceeds its limit.' }
    }
    return $expected
}

function Read-TestReleaseArtifactIdentity {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $document = [Text.Json.JsonDocument]::Parse((Read-PreviewManifestEntry $archive 'ahk-manager-release.json'))
        try {
            $root = $document.RootElement
            if ((@($root.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'commit,installer,notes,publishedAt,schemaVersion,updateProtocol,version' -or $root.GetProperty('schemaVersion').GetInt32() -ne 1 -or $root.GetProperty('updateProtocol').GetInt32() -ne 1) { throw 'Unexpected or duplicate preview manifest fields.' }
            $identity = [pscustomobject]@{
                Version = $root.GetProperty('version').GetString()
                Commit = $root.GetProperty('commit').GetString()
                Notes = $root.GetProperty('notes').GetString()
                PublishedAt = $root.GetProperty('publishedAt').GetString()
                HasPortable = $archive.Entries.Count -eq 5
            }
        }
        finally { $document.Dispose() }
        $null = Get-PreviewArtifactEntries $archive $identity.Version
        if ($identity.HasPortable) {
            $portable = ConvertFrom-PortableReleaseJson (Read-PreviewManifestEntry $archive 'ahk-manager-portable-release.json')
            if ($portable.Version -cne $identity.Version -or $portable.Commit -cne $identity.Commit -or $portable.PublishedAt -cne $identity.PublishedAt -or $portable.Notes -cne $identity.Notes -or $archive.GetEntry($portable.ArchiveName).Length -ne $portable.ArchiveBytes) { throw 'The portable package does not identify the same preview build.' }
        }
        return $identity
    }
    finally { $archive.Dispose() }
}

# Admit only the exact legacy installer family or the paired portable family.
# Neither PR executables nor the contents of the portable payload are executed.
function Expand-TestReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Version
    )
    if ((Get-ReleaseVersionChannel $Version) -cne 'test' -or -not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute preview extraction directory.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $expected = Get-PreviewArtifactEntries $archive $Version
        [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
        foreach ($entry in $archive.Entries) {
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Open((Join-Path $OutputRoot $entry.FullName), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(65536)
                $length = 0L
                while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $length += $read
                    if ($length -gt $entry.Length -or $length -gt $expected[$entry.FullName]) { throw 'Preview decompression exceeded its declared length.' }
                    $outputStream.Write($buffer, 0, $read)
                }
                if ($length -ne $entry.Length) { throw 'Incomplete preview artifact entry.' }
            }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

Export-ModuleMember -Function Expand-TestReleaseArtifact, Read-TestReleaseArtifactIdentity
