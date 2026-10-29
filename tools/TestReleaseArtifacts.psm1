#requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')

function Read-TestReleaseArtifactIdentity {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'ahk-manager-release.json' })
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 65536) { throw 'The preview manifest is absent, duplicate or oversized.' }
        $stream = $entries[0].Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(4096)
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                if ($memory.Length + $read -gt 65536) { throw 'The preview manifest exceeded its limit.' }
                $memory.Write($buffer, 0, $read)
            }
            $text = [Text.UTF8Encoding]::new($false, $true).GetString($memory.ToArray())
            $document = [Text.Json.JsonDocument]::Parse($text)
            try {
                $root = $document.RootElement
                if ((@($root.EnumerateObject() | ForEach-Object Name) | Sort-Object) -join ',' -cne 'commit,installer,notes,publishedAt,schemaVersion,updateProtocol,version') { throw 'Unexpected or duplicate preview manifest fields.' }
                return [pscustomobject]@{
                    Version = $root.GetProperty('version').GetString()
                    Commit = $root.GetProperty('commit').GetString()
                    Notes = $root.GetProperty('notes').GetString()
                    PublishedAt = $root.GetProperty('publishedAt').GetString()
                }
            }
            finally { $document.Dispose() }
        }
        finally { $memory.Dispose(); $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

# Never extract a downloaded ZIP wholesale. A PR can supply arbitrary entries;
# only the three flat, expected files are admitted to a fresh directory.
function Expand-TestReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Version
    )
    if ((Get-ReleaseVersionChannel $Version) -cne 'test' -or -not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute preview extraction directory.' }
    $expected = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
    $expected.Add("AHK-Manager-$Version-Setup-x64.exe", 1GB)
    $expected.Add('ahk-manager-release.json', 65536L)
    $expected.Add('SHA256.txt', 16384L)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -ne $expected.Count) { throw 'Unexpected preview ZIP entry count.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $type = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if (-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName) -or $entry.FullName -cne $entry.Name -or ($type -ne 0 -and $type -ne 0x8000) -or $entry.Length -le 0 -or $entry.Length -gt $expected[$entry.FullName]) { throw 'The preview ZIP contains an unsafe or oversized entry.' }
        }
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
