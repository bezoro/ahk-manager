#requires -Version 7.0
Set-StrictMode -Version Latest

function Stop-IneligibleTestRun([string]$Message) {
    $error = [InvalidOperationException]::new($Message)
    $error.Data['AhkTestIneligible'] = $true
    throw $error
}

# All routes are constructed here. No URL supplied by an event, PR or artifact is
# used for an authenticated request, and source code from the PR is never run.
function Get-TestReleaseContext {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][long]$RunId,
        [Parameter(Mandatory = $true)][scriptblock]$Request
    )
    if ($Repository -cne 'bezoro/ahk-manager' -or $RunId -le 0) { Stop-IneligibleTestRun 'Unexpected preview repository or run.' }
    $base = "https://api.github.com/repos/$Repository"
    $run = & $Request "$base/actions/runs/$RunId"
    if ($run.id -ne $RunId -or $run.status -cne 'completed' -or $run.conclusion -cne 'success' -or $run.event -cne 'pull_request' -or $run.name -cne 'Windows CI' -or $run.path -cne '.github/workflows/windows-ci.yml') { Stop-IneligibleTestRun 'Only a successful pull-request Windows CI run can publish a preview.' }
    if ($null -eq $run.repository -or $null -eq $run.head_repository -or $run.repository.full_name -cne $Repository -or $run.head_repository.full_name -cne $Repository -or $run.repository.id -ne $run.head_repository.id -or $run.repository.id -ne 1430333503) { Stop-IneligibleTestRun 'Forks cannot publish previews.' }
    if ($run.head_sha -cnotmatch '\A[0-9a-f]{40}\z' -or $run.run_number -le 0 -or $run.run_number -gt [int]::MaxValue -or $run.run_attempt -le 0 -or $run.run_attempt -gt [int]::MaxValue) { Stop-IneligibleTestRun 'Invalid preview run identity.' }
    $pulls = @(& $Request "$base/commits/$($run.head_sha)/pulls?per_page=100")
    if ($pulls.Count -gt 100) { throw 'The preview PR association exceeds its bound.' }
    $eligible = @($pulls | Where-Object {
        $_.state -ceq 'open' -and $null -ne $_.head -and $null -ne $_.head.repo -and $null -ne $_.base -and $null -ne $_.base.repo -and $_.head.sha -ceq $run.head_sha -and
        $_.head.repo.id -eq $run.repository.id -and $_.base.repo.id -eq $run.repository.id -and
        $_.base.ref -ceq 'main'
    })
    if ($eligible.Count -ne 1 -or $eligible[0].number -le 0 -or $eligible[0].number -gt [int]::MaxValue) { Stop-IneligibleTestRun 'The tested commit must still be the head of one open same-repository PR targeting main.' }
    $pull = & $Request "$base/pulls/$($eligible[0].number)"
    if ($pull.state -cne 'open' -or $null -eq $pull.head -or $null -eq $pull.head.repo -or $null -eq $pull.base -or $null -eq $pull.base.repo -or $pull.head.sha -cne $run.head_sha -or $pull.head.repo.id -ne $run.repository.id -or $pull.base.repo.id -ne $run.repository.id -or $pull.base.ref -cne 'main') { Stop-IneligibleTestRun 'The pull request changed during provenance inspection.' }
    $artifacts = & $Request "$base/actions/runs/$RunId/artifacts?per_page=100"
    if ($artifacts.total_count -gt 100) { throw 'The preview artifact listing exceeds its bound.' }
    $artifactName = "windows-installer-test-$($run.run_attempt)"
    $installers = @($artifacts.artifacts | Where-Object { $_.name -ceq $artifactName })
    if ($installers.Count -ne 1 -or $installers[0].expired -ne $false -or $installers[0].id -le 0 -or $installers[0].size_in_bytes -le 0 -or $installers[0].size_in_bytes -gt (1GB + 1MB)) { Stop-IneligibleTestRun 'Exactly one current, bounded Windows installer artifact is required.' }
    $artifact = $installers[0]
    if ($artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $run.head_sha -or $artifact.workflow_run.head_repository_id -ne $run.repository.id -or $artifact.digest -cnotmatch '\Asha256:[0-9a-f]{64}\z') { throw 'Artifact provenance does not match the tested source.' }
    return [pscustomobject]@{
        RunId = $RunId
        RunNumber = [int]$run.run_number
        Attempt = [int]$run.run_attempt
        PullRequest = [int]$pull.number
        Commit = $run.head_sha
        ArtifactId = [long]$artifact.id
        ArtifactBytes = [long]$artifact.size_in_bytes
        ArtifactDigest = $artifact.digest
    }
}



function Get-TestReleaseCandidates {
    param([string]$Repository, [long]$RunId, [scriptblock]$Request)
    if ($Repository -cne 'bezoro/ahk-manager' -or $RunId -le 0) { throw 'Unexpected preview trigger repository or run.' }
    $base = "https://api.github.com/repos/$Repository"
    $trigger = & $request "$base/actions/runs/$RunId"
    if ($trigger.id -ne $RunId -or $trigger.status -cne 'completed' -or $trigger.conclusion -cne 'success' -or $trigger.name -cne 'Windows CI' -or $trigger.path -cne '.github/workflows/windows-ci.yml' -or $trigger.repository.id -ne 1430333503 -or $trigger.repository.full_name -cne $Repository) { throw 'The trigger is not an approved completed Windows CI run.' }
    if ($null -eq $trigger.head_repository -or $trigger.head_repository.id -ne $trigger.repository.id) { return @() }
    $rows = @()
    if ($trigger.event -ceq 'pull_request') { $rows = @($trigger) }
    elseif ($trigger.event -ceq 'push' -and $trigger.head_branch -ceq 'main') {
        # Bootstrap activation also publishes previews whose CI completed before the
        # trusted publisher existed on main. No rebuild or manual download is needed.
        $runs = & $request "$base/actions/workflows/windows-ci.yml/runs?event=pull_request&status=success&per_page=100"
        $rows = @($runs.workflow_runs)
        if ($rows.Count -gt 100) { throw 'The preview backfill query exceeds its bound.' }
    }
    $selected = [Collections.Generic.List[long]]::new()
    $pulls = [Collections.Generic.HashSet[int]]::new()
    foreach ($row in $rows) {
        if ($selected.Count -ge 10) { break }
        if ($null -eq $row.head_repository -or $row.head_repository.id -ne 1430333503) { continue }
        try { $context = Get-TestReleaseContext -Repository $Repository -RunId $row.id -Request $request }
        catch {
            if ($_.Exception.Data['AhkTestIneligible'] -eq $true) { Write-Host "Run $($row.id) is no longer eligible for preview publication."; continue }
            throw
        }
        if ($pulls.Add($context.PullRequest)) { $selected.Add($context.RunId) }
    }
    return $selected.ToArray()
}

Export-ModuleMember -Function Get-TestReleaseContext, Get-TestReleaseCandidates
