<#
.SYNOPSIS
    Fail-closed check that a working copy is not lagging behind the local published main. Read-only:
    it never touches a file, only reports whether the checked-out sources already include main.

.DESCRIPTION
    Background. SpecDesk keeps mutable orchestrator state under .work/ and, after T-105, that state is
    untracked so the repository working copy can be fast-forwarded to the published main. But nothing
    yet *forced* that to happen: every published task advances the local `main` bookmark, and until the
    main working copy is re-synced its on-disk sources are stale — planner would then analyse an old
    tree as the current implementation, and a local build/run of SpecDesk.Host would compile and serve
    an old UI. T-105 fixed the root cause once; this check turns "please re-sync" into an executable,
    fail-closed gate.

    What it decides.
      * ROLE — is this the repository's main working copy, or an isolated task worktree? The two are
        held to different rules and the role is determined from explicit VCS structure, never guessed
        from file content:
          - a git LINKED worktree (its --git-dir differs from --git-common-dir), or
          - a jj workspace nested below the git top-level (a .jj/ directory at -RepoRoot whose path is
            not the git top-level)
        is an isolated task worktree; anything else is the main working copy. Pass -Role to override.
      * CURRENCY (main working copy only) — compare the checked-out HEAD with the target ref (`main`):
          - HEAD equals main .............. current  -> ok   (exit 0)
          - HEAD is a strict ancestor ..... behind   -> STALE (exit 1)   <-- the drift this exists to catch
          - main is an ancestor of HEAD ... ahead    -> ok   (exit 0, sources already include main)
          - the two have diverged ......... diverged -> ok   (exit 0, not the drift scenario; e.g. a
                                                          topic/PR checkout or CI building an arbitrary
                                                          commit — never falsely block legitimate work)
          - main does not resolve ......... no-target-> ok   (exit 0, e.g. a fresh CI checkout with no
                                                          local main bookmark — cannot be "behind" it)
        An isolated task worktree is always ok: a parallel publication advancing main must never make a
        valid in-progress task tree look broken.

    Exit codes: 0 = ok, 1 = stale (behind main), 2 = usage/tool error. The distinct stale code lets a
    build target treat only a genuine drift (1) as fatal while degrading a tooling failure (2) to a
    warning, and never false-blocking an isolated worktree (0).

    The recovery for a stale main working copy is the T-105 procedure: scripts/restore-main-worktree.ps1
    fast-forwards it to main while preserving the live .work/ state.

    Uses plain git plumbing so it works on any git working copy, and understands the jj-colocated layout
    the orchestrator actually uses (task worktrees are jj workspaces nested under the ignored .work/).

.PARAMETER RepoRoot
    Absolute path to the working copy to check. Defaults to the repository root inferred from this
    script's location (scripts/..).

.PARAMETER TargetRef
    The published ref the main working copy must already include. Defaults to 'main'.

.PARAMETER Role
    'auto' (default) detects the role from VCS structure as described above; 'main-worktree' or
    'task-worktree' force it. Forcing 'main-worktree' is the fail-closed choice when in doubt.

.PARAMETER ExpectedBase
    Optional. For a task worktree, a commit-ish the worktree is expected to descend from. When given,
    the check confirms the worktree actually contains it (guards against an unrelated tree); it never
    requires the worktree to include the *latest* main.

.PARAMETER Json
    Emit the machine-readable result as a single compact JSON object on stdout (for planner/processor to
    record). Human-readable step lines still go to stderr so they never pollute the JSON on stdout.

.EXAMPLE
    pwsh -File scripts/assert-main-current.ps1

.EXAMPLE
    pwsh -File scripts/assert-main-current.ps1 -Json
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$TargetRef = 'main',
    [ValidateSet('auto', 'main-worktree', 'task-worktree')]
    [string]$Role = 'auto',
    [string]$ExpectedBase,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Exit codes (documented above). Named so the intent is legible at every `return`.
$script:ExitOk = 0
$script:ExitStale = 1
$script:ExitError = 2

# ----------------------------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------------------------

function Write-Note {
    # Human-readable progress goes to the *error* stream on purpose: -Json writes the result object to
    # stdout, and mixing notes into stdout would make that output unparseable. Callers that only want
    # the JSON redirect stderr away; humans see both.
    param([string]$Message)
    [Console]::Error.WriteLine("[assert-main-current] $Message")
}

function Invoke-Git {
    <#
        Run git against $script:RepoRoot and return stdout lines. Throws on a non-zero exit unless
        -AllowFail is set (callers that inspect the exit code, e.g. merge-base --is-ancestor, pass it).
        The last exit code is always left in $script:LastGitExit.
    #>
    param(
        [Parameter(Mandatory)][string[]]$GitArgs,
        [switch]$AllowFail
    )

    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $allArgs = @('-C', $script:RepoRoot) + $GitArgs
        # Drop to ErrorActionPreference=Continue for the native call: Windows PowerShell 5.1 otherwise
        # turns git's informational stderr into a terminating error. Correctness is judged by exit code.
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
        # Leading comma: hand back a real array so a single-line result never unwraps to a scalar.
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

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    return (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\', '/')
}

function Test-SamePath {
    # Windows paths are case-insensitive; compare the resolved, separator-trimmed forms accordingly.
    param([string]$A, [string]$B)
    return [string]::Equals($A, $B, [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-Role {
    <#
        Decide the role from explicit VCS structure only. Fail-closed: unless there is a positive signal
        that this is an isolated tree (a git linked worktree, or a jj workspace nested under the git
        top-level), treat it as the main working copy so currency is actually enforced.
    #>
    param([Parameter(Mandatory)][string]$Root)

    # 1) A git LINKED worktree keeps its per-worktree git dir separate from the shared common dir.
    $gitDir = Resolve-FullPath (Invoke-Git @('rev-parse', '--absolute-git-dir'))[0]
    $commonDirRaw = (Invoke-Git @('rev-parse', '--git-common-dir'))[0]
    if (-not [System.IO.Path]::IsPathRooted($commonDirRaw)) {
        $commonDirRaw = Join-Path $Root $commonDirRaw
    }
    $commonDir = Resolve-FullPath $commonDirRaw
    if (-not (Test-SamePath $gitDir $commonDir)) {
        return 'task-worktree'
    }

    # 2) A jj workspace nested below the git top-level: git discovery walked up to the shared repo, so
    #    -RepoRoot is a real directory that is not git's top-level yet carries its own .jj/ workspace.
    $topLevel = Resolve-FullPath (Invoke-Git @('rev-parse', '--show-toplevel'))[0]
    if (-not (Test-SamePath $topLevel $Root) -and (Test-Path -LiteralPath (Join-Path $Root '.jj'))) {
        return 'task-worktree'
    }

    return 'main-worktree'
}

function New-Result {
    param(
        [string]$ResolvedRole,
        [string]$Relation,
        [string]$Verdict,
        [string]$Head,
        [string]$Target,
        [string]$Reason,
        [string]$Recovery
    )
    return [ordered]@{
        role      = $ResolvedRole
        repoRoot  = $script:RepoRoot
        targetRef = $TargetRef
        head      = $Head
        target    = $Target
        relation  = $Relation
        verdict   = $Verdict
        reason    = $Reason
        recovery  = $Recovery
        checkedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Write-Result {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Result)
    if ($Json) {
        # Write straight to the process stdout rather than the PowerShell success stream: Invoke-Check's
        # return value is captured into $exitCode by the caller, so a Write-Output here would be swallowed
        # into that same value (corrupting the exit code) instead of reaching a stdout-parsing caller.
        [Console]::Out.WriteLine(($Result | ConvertTo-Json -Compress))
    }
    $head = if ($Result.head) { $Result.head.Substring(0, [Math]::Min(12, $Result.head.Length)) } else { '(none)' }
    $target = if ($Result.target) { $Result.target.Substring(0, [Math]::Min(12, $Result.target.Length)) } else { '(none)' }
    Write-Note "role=$($Result.role) relation=$($Result.relation) verdict=$($Result.verdict) head=$head target=$target"
    Write-Note $Result.reason
    if ($Result.verdict -eq 'stale') {
        Write-Note "Recover with: $($Result.recovery)"
    }
}

# ----------------------------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------------------------

function Invoke-Check {
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $RepoRoot = Split-Path -Parent $PSScriptRoot
    }
    if (-not (Test-Path -LiteralPath $RepoRoot)) {
        throw "RepoRoot does not exist: $RepoRoot"
    }
    $script:RepoRoot = Resolve-FullPath $RepoRoot

    # Must be inside a git working tree at all.
    $insideTree = (Invoke-Git @('rev-parse', '--is-inside-work-tree') -AllowFail)
    if ($script:LastGitExit -ne 0 -or $insideTree.Count -eq 0 -or $insideTree[0] -ne 'true') {
        throw "Not inside a git working tree: $($script:RepoRoot)"
    }

    $resolvedRole = if ($Role -eq 'auto') { Resolve-Role -Root $script:RepoRoot } else { $Role }
    $recovery = "pwsh -File scripts/restore-main-worktree.ps1 -TargetRef $TargetRef"

    # --- Isolated task worktree: never held to "must include the latest main" -----------------------
    if ($resolvedRole -eq 'task-worktree') {
        $reason = 'Isolated task worktree: not required to include the latest published main (a parallel publication may legitimately have advanced main).'
        if (-not [string]::IsNullOrWhiteSpace($ExpectedBase)) {
            $baseCommit = (Invoke-Git @('rev-parse', '--verify', "$ExpectedBase^{commit}") -AllowFail)
            if ($script:LastGitExit -eq 0 -and $baseCommit.Count -gt 0) {
                Invoke-Git @('merge-base', '--is-ancestor', $baseCommit[0], 'HEAD') -AllowFail | Out-Null
                if ($script:LastGitExit -ne 0) {
                    $result = New-Result -ResolvedRole $resolvedRole -Relation 'unrelated-base' -Verdict 'stale' `
                        -Head '' -Target $baseCommit[0] `
                        -Reason "Task worktree does not contain its expected base $ExpectedBase." -Recovery $recovery
                    Write-Result -Result $result
                    return $script:ExitStale
                }
            }
        }
        $result = New-Result -ResolvedRole $resolvedRole -Relation 'isolated-worktree' -Verdict 'ok' `
            -Head '' -Target '' -Reason $reason -Recovery $recovery
        Write-Result -Result $result
        return $script:ExitOk
    }

    # --- Main working copy: it must already include the published main ------------------------------
    $targetResolved = (Invoke-Git @('rev-parse', '--verify', "$TargetRef^{commit}") -AllowFail)
    if ($script:LastGitExit -ne 0 -or $targetResolved.Count -eq 0) {
        $result = New-Result -ResolvedRole $resolvedRole -Relation 'no-target' -Verdict 'ok' `
            -Head '' -Target '' `
            -Reason "Target ref '$TargetRef' does not resolve locally; cannot be behind it (e.g. a fresh CI checkout)." `
            -Recovery $recovery
        Write-Result -Result $result
        return $script:ExitOk
    }
    $targetCommit = $targetResolved[0]

    $headResolved = (Invoke-Git @('rev-parse', '--verify', 'HEAD') -AllowFail)
    if ($script:LastGitExit -ne 0 -or $headResolved.Count -eq 0) {
        $result = New-Result -ResolvedRole $resolvedRole -Relation 'no-head' -Verdict 'ok' `
            -Head '' -Target $targetCommit `
            -Reason 'HEAD does not resolve (unborn branch); nothing checked out to be stale.' -Recovery $recovery
        Write-Result -Result $result
        return $script:ExitOk
    }
    $headCommit = $headResolved[0]

    if ($headCommit -eq $targetCommit) {
        $result = New-Result -ResolvedRole $resolvedRole -Relation 'current' -Verdict 'ok' `
            -Head $headCommit -Target $targetCommit `
            -Reason "Main working copy is at '$TargetRef'." -Recovery $recovery
        Write-Result -Result $result
        return $script:ExitOk
    }

    # HEAD strictly behind main (a proper ancestor) is the drift this gate exists to catch.
    Invoke-Git @('merge-base', '--is-ancestor', $headCommit, $targetCommit) -AllowFail | Out-Null
    $headIsAncestorOfTarget = ($script:LastGitExit -eq 0)
    if ($headIsAncestorOfTarget) {
        $result = New-Result -ResolvedRole $resolvedRole -Relation 'behind' -Verdict 'stale' `
            -Head $headCommit -Target $targetCommit `
            -Reason "Main working copy $($headCommit.Substring(0,12)) is behind '$TargetRef' $($targetCommit.Substring(0,12)); its sources do NOT include the published main. Planning or building from it would use the old implementation." `
            -Recovery $recovery
        Write-Result -Result $result
        return $script:ExitStale
    }

    # main is an ancestor of HEAD (already included) -> ahead; otherwise the two diverged. Neither is
    # the drift scenario, so neither blocks: a diverged topic/PR checkout is legitimate, not stale.
    Invoke-Git @('merge-base', '--is-ancestor', $targetCommit, $headCommit) -AllowFail | Out-Null
    $targetIsAncestorOfHead = ($script:LastGitExit -eq 0)
    if ($targetIsAncestorOfHead) {
        $result = New-Result -ResolvedRole $resolvedRole -Relation 'ahead' -Verdict 'ok' `
            -Head $headCommit -Target $targetCommit `
            -Reason "Working copy already includes '$TargetRef' (it is ahead)." -Recovery $recovery
    }
    else {
        $result = New-Result -ResolvedRole $resolvedRole -Relation 'diverged' -Verdict 'ok' `
            -Head $headCommit -Target $targetCommit `
            -Reason "Working copy has diverged from '$TargetRef' (a topic/PR checkout, not the main-copy drift); not blocked." `
            -Recovery $recovery
    }
    Write-Result -Result $result
    return $script:ExitOk
}

try {
    $exitCode = Invoke-Check
    exit $exitCode
}
catch {
    [Console]::Error.WriteLine("[assert-main-current] ERROR: $($_.Exception.Message)")
    [Console]::Error.WriteLine("[assert-main-current] at line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())")
    exit $script:ExitError
}
