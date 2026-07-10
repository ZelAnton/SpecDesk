<#
.SYNOPSIS
    Detach the orchestrator's local .work/ state from the versioned tree and fast-forward
    the repository working copy to the published main, without losing local .work content
    and without clobbering un-committed source edits.

.DESCRIPTION
    Background. SpecDesk mixes source with mutable orchestrator state under .work/. Historically
    .work/Active_Task.md, .work/Checkpoint.md, .work/Review.md, .work/Tasks_Done.md,
    .work/Tasks_Queue.md and .work/status.md were tracked. Because they were tracked, the main
    working copy could not be advanced to a newer main without either overwriting the live queue /
    checkpoints with stale history or refusing the update outright, so the running app kept drifting
    behind reviewed-and-merged code (no new spacer modules, old Code -> Formatted path).

    This procedure fixes the root cause once and is safe to re-run:

      1. Refuses to touch anything if the working copy carries un-committed changes OUTSIDE .work/
         (protects author source edits).
      2. Requires the target ref to already ignore .work/ (so the detach cannot silently re-track).
      3. Backs up the live .work/ and records a SHA-256 manifest before mutating.
      4. Moves the live .work/ aside, fast-forwards the working copy to the target ref, removes the
         previously tracked .work/* from the index (content is kept on disk), and commits that
         detach only when there is actually something to untrack.
      5. Restores the live .work/ and verifies every file matches the pre-migration manifest.

    Idempotent: a second run against an already-migrated working copy (nothing tracked under .work/
    and the working copy already at the target commit) makes no change to the tree, the index, the
    branch, or the local .work/ metadata.

    Uses plain git plumbing so it works on any git working copy. In a jujutsu-colocated repo the
    resulting git commit is imported by jj on its next command.

.PARAMETER RepoRoot
    Absolute path to the git working copy to migrate. Defaults to the repository root inferred from
    this script's location (scripts/..). Must be a git top-level directory.

.PARAMETER TargetRef
    The published ref to bring the working copy to. Defaults to 'main'.

.PARAMETER BackupRoot
    Directory that will receive the timestamped .work/ backup. Defaults to a SpecDesk folder under
    the system temp directory. Never place this inside .work/.

.PARAMETER NoBackup
    Skip the on-disk backup copy. The live .work/ is still moved aside and restored, and checksums
    are still verified, but no independent safety copy is written. Not recommended for production.

.PARAMETER DryRun
    Report the actions that would be taken and exit without mutating the repository or .work/.

.EXAMPLE
    pwsh -File scripts/restore-main-worktree.ps1

.EXAMPLE
    pwsh -File scripts/restore-main-worktree.ps1 -TargetRef main -DryRun
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$TargetRef = 'main',
    [string]$BackupRoot,
    [switch]$NoBackup,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------------------------

function Write-Step {
    param([string]$Message)
    Write-Host "[restore-main-worktree] $Message"
}

function Invoke-Git {
    <#
        Run git against $script:RepoRoot and return stdout lines. Throws on a non-zero exit unless
        -AllowFail is set (callers that inspect the exit code, e.g. check-ignore, pass -AllowFail).
        Combined stderr is surfaced in the thrown message so a failure is diagnosable.
    #>
    param(
        [Parameter(Mandatory)][string[]]$GitArgs,
        [switch]$AllowFail
    )

    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $allArgs = @('-C', $script:RepoRoot) + $GitArgs
        # Capture stdout as PowerShell string lines; keep stderr in a file so git's progress /
        # warning noise never pollutes parsed output. Drop to ErrorActionPreference=Continue for the
        # native call: Windows PowerShell 5.1 otherwise turns a git command's informational stderr
        # (e.g. checkout's "Previous HEAD position was ...") into a terminating error. Correctness is
        # judged solely by the exit code below.
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $stdout = & git @allArgs 2>$stderrFile
            $script:LastGitExit = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousEap
        }
        if ($script:LastGitExit -ne 0 -and -not $AllowFail) {
            $stderr = Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue
            $shown = if ([string]::IsNullOrWhiteSpace($stderr)) { ($stdout -join "`n") } else { $stderr }
            throw "git $($GitArgs -join ' ') failed (exit $($script:LastGitExit)): $($shown.Trim())"
        }
        # Always hand back a real array (the leading comma stops PowerShell from unwrapping a
        # single-element result to a scalar string, which would make callers' [0]/.Count wrong).
        if ($null -eq $stdout) {
            return , @()
        }
        $lines = @(@($stdout) | Where-Object { $_ -ne '' })
        return , $lines
    }
    finally {
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
    }
}

function Get-Sha256 {
    <#
        Hash a file with the .NET SHA-256 API rather than Get-FileHash: the cmdlet is absent from
        some Windows PowerShell 5.1 installs, whereas this works identically on 5.1 and 7.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $bytes = $sha.ComputeHash($stream)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
    return [System.BitConverter]::ToString($bytes).Replace('-', '')
}

function Get-WorkManifest {
    <#
        Build an ordered map of <forward-slash relative path> -> SHA-256 for every file under the
        given directory. An empty or absent directory yields an empty map.
    #>
    param([Parameter(Mandatory)][string]$Directory)

    $manifest = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Directory)) {
        return $manifest
    }
    $root = (Resolve-Path -LiteralPath $Directory).Path
    $files = Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
        $manifest[$relative] = Get-Sha256 -Path $file.FullName
    }
    return $manifest
}

function Compare-Manifest {
    <#
        Return the list of human-readable differences between an expected and an actual manifest.
        Empty list means the restore reproduced the pre-migration .work/ byte-for-byte.
    #>
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Expected,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Actual
    )

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Expected.Keys) {
        if (-not $Actual.Contains($key)) {
            $problems.Add("missing after restore: $key")
        }
        elseif ($Actual[$key] -ne $Expected[$key]) {
            $problems.Add("checksum mismatch: $key")
        }
    }
    foreach ($key in $Actual.Keys) {
        if (-not $Expected.Contains($key)) {
            $problems.Add("unexpected extra file after restore: $key")
        }
    }
    # Leading comma: return the list as a single object so an empty result stays a zero-length
    # list instead of being enumerated away to $null (which would break the caller's .Count).
    return , $problems
}

# ----------------------------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------------------------

function Invoke-Migration {
    # --- Resolve and validate the repository root -------------------------------------------------
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = Split-Path -Parent $PSScriptRoot
    }
    if (-not (Test-Path -LiteralPath $RepoRoot)) {
        throw "RepoRoot does not exist: $RepoRoot"
    }
    $script:RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

    $topLevel = (Invoke-Git @('rev-parse', '--show-toplevel'))[0]
    $topLevelFull = (Resolve-Path -LiteralPath $topLevel).Path
    if ($topLevelFull -ne $script:RepoRoot) {
        throw "RepoRoot must be the git top-level directory. Got '$($script:RepoRoot)', git reports '$topLevelFull'."
    }

    # --- Resolve the target ref ------------------------------------------------------------------
    $targetCommit = (Invoke-Git @('rev-parse', '--verify', "$TargetRef^{commit}") -AllowFail)
    if ($script:LastGitExit -ne 0 -or $targetCommit.Count -eq 0) {
        throw "Target ref '$TargetRef' does not resolve to a commit in $($script:RepoRoot)."
    }
    $targetCommit = $targetCommit[0]

    $workDir = Join-Path $script:RepoRoot '.work'

    Write-Step "Repository : $($script:RepoRoot)"
    Write-Step "Target ref : $TargetRef ($($targetCommit.Substring(0, 12)))"

    # --- Guard 1: refuse on un-committed changes outside .work/ ----------------------------------
    # Porcelain paths are repo-root-relative; core.quotePath=false keeps non-ASCII paths literal.
    $statusLines = Invoke-Git @('-c', 'core.quotePath=false', 'status', '--porcelain=v1', '--untracked-files=all', '--no-renames')
    $foreign = New-Object System.Collections.Generic.List[string]
    foreach ($line in $statusLines) {
        if ($line.Length -lt 4) { continue }
        $path = $line.Substring(3).Trim('"')
        if ($path -eq '.work' -or $path.StartsWith('.work/')) { continue }
        $foreign.Add($path)
    }
    if ($foreign.Count -gt 0) {
        throw "Refusing to migrate: un-committed changes outside .work/:`n  " + ($foreign -join "`n  ")
    }

    # --- Guard 2: the target must already ignore .work/ ------------------------------------------
    # Without this the detach would only untrack; the next commit could silently re-add the live
    # state. Read the target's .gitignore rather than the (possibly stale) working copy's.
    $targetGitignore = Invoke-Git @('show', "${TargetRef}:.gitignore") -AllowFail
    $ignoresWork = $false
    if ($script:LastGitExit -eq 0) {
        foreach ($rule in $targetGitignore) {
            $trimmed = $rule.Trim()
            if ($trimmed -eq '.work/' -or $trimmed -eq '.work' -or $trimmed -eq '/.work/' -or $trimmed -eq '/.work') {
                $ignoresWork = $true
                break
            }
        }
    }
    if (-not $ignoresWork) {
        throw "Target ref '$TargetRef' does not ignore .work/ in its .gitignore; refusing to migrate (the detach would risk re-tracking local state)."
    }

    # --- Decide what work is needed --------------------------------------------------------------
    $trackedWork = Invoke-Git @('ls-files', '--', '.work')
    $needsUntrack = $trackedWork.Count -gt 0
    $headCommit = (Invoke-Git @('rev-parse', 'HEAD'))[0]
    $needsUpdate = $headCommit -ne $targetCommit

    if (-not $needsUntrack -and -not $needsUpdate) {
        Write-Step "Already migrated: .work/ is untracked and the working copy is at $TargetRef. No changes."
        return 0
    }

    $planParts = @()
    if ($needsUpdate) {
        $planParts += "fast-forward working copy $($headCommit.Substring(0, 12)) -> $($targetCommit.Substring(0, 12))"
    }
    if ($needsUntrack) {
        $planParts += "untrack $($trackedWork.Count) file(s) under .work/"
    }
    Write-Step ("Plan       : " + ($planParts -join '; '))

    if ($DryRun) {
        Write-Step "Dry run: no changes made."
        return 0
    }

    # --- Backup + manifest -----------------------------------------------------------------------
    $manifest = Get-WorkManifest -Directory $workDir
    $backupDir = $null
    if (-not $NoBackup -and (Test-Path -LiteralPath $workDir)) {
        if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
            $BackupRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'specdesk-work-backups'
        }
        if (-not (Test-Path -LiteralPath $BackupRoot)) {
            New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
        }
        $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
        $backupDir = Join-Path $BackupRoot "work-$stamp"
        Copy-Item -LiteralPath $workDir -Destination $backupDir -Recurse -Force
        $manifestPath = Join-Path $backupDir 'manifest.sha256'
        $manifest.GetEnumerator() | ForEach-Object { "$($_.Value)  $($_.Key)" } | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        Write-Step "Backup     : $backupDir ($($manifest.Count) file(s))"
    }

    # --- Migrate ---------------------------------------------------------------------------------
    # Move the live .work/ aside so the fast-forward cannot overwrite the tracked files with stale
    # history. A rename on the same volume is atomic; the staging dir is untracked and transient.
    $staging = $null
    if (Test-Path -LiteralPath $workDir) {
        $staging = Join-Path $script:RepoRoot (".work.migrating-" + (Get-Date).ToString('yyyyMMddHHmmssfff'))
        Rename-Item -LiteralPath $workDir -NewName (Split-Path -Leaf $staging)
    }

    try {
        # Fast-forward the working copy (and HEAD) to the target ref.
        if ($needsUpdate) {
            Invoke-Git @('checkout', '--force', '--quiet', $TargetRef) | Out-Null
        }

        # Untrack the previously tracked .work/* (content stays on disk) and commit the detach only
        # when there is actually something to remove.
        $trackedAfter = Invoke-Git @('ls-files', '--', '.work')
        if ($trackedAfter.Count -gt 0) {
            Invoke-Git @('rm', '-r', '--cached', '--quiet', '--', '.work') | Out-Null
            Invoke-Git @('commit', '--quiet', '--no-verify', '-m', 'Detach .work orchestrator state from the versioned tree') | Out-Null
            Write-Step "Detached   : removed $($trackedAfter.Count) file(s) under .work/ from tracking"
        }

        # Drop whatever .work/ the checkout materialised (the stale tracked versions), then restore
        # the live state that was moved aside.
        if (Test-Path -LiteralPath $workDir) {
            Remove-Item -LiteralPath $workDir -Recurse -Force
        }
        if ($null -ne $staging) {
            Rename-Item -LiteralPath $staging -NewName '.work'
        }
    }
    catch {
        # Never leave the live .work/ stranded in staging or partially replaced by stale history.
        # Prefer the exact bytes moved aside (staging); fall back to the independent backup copy.
        if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
            if (Test-Path -LiteralPath $workDir) {
                Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            Rename-Item -LiteralPath $staging -NewName '.work'
        }
        elseif ($null -ne $backupDir -and (Test-Path -LiteralPath $backupDir)) {
            if (Test-Path -LiteralPath $workDir) {
                Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            Copy-Item -LiteralPath $backupDir -Destination $workDir -Recurse -Force
        }
        throw
    }

    # --- Verify the restored .work/ matches the pre-migration manifest ---------------------------
    $restored = Get-WorkManifest -Directory $workDir
    $problems = Compare-Manifest -Expected $manifest -Actual $restored
    if ($problems.Count -gt 0) {
        throw "Checksum verification failed after restore:`n  " + ($problems -join "`n  ")
    }
    Write-Step "Verified   : $($restored.Count) .work/ file(s) match the pre-migration checksums"

    $newHead = (Invoke-Git @('rev-parse', 'HEAD'))[0]
    Write-Step "Done       : working copy at $($newHead.Substring(0, 12)); .work/ preserved and untracked."
    return 0
}

try {
    $exitCode = Invoke-Migration
    exit $exitCode
}
catch {
    Write-Host "[restore-main-worktree] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "[restore-main-worktree] at line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())" -ForegroundColor Red
    exit 1
}
