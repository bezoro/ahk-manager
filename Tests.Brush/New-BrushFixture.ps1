[CmdletBinding(PositionalBinding = $false)]
param([Parameter(Mandatory = $true)][string]$OutputPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceHash = '9662EBFA3F6C72762D76A7CC7AE2DE6AAFC12C086734EB825B2391A8F5A5C55E'
$templateHash = 'B707A3095E63D742988F6BDE11F6A34AFBC5981FC273B846D00CDBFBF128136C'
$utf8 = [Text.UTF8Encoding]::new($false, $true)

function Replace-ExactlyOnce([string]$Text, [string]$Before, [string]$After) {
    $first = $Text.IndexOf($Before, [StringComparison]::Ordinal)
    if ($first -lt 0 -or $Text.IndexOf($Before, $first + $Before.Length, [StringComparison]::Ordinal) -ge 0) {
        throw 'Captured source or fixture template shape mismatch.'
    }
    return $Text.Substring(0, $first) + $After + $Text.Substring($first + $Before.Length)
}

# CmdletBinding with PositionalBinding=false rejects undeclared arguments before this body.
if (-not [IO.Path]::IsPathFullyQualified($OutputPath) -or $OutputPath.StartsWith('\\') -or
    $OutputPath.Length -gt 1024 -or [IO.Path]::GetExtension($OutputPath) -cne '.ahk') {
    throw 'The internal output must be an absolute local .ahk path.'
}
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output.Substring([IO.Path]::GetPathRoot($output).Length).Contains(':')) { throw 'Alternate data stream output refused.' }
$parent = [IO.Path]::GetDirectoryName($output)
if (-not [IO.Directory]::Exists($parent) -or [IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'The internal output parent must exist and the output must be absent.'
}
$ancestor = [IO.DirectoryInfo]::new($parent)
while ($null -ne $ancestor) {
    if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse output path refused.' }
    $ancestor = $ancestor.Parent
}

$originalBytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'Original-Brush.ahk'))
if ($originalBytes.Length -gt 65536 -or
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($originalBytes)) -cne $sourceHash) {
    throw 'Captured Brush source fingerprint mismatch.'
}
$source = $utf8.GetString($originalBytes).TrimStart([char]0xFEFF).Replace("`r`n", "`n")
$source = Replace-ExactlyOnce $source '#SingleInstance Force' '#SingleInstance Off'
$selfElevation = @'
if !A_IsAdmin {
    try Run('*RunAs "' A_AhkPath '" "' A_ScriptFullPath '"')
    ExitApp
}
'@
$source = Replace-ExactlyOnce $source $selfElevation ''
$source = Replace-ExactlyOnce $source 'WinActive("A")' 'BrushFixtureObserveActive("A")'
$source = Replace-ExactlyOnce $source 'WinGetProcessName(hwnd)' 'BrushFixtureObserveProcessName(hwnd)'
$source = Replace-ExactlyOnce $source '#HotIf IsPaintingApp()' ''
$source = Replace-ExactlyOnce $source '#HotIf' ''
$payloads = [ordered]@{
    SMALLER = '"{Blind}" Config.BrushSizeSmaller'
    LARGER = '"{Blind}" Config.BrushSizeLarger'
    VOLUME_DOWN = '"{Volume_Down}"'
    VOLUME_UP = '"{Volume_Up}"'
}
$source = Replace-ExactlyOnce $source ('F13:: SendInput(' + $payloads.SMALLER + ')') ''
$source = Replace-ExactlyOnce $source ('F14:: SendInput(' + $payloads.LARGER + ')') ''
$source = Replace-ExactlyOnce $source ('F13:: SendInput(' + $payloads.VOLUME_DOWN + ')') ''
$source = Replace-ExactlyOnce $source ('F14:: SendInput(' + $payloads.VOLUME_UP + ')') ''
$templateBytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'BrushFixture.template.ahk'))
if ($templateBytes.Length -gt 65536 -or
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($templateBytes)) -cne $templateHash) {
    throw 'Fixture template fingerprint mismatch.'
}
$fixture = $utf8.GetString($templateBytes).Replace("`r`n", "`n")
$fixture = Replace-ExactlyOnce $fixture '@@ADAPTED_SOURCE@@' $source
foreach ($entry in $payloads.GetEnumerator()) {
    $fixture = Replace-ExactlyOnce $fixture ('@@' + $entry.Key + '@@') $entry.Value
}
# Defense in depth before writing; the runner independently binds the whole template and output.
if ($fixture.Contains('@@') -or $fixture.Contains('::') -or $fixture.Contains('#HotIf') -or
    $fixture -match '(?im)^\s*#(?:InstallKeybdHook|InstallMouseHook|UseHook|Hotstring)\b' -or
    $fixture -match '(?i)\b(?:Run|RunWait|Send|SendInput|SendEvent|SendPlay|ControlSend|Hotkey|Hotstring|DllCall|ComObject|WinActive|WinGetProcessName|WinActivate|ProcessClose|ProcessSetPriority|Shutdown)\s*\(' -or
    $fixture -match '(?im)^\s*(?:Run|RunWait|Send|SendInput|SendEvent|SendPlay|ControlSend|Hotkey|Hotstring|DllCall|ComObject|WinActive|WinGetProcessName|WinActivate|ProcessClose|ProcessSetPriority|Shutdown)\b' -or
    $fixture -match '(?i)\bRunAs\b' -or
    ([regex]::Matches($fixture, '(?m)^#SingleInstance Off$').Count -ne 1)) {
    throw 'Generated fixture failed its no-input/no-process-effects check.'
}
$fixtureBytes = $utf8.GetBytes($fixture)
if ($fixtureBytes.Length -gt 131072) { throw 'Generated fixture exceeds its bound.' }
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($fixtureBytes) } finally { $stream.Dispose() }
[Console]::Out.WriteLine('PASS brush-fixture-generated source=' + $sourceHash + ' bytes=' + $fixtureBytes.Length)
