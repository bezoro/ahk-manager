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
    $attempt = [int]$run.run_attempt
    if ($attempt -gt 1) {
        # A downstream-only retry reuses the latest successful producer job.
        $jobs = & $Request "$base/actions/runs/$RunId/jobs?filter=all&per_page=100"
        if ($jobs.total_count -gt 100 -or $jobs.jobs.Count -ne $jobs.total_count) { throw 'The preview job listing exceeds its bound or is incomplete.' }
        $builds = @($jobs.jobs | Where-Object { $_.name -ceq 'Build and isolated tests (Windows x64)' } | Sort-Object { [int]$_.run_attempt } -Descending)
        if (-not $builds.Count) { Stop-IneligibleTestRun 'The package producer job is missing.' }
        $build = $builds[0]
        if ($build.run_id -ne $RunId -or $build.head_sha -cne $run.head_sha -or $build.conclusion -cne 'success' -or $build.run_attempt -lt 1 -or $build.run_attempt -gt $attempt -or @($builds | Where-Object run_attempt -EQ $build.run_attempt).Count -ne 1) { Stop-IneligibleTestRun 'The latest package producer must be an unambiguous successful build of this source.' }
        $attempt = [int]$build.run_attempt
    }
    $artifactName = "windows-portable-test-$attempt"
    $packages = @($artifacts.artifacts | Where-Object { $_.name -ceq $artifactName })
    if ($packages.Count -ne 1 -or $packages[0].expired -ne $false -or $packages[0].id -le 0 -or $packages[0].size_in_bytes -le 0 -or $packages[0].size_in_bytes -gt 2GB) { Stop-IneligibleTestRun 'Exactly one current, bounded portable package artifact is required.' }
    $artifact = $packages[0]
    if ($artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $run.head_sha -or $artifact.workflow_run.head_repository_id -ne $run.repository.id -or $artifact.digest -cnotmatch '\Asha256:[0-9a-f]{64}\z') { throw 'Artifact provenance does not match the tested source.' }
    return [pscustomobject]@{
        RunId = $RunId
        RunNumber = [int]$run.run_number
        Attempt = $attempt
        ValidationAttempt = [int]$run.run_attempt
        PullRequest = [int]$pull.number
        Commit = $run.head_sha
        ArtifactId = [long]$artifact.id
        ArtifactBytes = [long]$artifact.size_in_bytes
        ArtifactDigest = $artifact.digest
    }
}

Export-ModuleMember -Function Get-TestReleaseContext
