#requires -Version 7.2
param([ValidateSet('Pass','Fail','MissingPass','Hold')][string]$Mode='Pass')
$ErrorActionPreference='Stop'
if($Mode-eq 'Fail'){Write-Output 'fixed owned worker deliberate failure';exit 7}
if($Mode-eq 'MissingPass'){Write-Output 'fixed owned worker missing result';exit 0}
if($Mode-eq 'Hold'){Start-Sleep -Milliseconds 1500}
Write-Output 'PASS safe-owned-worker count=1';exit 0
