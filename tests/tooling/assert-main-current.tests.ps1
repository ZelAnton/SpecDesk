<#
.SYNOPSIS
    Self-contained tests for scripts/assert-main-current.ps1.

.DESCRIPTION
    Each test builds a throwaway git repository under the system temp directory, runs the check script
    as a child process (so its `exit` never touches this harness), and asserts the verdict, exit code,
    and machine-readable JSON. No external test framework is required — run it directly:

        pwsh -File tests/tooling/assert-main-current.tests.ps1

    Exit code is 0 when every test passes, 1 otherwise. Temp repositories are removed on exit.

    Coverage mirrors the task's required scenarios:
      * main advanced while the main working copy stayed on an ancestor -> STALE (exit 1), naming both
        revisions and the recovery command,
      * a re-synced working copy (HEAD == main) -> ok,
      * safe (read-only) behaviour with un-committed local changes present,
      * NO false failure in a valid isolated task worktree (a git linked worktree and a jj-style nested
        workspace), even after main advanced,
      * a diverged topic/PR checkout is not flagged as stale,
      * a fresh checkout with no local main is not "behind" it,
      * the JSON result carries role / relation / verdict / revisions / recovery,
      * an explicit -Role override.
#>
#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ScriptUnderTest = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/assert-main-current.ps1'
if (-not (Test-Path -LiteralPath $script:ScriptUnderTest)) {
    throw "Script under test not found: $($script:ScriptUnderTest)"
}
$script:PsExe = (Get-Process -Id $PID).Path
$script:TmpRoot = Join-Path $env:TEMP ('specdesk-current-tests-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $script:TmpRoot -Force | Out-Null

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:TestCount = 0
$script:PassCount = 0
$script:CurrentErrors = $null

# ----------------------------------------------------------------------------------------------
# Harness helpers
# ----------------------------------------------------------------------------------------------

function Test-Case {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    $script:TestCount++
    $script:CurrentErrors = New-Object System.Collections.Generic.List[string]
    Write-Host "== $Name =="
    try {
        & $Body
    }
    catch {
        $script:CurrentErrors.Add("unexpected exception: $($_.Exception.Message)")
    }
    if ($script:CurrentErrors.Count -eq 0) {
        $script:PassCount++
        Write-Host "  PASS" -ForegroundColor Green
    }
    else {
        foreach ($e in $script:CurrentErrors) {
            Write-Host "  FAIL: $e" -ForegroundColor Red
            $script:Failures.Add("[$Name] $e")
        }
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        $script:CurrentErrors.Add($Message)
    }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    if ($Actual -ne $Expected) {
        $script:CurrentErrors.Add("$Message (expected '$Expected', got '$Actual')")
    }
}

function Assert-Match {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -notmatch $Pattern) {
        $script:CurrentErrors.Add("$Message (pattern '$Pattern' not found)")
    }
}

function Invoke-RepoGit {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string[]]$GitArgs)
    $allArgs = @('-C', $Repo) + $GitArgs
    $out = & git @allArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArgs -join ' ') failed: $out"
    }
    if ($null -eq $out) {
        return , @()
    }
    return , @(@($out) | Where-Object { "$_".Trim() -ne '' })
}

function New-TempRepo {
    param([string]$InitialBranch = 'main')
    $dir = Join-Path $script:TmpRoot ([System.Guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Invoke-RepoGit $dir @('init', '-q', '-b', $InitialBranch) | Out-Null
    Invoke-RepoGit $dir @('config', 'user.email', 'tooling-test@specdesk.local') | Out-Null
    Invoke-RepoGit $dir @('config', 'user.name', 'SpecDesk Tooling Test') | Out-Null
    Invoke-RepoGit $dir @('config', 'commit.gpgsign', 'false') | Out-Null
    Invoke-RepoGit $dir @('config', 'core.autocrlf', 'false') | Out-Null
    return $dir
}

function Set-FileContent {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Content)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Set-Content -LiteralPath $Path -Value $Content -NoNewline -Encoding UTF8
}

function Add-Commit {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string]$Message)
    Invoke-RepoGit $Repo @('add', '-A') | Out-Null
    Invoke-RepoGit $Repo @('commit', '-q', '-m', $Message) | Out-Null
    return (Invoke-RepoGit $Repo @('rev-parse', 'HEAD'))[0]
}

# Run the check as a child process, capturing stdout (the JSON/result) and stderr (human notes)
# separately so the JSON can be parsed cleanly.
function Invoke-Check {
    param([Parameter(Mandatory)][string]$Repo, [string[]]$ExtraArgs = @())
    $fileArgs = @('-NoProfile', '-File', $script:ScriptUnderTest, '-RepoRoot', $Repo) + $ExtraArgs
    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $script:PsExe -ArgumentList $fileArgs -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
        $out = (Get-Content -LiteralPath $stdoutFile -Raw)
        $err = (Get-Content -LiteralPath $stderrFile -Raw)
    }
    finally {
        Remove-Item -LiteralPath $stdoutFile, $stderrFile -Force -ErrorAction SilentlyContinue
    }
    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($out)) {
        $json = $out | ConvertFrom-Json
    }
    return [pscustomobject]@{ ExitCode = $proc.ExitCode; StdOut = $out; StdErr = $err; Json = $json }
}

# A repo whose main has advanced past the checked-out HEAD: HEAD sits on the first commit (an ancestor)
# while main points at the second — the exact drift the gate must catch. Returns the repo path.
function New-BehindRepo {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    $base = Add-Commit -Repo $repo -Message 'commit1'
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v2'
    Set-FileContent (Join-Path $repo 'src/new.ts') 'ADDED'
    Add-Commit -Repo $repo -Message 'commit2' | Out-Null
    # Detach onto the ancestor: the working copy now lags main by one commit.
    Invoke-RepoGit $repo @('checkout', '-q', $base) | Out-Null
    return $repo
}

# ----------------------------------------------------------------------------------------------
# Tests
# ----------------------------------------------------------------------------------------------

Test-Case 'flags a main working copy that lags behind main as stale' {
    $repo = New-BehindRepo
    $result = Invoke-Check -Repo $repo -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 1 "stale drift must exit 1 (stderr: $($result.StdErr))"
    Assert-True ($null -ne $result.Json) 'a JSON result is emitted on stdout'
    Assert-Equal $result.Json.role 'main-worktree' 'role is the main working copy'
    Assert-Equal $result.Json.relation 'behind' 'relation is behind'
    Assert-Equal $result.Json.verdict 'stale' 'verdict is stale'
    Assert-True ($result.Json.head.Length -ge 7) 'actual (head) revision reported'
    Assert-True ($result.Json.target.Length -ge 7) 'expected (target) revision reported'
    Assert-Match $result.Json.recovery 'restore-main-worktree' 'recovery names the T-105 procedure'
}

Test-Case 'passes a working copy that has been re-synced to main' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    Add-Commit -Repo $repo -Message 'commit1' | Out-Null
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v2'
    Add-Commit -Repo $repo -Message 'commit2' | Out-Null

    $result = Invoke-Check -Repo $repo -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 0 "a current working copy must pass (stderr: $($result.StdErr))"
    Assert-Equal $result.Json.relation 'current' 'relation is current'
    Assert-Equal $result.Json.verdict 'ok' 'verdict is ok'
}

Test-Case 'is read-only: un-committed local changes do not change the stale verdict or the tree' {
    $repo = New-BehindRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'DIRTY-LOCAL-EDIT'
    $headBefore = (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0]
    $statusBefore = (Invoke-RepoGit $repo @('status', '--porcelain=v1')) -join "`n"

    $result = Invoke-Check -Repo $repo -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 1 'still stale despite the dirty tree'
    Assert-Equal (Get-Content -LiteralPath (Join-Path $repo 'src/app.ts') -Raw) 'DIRTY-LOCAL-EDIT' 'local edit left untouched'
    Assert-Equal (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0] $headBefore 'HEAD unchanged (no mutation)'
    Assert-Equal ((Invoke-RepoGit $repo @('status', '--porcelain=v1')) -join "`n") $statusBefore 'working tree status unchanged'
}

Test-Case 'passes a dirty but current working copy' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    Add-Commit -Repo $repo -Message 'commit1' | Out-Null
    Set-FileContent (Join-Path $repo 'src/app.ts') 'DIRTY'

    $result = Invoke-Check -Repo $repo -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 0 'current + dirty still passes'
    Assert-Equal $result.Json.relation 'current' 'relation is current'
}

Test-Case 'does not falsely fail a git linked worktree even after main advanced' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    Add-Commit -Repo $repo -Message 'commit1' | Out-Null
    # Add the linked worktree at the current tip on its own branch, THEN advance main in the main repo.
    $wt = Join-Path $script:TmpRoot ('wt-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))
    Invoke-RepoGit $repo @('worktree', 'add', '-q', '-b', 'task/T-1', $wt) | Out-Null
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v2'
    Add-Commit -Repo $repo -Message 'commit2' | Out-Null

    $result = Invoke-Check -Repo $wt -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 0 "a linked task worktree must not be flagged (stderr: $($result.StdErr))"
    Assert-Equal $result.Json.role 'task-worktree' 'role auto-detected as task worktree'
    Assert-Equal $result.Json.relation 'isolated-worktree' 'isolated worktrees skip the currency comparison'
}

Test-Case 'does not falsely fail a jj-style nested workspace even after main advanced' {
    $repo = New-BehindRepo
    # A jj workspace nested under the (ignored) .work/ tree: git discovery walks up to the shared repo,
    # but the directory carries its own .jj/ and is not git's top-level. Git alone would report the
    # shared repo's (behind) HEAD, so the role must be detected structurally, not from the git HEAD.
    $ws = Join-Path $repo '.work/worktrees/T-1'
    New-Item -ItemType Directory -Path (Join-Path $ws '.jj') -Force | Out-Null
    Set-FileContent (Join-Path $ws 'src/app.ts') 'workspace content'

    $result = Invoke-Check -Repo $ws -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 0 "a jj workspace must not be flagged (stderr: $($result.StdErr))"
    Assert-Equal $result.Json.role 'task-worktree' 'nested .jj workspace detected as a task worktree'
}

Test-Case 'does not flag a diverged topic checkout as stale' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    $base = Add-Commit -Repo $repo -Message 'base'
    Invoke-RepoGit $repo @('checkout', '-q', '-b', 'topic') | Out-Null
    Set-FileContent (Join-Path $repo 'src/topic.ts') 'T'
    Add-Commit -Repo $repo -Message 'topic-only commit' | Out-Null
    Invoke-RepoGit $repo @('checkout', '-q', 'main') | Out-Null
    Set-FileContent (Join-Path $repo 'src/main.ts') 'M'
    Add-Commit -Repo $repo -Message 'main-only commit' | Out-Null
    Invoke-RepoGit $repo @('checkout', '-q', 'topic') | Out-Null

    $result = Invoke-Check -Repo $repo -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 0 'a diverged checkout is not the main-copy drift, so it is not blocked'
    Assert-Equal $result.Json.relation 'diverged' 'relation is diverged'
    Assert-Equal $result.Json.verdict 'ok' 'diverged is not stale'
}

Test-Case 'passes when there is no local main to lag behind' {
    $repo = New-TempRepo -InitialBranch 'dev'
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    Add-Commit -Repo $repo -Message 'commit1' | Out-Null

    $result = Invoke-Check -Repo $repo -ExtraArgs @('-Json')

    Assert-Equal $result.ExitCode 0 'no local main ref -> cannot be behind it -> pass'
    Assert-Equal $result.Json.relation 'no-target' 'relation is no-target'
}

Test-Case 'a task worktree is verified against its explicit expected base when one is given' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    $base = Add-Commit -Repo $repo -Message 'commit1'
    $wt = Join-Path $script:TmpRoot ('wt-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))
    Invoke-RepoGit $repo @('worktree', 'add', '-q', '-b', 'task/T-2', $wt) | Out-Null
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v2'
    $advanced = Add-Commit -Repo $repo -Message 'commit2'

    # The worktree DOES contain its base commit -> ok.
    $good = Invoke-Check -Repo $wt -ExtraArgs @('-Json', '-ExpectedBase', $base)
    Assert-Equal $good.ExitCode 0 'a worktree containing its expected base passes'
    Assert-Equal $good.Json.relation 'isolated-worktree' 'still classified as an isolated worktree'

    # The worktree does NOT contain the newer main commit, so asserting it as the base fails.
    $bad = Invoke-Check -Repo $wt -ExtraArgs @('-Json', '-ExpectedBase', $advanced)
    Assert-Equal $bad.ExitCode 1 'a worktree missing its asserted base is flagged'
    Assert-Equal $bad.Json.relation 'unrelated-base' 'relation is unrelated-base'
}

Test-Case 'an explicit -Role task-worktree skips the currency check on a behind tree' {
    $repo = New-BehindRepo

    $forced = Invoke-Check -Repo $repo -ExtraArgs @('-Json', '-Role', 'task-worktree')
    Assert-Equal $forced.ExitCode 0 'forcing the task-worktree role skips the currency gate'
    Assert-Equal $forced.Json.role 'task-worktree' 'role honoured the override'

    # And forcing main-worktree on the same tree still catches the drift.
    $auto = Invoke-Check -Repo $repo -ExtraArgs @('-Json', '-Role', 'main-worktree')
    Assert-Equal $auto.ExitCode 1 'forcing main-worktree re-enables the gate'
    Assert-Equal $auto.Json.relation 'behind' 'still detects behind under the forced role'
}

# ----------------------------------------------------------------------------------------------
# Summary + cleanup
# ----------------------------------------------------------------------------------------------

Write-Host ''
Write-Host "Ran $($script:TestCount) test(s): $($script:PassCount) passed, $($script:Failures.Count) failed."

try {
    # Linked worktrees leave read-only pack files; clear attributes so the throwaway tree can be removed.
    Get-ChildItem -LiteralPath $script:TmpRoot -Recurse -File -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Attributes = 'Normal' }
    Remove-Item -LiteralPath $script:TmpRoot -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    # Temp cleanup is best-effort: a leftover throwaway repo under %TEMP% must never fail the run.
    Write-Host "  (warning: could not fully remove temp dir $($script:TmpRoot))" -ForegroundColor Yellow
}

if ($script:Failures.Count -gt 0) {
    exit 1
}
exit 0
