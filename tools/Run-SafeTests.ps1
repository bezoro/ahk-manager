#requires -Version 7.2
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PackageRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$script:Plan = @(
    @{ Name='brush-stopped-recovery'; Selector='--brush-stopped-recovery'; Count=152 },
    @{ Name='brush-confirmation-final-read'; Selector='--brush-confirmation-final-read'; Count=8 },
    @{ Name='brush-register-formatting-settlement'; Selector='--brush-register-formatting-settlement'; Count=65 },
    @{ Name='manual-uac'; Selector='--manual-uac'; Count=47 }
)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
public static class SafeRunnerNative {
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,out int value,int size,out int returned);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle p,out long created,out long exit,out long kernel,out long user);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(SafeProcessHandle p,int flags,System.Text.StringBuilder name,ref int size);
 [DllImport("kernel32.dll")] public static extern uint GetProcessId(SafeProcessHandle p);
 [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(SafeProcessHandle p,uint ms);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool TerminateProcess(SafeProcessHandle p,uint code);
 public static bool Elevation(WindowsIdentity identity,out bool elevated) { int value,returned; bool ok=GetTokenInformation(identity.AccessToken.DangerousGetHandle(),20,out value,4,out returned); elevated=value!=0; return ok&&returned==4; }
 public static bool Stamp(SafeProcessHandle p,out long created,out string image) { created=0;image="";long exit,kernel,user;int size=32768;var name=new System.Text.StringBuilder(size);if(!GetProcessTimes(p,out created,out exit,out kernel,out user)||!QueryFullProcessImageName(p,0,name,ref size)||size<=0||size>=32768)return false;image=name.ToString();return created>0; }
}
"@
function Get-NormalProof {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent([Security.Principal.TokenAccessLevels]::Query)
    try { $elevated=$false; $known=[SafeRunnerNative]::Elevation($identity,[ref]$elevated)
        return @{ Known=$known; Elevated=$elevated; Sid=$identity.User.Value; Session=[Diagnostics.Process]::GetCurrentProcess().SessionId; Pid=$PID }
    } finally { $identity.Dispose() }
}
function Assert-NormalProof($Proof) {
    if (-not $Proof.Known -or $Proof.Elevated -or -not $Proof.Sid -or $Proof.Session -lt 1) { throw 'Normal interactive token not proved; no tests launched.' }
}
function Assert-PlainPath([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path refused: $current" }
        }
        $parent=[IO.Path]::GetDirectoryName($current); if ($parent -eq $current) { break }; $current=$parent
    }
}
function Resolve-PackageFile([string]$Root,[string]$Relative) {
    if (-not $Relative -or $Relative.Length -gt 4096 -or $Relative.Contains('\') -or $Relative -match '[:*?"<>|\r\n]' -or [IO.Path]::IsPathRooted($Relative) -or @($Relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { throw 'Invalid relative package path.' }
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd('\')+'\'; $resolved=[IO.Path]::GetFullPath([IO.Path]::Combine($Root,$Relative.Replace('/','\')))
    if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escape.' }
    Assert-PlainPath $resolved; return $resolved
}
function Get-StreamHash([IO.Stream]$Stream) {
    $Stream.Position=0; $hash=[Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hash.ComputeHash($Stream)) } finally { $hash.Dispose(); $Stream.Position=0 }
}
function Open-PinnedFile([string]$Path,[string]$Expected) {
    if ($Expected -notmatch '^[A-F0-9]{64}$') { throw 'Invalid fingerprint.' }; Assert-PlainPath $Path
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { if ($stream.Length -gt 67108864 -or (Get-StreamHash $stream) -ne $Expected) { throw "Input fingerprint mismatch: $Path" }; return @{Path=$Path;SHA256=$Expected;Stream=$stream} }
    catch { $stream.Dispose(); throw }
}
function Assert-Pins($Pins) {
    foreach ($pin in $Pins) { Assert-PlainPath $pin.Path; if ((Get-StreamHash $pin.Stream) -ne $pin.SHA256) { throw "Input changed: $($pin.Path)" } }
}
function Write-AtomicJson([string]$Path,$Value) {
    Assert-PlainPath $Path; $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $bytes=[Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 16))
    $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary,$Path)
}
function Start-OwnedChild([string]$Executable,[string[]]$Arguments,[string]$PrivateRoot) {
    Assert-PlainPath $Executable; Assert-PlainPath $PrivateRoot
    $info=[Diagnostics.ProcessStartInfo]::new($Executable); $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true; $info.WorkingDirectory=$PrivateRoot
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $info.Environment['TEMP']=$PrivateRoot; $info.Environment['TMP']=$PrivateRoot
    $started=[DateTime]::UtcNow;$clock=[Diagnostics.Stopwatch]::StartNew()
    $process=[Diagnostics.Process]::Start($info); $handle=$process.SafeHandle; $created=0L; $image=''
    $known=[SafeRunnerNative]::Stamp($handle,[ref]$created,[ref]$image)
    if (-not $known -or [SafeRunnerNative]::GetProcessId($handle) -ne $process.Id -or -not [string]::Equals($image,$Executable,[StringComparison]::OrdinalIgnoreCase)) { $process.Dispose(); throw 'Returned owned-child identity not proved.' }
    return @{StartedUtc=$started.ToString('o');Clock=$clock;Process=$process;Handle=$handle;Pid=$process.Id;Created=$created;Image=$image;Out=$process.StandardOutput.ReadToEndAsync();Err=$process.StandardError.ReadToEndAsync()}
}
function Finish-OwnedChild($Child,[int]$TimeoutMs) {
    $sw=$Child.Clock; $timedOut=-not $Child.Process.WaitForExit($TimeoutMs); $terminated=$false
    if ($timedOut) {
        $created=0L;$image=''
        if ([SafeRunnerNative]::WaitForSingleObject($Child.Handle,0) -eq 258 -and [SafeRunnerNative]::Stamp($Child.Handle,[ref]$created,[ref]$image) -and $created -eq $Child.Created -and [SafeRunnerNative]::GetProcessId($Child.Handle) -eq $Child.Pid -and [string]::Equals($image,$Child.Image,[StringComparison]::OrdinalIgnoreCase)) {
            $terminated=[SafeRunnerNative]::TerminateProcess($Child.Handle,124)
            [void]$Child.Process.WaitForExit(2000)
        }
    }
    if ($Child.Process.HasExited) { [void]$Child.Out.Wait(2000); [void]$Child.Err.Wait(2000) }
    $sw.Stop(); $exit=if($Child.Process.HasExited){$Child.Process.ExitCode}else{$null}
    $stdout=if($Child.Out.IsCompletedSuccessfully){$Child.Out.Result}else{''};$stderr=if($Child.Err.IsCompletedSuccessfully){$Child.Err.Result}else{''}
    if ($stdout.Length -gt 1048576 -or $stderr.Length -gt 1048576) { throw 'Child output limit exceeded.' }
    return @{StartedUtc=$Child.StartedUtc;EndedUtc=[DateTime]::UtcNow.ToString('o');ExitCode=$exit;Timeout=$timedOut;OwnedChildTerminated=$terminated;Pid=$Child.Pid;Created=$Child.Created;Image=$Child.Image;ElapsedMs=$sw.ElapsedMilliseconds;Stdout=$stdout;Stderr=$stderr}
}
function Test-PassRecord($Result,[string]$Kind,[int]$Count) {
    if ($Result.Timeout -or $Result.ExitCode -ne 0 -or $Result.Stderr.Length) { return $false }
    $lines=@($Result.Stdout -split '\r?\n' | Where-Object { $_.Length })
    if ($Kind -eq 'Wpf') { return @($lines | Where-Object { $_ -match '^UI checks=' }).Count -eq 1 -and $lines[-1] -ceq "UI checks=$Count PASS; native keyboard/monitor DPI not executed" -and @($lines | Where-Object { $_ -match '^PASS ' }).Count -eq $Count }
    if ($Kind -eq 'Ahk') { return $lines.Count -eq 1 -and $lines[0] -ceq 'PASS brush-functional count=16 classification=6' }
    if ($Kind -eq 'Generator') { return $lines.Count -eq 1 -and $lines[0] -cmatch '^PASS brush-fixture-generated source=9662EBFA3F6C72762D76A7CC7AE2DE6AAFC12C086734EB825B2391A8F5A5C55E bytes=[1-9][0-9]{0,5}$' }
    if ($Kind -eq 'FailureWorker') { return $lines.Count -eq 1 -and $lines[0] -ceq 'PASS safe-owned-worker count=1' }
    return $false
}
function Save-Suite([string]$Directory,[string]$Name,$Result,[string]$Kind,[int]$Count,$Inputs) {
    $out=Join-Path $Directory ($Name+'.stdout.txt');$err=Join-Path $Directory ($Name+'.stderr.txt')
    [IO.File]::WriteAllText($out,$Result.Stdout);[IO.File]::WriteAllText($err,$Result.Stderr)
    $pass=Test-PassRecord $Result $Kind $Count
    $row=@{Name=$Name;Kind=$Kind;Status=if($pass){'PASS'}else{'FAIL'};ExpectedCount=$Count;ObservedPassLines=@($Result.Stdout -split '\r?\n' | Where-Object { $_ -match '^PASS ' }).Count;AssertedCount=if($pass){$Count}else{$null};ExitCode=$Result.ExitCode;Timeout=$Result.Timeout;OwnedChildTerminated=$Result.OwnedChildTerminated;Pid=$Result.Pid;Creation=$Result.Created;Image=$Result.Image;StartedUtc=$Result.StartedUtc;EndedUtc=$Result.EndedUtc;ElapsedMs=$Result.ElapsedMs;InputFingerprints=$Inputs;StdoutSHA256=(Get-FileHash -LiteralPath $out).Hash;StderrSHA256=(Get-FileHash -LiteralPath $err).Hash}
    Write-AtomicJson (Join-Path $Directory ($Name+'.json')) $row;return $row
}
function Assert-AhkFixture([string]$Path) {
    Assert-PlainPath $Path;$bytes=[IO.File]::ReadAllBytes($Path)
    if($bytes.Length -gt 131072){throw 'Generated fixture exceeds bound.'}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
    if($text.Contains('@@') -or $text.Contains('::') -or $text.Contains('#HotIf') -or $text -match '(?im)^\s*#(?:InstallKeybdHook|InstallMouseHook|UseHook|Hotstring)\b' -or $text -match '(?i)\b(?:Run|RunWait|Send|SendInput|SendEvent|SendPlay|ControlSend|Hotkey|Hotstring|DllCall|ComObject|WinActive|WinGetProcessName|WinActivate|ProcessClose|ProcessSetPriority|Shutdown)\s*\(' -or $text -match '(?im)^\s*(?:Run|RunWait|Send|SendInput|SendEvent|SendPlay|ControlSend|Hotkey|Hotstring|DllCall|ComObject|WinActive|WinGetProcessName|WinActivate|ProcessClose|ProcessSetPriority|Shutdown)\b' -or $text -match '(?i)\bRunAs\b' -or [regex]::Matches($text,'(?m)^#SingleInstance Off$').Count -ne 1 -or [regex]::Matches($text,'(?m)^if A_IsAdmin \{').Count -ne 1){throw 'Generated fixture failed independent no-effect/normal-token preflight.'}
}
function Invoke-SafePlan {
    $start=[DateTime]::UtcNow;$sw=[Diagnostics.Stopwatch]::StartNew();$pins=[Collections.Generic.List[object]]::new();$entries=[Collections.Generic.List[object]]::new();$run=$null;$proof=$null;$failure='';$manifestHash=$null
    try {
        if ($args.Count) { throw 'Caller arguments refused.' }
        $proof=Get-NormalProof;Assert-NormalProof $proof
        $run=Join-Path ([IO.Path]::GetTempPath()) ('BezoroSafeTests-'+[Guid]::NewGuid().ToString('N'));Assert-PlainPath $run
        if (Test-Path -LiteralPath $run) { throw 'Private run directory already exists.' };[void][IO.Directory]::CreateDirectory($run)
        $manifestPath=Resolve-PackageFile $script:PackageRoot 'tools/safe-tests/manifest.json';$checksum=Resolve-PackageFile $script:PackageRoot 'tools/safe-tests/bin/prepared-manifest.sha256'
        $manifestHash=[IO.File]::ReadAllText($checksum).Trim();$manifestPin=Open-PinnedFile $manifestPath $manifestHash;$pins.Add($manifestPin)
        $checksumPin=Open-PinnedFile $checksum (Get-FileHash -LiteralPath $checksum).Hash;$pins.Add($checksumPin)
        $manifest=[IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable
        if ($manifest.Schema -ne 1 -or $manifest.SourceInputs.Count -ne 74 -or $manifest.Plan.Count -ne 4 -or $manifest.Interpreter.Path -cne 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe') { throw 'Unsupported prepared manifest.' }
        for($index=0;$index-lt 4;$index++){if($manifest.Plan[$index].Selector -cne $script:Plan[$index].Selector -or $manifest.Plan[$index].Count -ne $script:Plan[$index].Count){throw 'Prepared plan differs from fixed allowed plan.'}}
        $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($row in @($manifest.SourceInputs)+@($manifest.PreparedInputs)+@($manifest.BinaryOutputs)+@($manifest.DataInputs)){
            if(-not $paths.Add($row.Path)){throw 'Duplicate prepared path.'};$path=Resolve-PackageFile $script:PackageRoot $row.Path;$pins.Add((Open-PinnedFile $path $row.SHA256))
        }
        $pins.Add((Open-PinnedFile $manifest.Interpreter.Path $manifest.Interpreter.SHA256))
        $pwsh=Join-Path $PSHOME 'pwsh.exe';Assert-PlainPath $pwsh;$pins.Add((Open-PinnedFile $pwsh (Get-FileHash -LiteralPath $pwsh).Hash))
        $test=Resolve-PackageFile $script:PackageRoot 'tools/safe-tests/bin/Tests.WpfUI.exe';$generator=Resolve-PackageFile $script:PackageRoot 'Tests.Brush/New-BrushFixture.ps1'
        $fingerprints=@($pins | ForEach-Object { @{Path=$_.Path;SHA256=$_.SHA256} })
        foreach($suite in $script:Plan){
            Assert-Pins $pins;$suiteDir=Join-Path $run $suite.Name;[void][IO.Directory]::CreateDirectory($suiteDir);$private=Join-Path $suiteDir 'fixtures';[void][IO.Directory]::CreateDirectory($private)
            if($suite.Selector -eq '--brush-register-formatting-settlement'){[IO.File]::Copy((Resolve-PackageFile $script:PackageRoot 'tools/safe-tests/bin/fixtures/ORIGINAL-POSTREGISTER-PENDING.json'),(Join-Path $suiteDir 'ORIGINAL-POSTREGISTER-PENDING.json'),$false)}
            $child=Start-OwnedChild $test @($suite.Selector,$private) $private
            try {$result=Finish-OwnedChild $child 60000} finally {$child.Process.Dispose()}
            $row=Save-Suite $run $suite.Name $result 'Wpf' $suite.Count $fingerprints;$entries.Add($row);if($row.Status-ne 'PASS'){throw "Required suite failed: $($suite.Name)"}
        }
        Assert-Pins $pins;$ahkDir=Join-Path $run 'brush-functional';[void][IO.Directory]::CreateDirectory($ahkDir);$fixture=Join-Path $ahkDir ('brush-fixture-'+[Guid]::NewGuid().ToString('N')+'.ahk')
        $child=Start-OwnedChild $pwsh @('-NoProfile','-File',$generator,'-OutputPath',$fixture) $ahkDir
        try {$result=Finish-OwnedChild $child 10000} finally {$child.Process.Dispose()}
        $row=Save-Suite $run 'brush-generation' $result 'Generator' 0 $fingerprints;$entries.Add($row);if($row.Status-ne 'PASS'){throw 'Fixture generation failed.'}
        Assert-AhkFixture $fixture;$fixturePin=Open-PinnedFile $fixture (Get-FileHash -LiteralPath $fixture).Hash;$pins.Add($fixturePin);Assert-Pins $pins
        $child=Start-OwnedChild $manifest.Interpreter.Path @('/ErrorStdOut',$fixture) $ahkDir
        try {$result=Finish-OwnedChild $child 10000} finally {$child.Process.Dispose()}
        $row=Save-Suite $run 'brush-functional' $result 'Ahk' 22 @($fingerprints+@(@{Path=$fixture;SHA256=$fixturePin.SHA256}));$entries.Add($row);if($row.Status-ne 'PASS'){throw 'Adapted Brush routing fixture failed.'}
        Assert-Pins $pins
    } catch {$failure=$_.Exception.Message}
    finally {foreach($pin in $pins){$pin.Stream.Dispose()}}
    $sw.Stop();$summary=@{Schema=1;Status=if($failure){'FAIL'}else{'PASS'};Failure=$failure;NormalToken=$proof;StartedUtc=$start.ToString('o');EndedUtc=[DateTime]::UtcNow.ToString('o');ElapsedMs=$sw.ElapsedMilliseconds;RunDirectory=$run;ManifestSHA256=$manifestHash;FeatureSuiteCount=5;PreparationCount=1;Entries=@($entries.ToArray());AssertedCount=if($failure){$null}else{294};AppEffectsPerformed=$false;Limitations='Fake/offscreen WPF and adapted AHK routing only; no physical hooks/sends/UIPI or real task/elevated helper effects.'}
    if($run){Write-AtomicJson (Join-Path $run 'summary.json') $summary};$summary | ConvertTo-Json -Depth 16
    return $(if($failure){2}else{0})
}
if ($MyInvocation.InvocationName -ne '.') {
    if ($args.Count) { @{Schema=1;Status='FAIL';Failure='Caller arguments refused.';Entries=@();AppEffectsPerformed=$false} | ConvertTo-Json -Depth 4;exit 2 }
    $output=@(Invoke-SafePlan);$code=$output[-1];$output[0..($output.Count-2)] | Write-Output;exit $code
}
