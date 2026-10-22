#requires -Version 7.2
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($args.Count){throw 'No caller arguments allowed.'}
$package=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $package 'tools/Run-SafeTests.ps1')
Assert-NormalProof (Get-NormalProof)
$root=Join-Path ([IO.Path]::GetTempPath()) ('BezoroSafeRunnerFailures-'+[Guid]::NewGuid().ToString('N'));Assert-PlainPath $root
[void][IO.Directory]::CreateDirectory($root)
$cases=[Collections.Generic.List[object]]::new();$sw=[Diagnostics.Stopwatch]::StartNew();$started=[DateTime]::UtcNow
function Check-Refusal([string]$Name,[scriptblock]$Body){
    $refused=$false;$reason=''
    try{& $Body | Out-Null}catch{$refused=$true;$reason=$_.Exception.Message}
    $row=@{Name=$Name;Status=if($refused){'PASS'}else{'FAIL'};ExpectedExit=2;ActualExit=if($refused){2}else{0};Reason=$reason;SimulatedBoundary=$true}
    $cases.Add($row);Write-AtomicJson (Join-Path $root ($Name+'.json')) $row
}
Check-Refusal 'elevated-preflight' {Assert-NormalProof @{Known=$true;Elevated=$true;Sid='scripted elevated SID';Session=1}}
Check-Refusal 'unknown-token-preflight' {Assert-NormalProof @{Known=$false;Elevated=$false;Sid='scripted SID';Session=1}}
Check-Refusal 'path-escape' {Resolve-PackageFile $package '../outside'}
Check-Refusal 'rooted-path' {Resolve-PackageFile $package 'C:/outside'}
$modified=Join-Path $root 'modified-private-input.txt';[IO.File]::WriteAllText($modified,'original');$original=(Get-FileHash -LiteralPath $modified).Hash;[IO.File]::WriteAllText($modified,'changed')
Check-Refusal 'modified-input' {$pin=Open-PinnedFile $modified $original;$pin.Stream.Dispose()}
$pwsh=Join-Path $PSHOME 'pwsh.exe';$worker=Join-Path $PSScriptRoot 'OwnedWorker.ps1';$runner=Join-Path $package 'tools/Run-SafeTests.ps1'
$unknown=Start-OwnedChild $pwsh @('-NoProfile','-File',$runner,'--unknown-selector') $root
try{$result=Finish-OwnedChild $unknown 10000}finally{$unknown.Process.Dispose()}
[IO.File]::WriteAllText((Join-Path $root 'unknown-argument.stdout.txt'),$result.Stdout);[IO.File]::WriteAllText((Join-Path $root 'unknown-argument.stderr.txt'),$result.Stderr)
$json=$result.Stdout | ConvertFrom-Json -AsHashtable
$row=@{Name='unknown-argument';Status=if($result.ExitCode -ne 0 -and $json.Status-eq 'FAIL' -and $json.Entries.Count-eq 0){'PASS'}else{'FAIL'};ActualExit=$result.ExitCode;RefusedJSON=$json;SimulatedBoundary=$false};$cases.Add($row);Write-AtomicJson (Join-Path $root 'unknown-argument.json') $row
foreach($mode in @('Fail','MissingPass')){
    $child=Start-OwnedChild $pwsh @('-NoProfile','-File',$worker,'-Mode',$mode) $root
    try{$result=Finish-OwnedChild $child 10000}finally{$child.Process.Dispose()}
    $suite=Save-Suite $root ('worker-'+$mode) $result 'FailureWorker' 1 @(@{Path=$worker;SHA256=(Get-FileHash -LiteralPath $worker).Hash})
    $propagated=if($suite.Status-eq 'PASS'){0}else{2}
    $row=@{Name=$mode;Status=if($suite.Status-eq 'FAIL' -and $propagated-ne 0){'PASS'}else{'FAIL'};RunnerStatus=$suite.Status;PropagatedExit=$propagated;WorkerExit=$result.ExitCode;SimulatedBoundary=$false};$cases.Add($row);Write-AtomicJson (Join-Path $root ($mode+'.json')) $row
}
$companion=Start-OwnedChild $pwsh @('-NoProfile','-File',$worker,'-Mode','Hold') $root
try {
    $target=Start-OwnedChild $pwsh @('-NoProfile','-File',$worker,'-Mode','Hold') $root
    try{$result=Finish-OwnedChild $target 30;$targetEnded=$target.Process.HasExited}finally{$target.Process.Dispose()}
    $companionAlive=[SafeRunnerNative]::WaitForSingleObject($companion.Handle,0)-eq 258
    $suite=Save-Suite $root 'owned-timeout' $result 'FailureWorker' 1 @(@{Path=$worker;SHA256=(Get-FileHash -LiteralPath $worker).Hash})
    $row=@{Name='owned-child-timeout';Status=if($result.Timeout -and $result.OwnedChildTerminated -and $targetEnded -and $companionAlive -and $suite.Status-eq 'FAIL'){'PASS'}else{'FAIL'};Timeout=$result.Timeout;OnlyHeldTargetTerminated=$result.OwnedChildTerminated;TargetPid=$result.Pid;CompanionPid=$companion.Pid;CompanionStillAlive=$companionAlive;PropagatedExit=2;SimulatedBoundary=$false};$cases.Add($row);Write-AtomicJson (Join-Path $root 'owned-child-timeout.json') $row
    [void]$companion.Process.WaitForExit(5000)
}finally{$companion.Process.Dispose()}
$sw.Stop();$failed=@($cases | Where-Object Status -ne 'PASS').Count
$summary=@{Schema=1;Status=if($failed){'FAIL'}else{'PASS'};Cases=@($cases.ToArray());CaseCount=$cases.Count;NormalToken=(Get-NormalProof);StartedUtc=$started.ToString('o');EndedUtc=[DateTime]::UtcNow.ToString('o');ElapsedMs=$sw.ElapsedMilliseconds;RunDirectory=$root;AppEffectsPerformed=$false;Limitations='Elevated/unknown-token rejection is a scripted preflight boundary, not a real elevated execution. Only fixed private benign workers ran; timeout terminated only its held owned target.'}
Write-AtomicJson (Join-Path $root 'summary.json') $summary;$summary | ConvertTo-Json -Depth 14
exit $(if($failed){1}else{0})
