<#
.SYNOPSIS
    Self-contained tests for scripts/restore-main-worktree.ps1.

.DESCRIPTION
    Each test builds a throwaway git repository under the system temp directory, drives the
    migration script as a child process (so its `exit` never touches this harness), and asserts
    the outcome. No external test framework is required — run it directly:

        pwsh -File tests/tooling/restore-main-worktree.tests.ps1

    Exit code is 0 when every test passes, 1 otherwise. Temp repositories are removed on exit.

    Coverage mirrors the task's required scenarios:
      * preserving the live .work/ through the migration (content + checksums),
      * fast-forwarding a working copy stuck on an older ancestor,
      * refusing when there are un-committed changes outside .work/,
      * a safe, no-op second run (idempotency),
    plus two guardrail cases: -DryRun makes no change, and the migration refuses a target that
    does not ignore .work/.
#>
#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ScriptUnderTest = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'scripts/restore-main-worktree.ps1'
if (-not (Test-Path -LiteralPath $script:ScriptUnderTest)) {
    throw "Script under test not found: $($script:ScriptUnderTest)"
}
$script:PsExe = (Get-Process -Id $PID).Path
$script:TmpRoot = Join-Path $env:TEMP ('specdesk-tooling-tests-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))
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
    # Leading comma keeps a single-line result an array so callers' [0]/.Count stay correct.
    if ($null -eq $out) {
        return , @()
    }
    return , @(@($out) | Where-Object { "$_".Trim() -ne '' })
}

function New-TempRepo {
    $dir = Join-Path $script:TmpRoot ([System.Guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Invoke-RepoGit $dir @('init', '-q', '-b', 'main') | Out-Null
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

function Get-Trimmed {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-Content -LiteralPath $Path -Raw).Trim()
}

function Invoke-Migration {
    param([Parameter(Mandatory)][string]$Repo, [string[]]$ExtraArgs = @())
    $fileArgs = @('-NoProfile', '-File', $script:ScriptUnderTest, '-RepoRoot', $Repo) + $ExtraArgs
    $output = & $script:PsExe @fileArgs 2>&1 | Out-String
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

# Build a repo whose main both ignores .work/ and still tracks a single .work/ file (the
# historically tracked state), then overwrite it on disk with newer "live" content plus extra
# untracked files — the shape the migration has to preserve.
function New-DriftedRepo {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v2'
    Set-FileContent (Join-Path $repo '.work/Tasks_Queue.md') 'OLD-QUEUE'
    Set-FileContent (Join-Path $repo '.gitignore') ".work/`n"
    Invoke-RepoGit $repo @('add', 'src/app.ts', '.gitignore') | Out-Null
    Invoke-RepoGit $repo @('add', '-f', '.work/Tasks_Queue.md') | Out-Null
    Invoke-RepoGit $repo @('commit', '-q', '-m', 'init main') | Out-Null
    Set-FileContent (Join-Path $repo '.work/Tasks_Queue.md') 'LIVE-QUEUE'
    Set-FileContent (Join-Path $repo '.work/journal.md') 'LIVE-JOURNAL'
    Set-FileContent (Join-Path $repo '.work/tasks/T-1/task.md') 'task body'
    return $repo
}

# ----------------------------------------------------------------------------------------------
# Tests
# ----------------------------------------------------------------------------------------------

Test-Case 'preserves the live .work through the migration' {
    $repo = New-DriftedRepo
    $backup = Join-Path $script:TmpRoot ('bk-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))
    $result = Invoke-Migration -Repo $repo -ExtraArgs @('-TargetRef', 'main', '-BackupRoot', $backup)

    Assert-True ($result.ExitCode -eq 0) "exit code should be 0 (was $($result.ExitCode)):`n$($result.Output)"
    Assert-Equal (Get-Trimmed (Join-Path $repo '.work/Tasks_Queue.md')) 'LIVE-QUEUE' 'live queue preserved (not reverted to history)'
    Assert-Equal (Get-Trimmed (Join-Path $repo '.work/journal.md')) 'LIVE-JOURNAL' 'untracked live journal preserved'
    Assert-Equal (Get-Trimmed (Join-Path $repo '.work/tasks/T-1/task.md')) 'task body' 'nested live task file preserved'
    $tracked = Invoke-RepoGit $repo @('ls-files', '--', '.work')
    Assert-True (@($tracked).Count -eq 0) ".work must be untracked after migration (still tracked: $($tracked -join ', '))"
    Assert-True (Test-Path -LiteralPath $backup) 'backup directory created'
    $manifest = @(Get-ChildItem -Path $backup -Recurse -Filter 'manifest.sha256')
    Assert-True ($manifest.Count -ge 1) 'backup checksum manifest written'
}

Test-Case 'fast-forwards a working copy stuck on an older ancestor' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    Set-FileContent (Join-Path $repo '.work/Tasks_Queue.md') 'OLD'
    Set-FileContent (Join-Path $repo '.gitignore') ".work/`n"
    Invoke-RepoGit $repo @('add', 'src/app.ts', '.gitignore') | Out-Null
    Invoke-RepoGit $repo @('add', '-f', '.work/Tasks_Queue.md') | Out-Null
    Invoke-RepoGit $repo @('commit', '-q', '-m', 'commit1') | Out-Null
    $ancestor = (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0]
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v2'
    Set-FileContent (Join-Path $repo 'src/new.ts') 'ADDED'
    Invoke-RepoGit $repo @('add', 'src/app.ts', 'src/new.ts') | Out-Null
    Invoke-RepoGit $repo @('commit', '-q', '-m', 'commit2') | Out-Null
    Invoke-RepoGit $repo @('checkout', '-q', $ancestor) | Out-Null
    Set-FileContent (Join-Path $repo '.work/Tasks_Queue.md') 'LIVE'

    Assert-True (-not (Test-Path -LiteralPath (Join-Path $repo 'src/new.ts'))) 'precondition: new.ts absent while behind'
    $result = Invoke-Migration -Repo $repo -ExtraArgs @('-TargetRef', 'main', '-NoBackup')

    Assert-True ($result.ExitCode -eq 0) "exit code should be 0 (was $($result.ExitCode)):`n$($result.Output)"
    Assert-True (Test-Path -LiteralPath (Join-Path $repo 'src/new.ts')) 'new source module present after fast-forward'
    Assert-Equal (Get-Trimmed (Join-Path $repo 'src/app.ts')) 'v2' 'source updated to the target'
    Assert-Equal (Get-Trimmed (Join-Path $repo '.work/Tasks_Queue.md')) 'LIVE' 'live .work preserved across the fast-forward'
    $tracked = Invoke-RepoGit $repo @('ls-files', '--', '.work')
    Assert-True (@($tracked).Count -eq 0) '.work untracked after migration'
}

Test-Case 'refuses when there are un-committed changes outside .work' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    Set-FileContent (Join-Path $repo '.work/Tasks_Queue.md') 'OLD'
    Set-FileContent (Join-Path $repo '.gitignore') ".work/`n"
    Invoke-RepoGit $repo @('add', 'src/app.ts', '.gitignore') | Out-Null
    Invoke-RepoGit $repo @('add', '-f', '.work/Tasks_Queue.md') | Out-Null
    Invoke-RepoGit $repo @('commit', '-q', '-m', 'commit1') | Out-Null
    $headBefore = (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0]
    Set-FileContent (Join-Path $repo 'src/app.ts') 'DIRTY-UNCOMMITTED'
    Set-FileContent (Join-Path $repo '.work/Tasks_Queue.md') 'LIVE'

    $result = Invoke-Migration -Repo $repo -ExtraArgs @('-TargetRef', 'main', '-NoBackup')

    Assert-True ($result.ExitCode -ne 0) "migration must fail on a foreign change (exit was $($result.ExitCode))"
    Assert-Match $result.Output 'src/app.ts' 'error should name the offending file'
    Assert-Equal (Get-Trimmed (Join-Path $repo 'src/app.ts')) 'DIRTY-UNCOMMITTED' 'foreign change left untouched'
    Assert-Equal (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0] $headBefore 'HEAD unchanged after refusal'
    $tracked = Invoke-RepoGit $repo @('ls-files', '--', '.work')
    Assert-True (@($tracked).Count -eq 1) '.work still tracked — nothing was mutated'
}

Test-Case 'is idempotent on a safe second run' {
    $repo = New-DriftedRepo
    $first = Invoke-Migration -Repo $repo -ExtraArgs @('-TargetRef', 'main', '-NoBackup')
    Assert-True ($first.ExitCode -eq 0) "first run should succeed (was $($first.ExitCode)):`n$($first.Output)"
    $headAfterFirst = (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0]
    $queueAfterFirst = Get-Trimmed (Join-Path $repo '.work/Tasks_Queue.md')

    $second = Invoke-Migration -Repo $repo -ExtraArgs @('-TargetRef', 'main', '-NoBackup')
    Assert-True ($second.ExitCode -eq 0) "second run should succeed (was $($second.ExitCode)):`n$($second.Output)"
    Assert-Match $second.Output 'Already migrated' 'second run reports a no-op'
    Assert-Equal (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0] $headAfterFirst 'no new commit on the second run'
    Assert-Equal (Get-Trimmed (Join-Path $repo '.work/Tasks_Queue.md')) $queueAfterFirst 'live .work unchanged on the second run'
    $tracked = Invoke-RepoGit $repo @('ls-files', '--', '.work')
    Assert-True (@($tracked).Count -eq 0) '.work stays untracked on the second run'
}

Test-Case 'a dry run reports the plan without mutating anything' {
    $repo = New-DriftedRepo
    $headBefore = (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0]
    $result = Invoke-Migration -Repo $repo -ExtraArgs @('-TargetRef', 'main', '-DryRun')

    Assert-True ($result.ExitCode -eq 0) "dry run should exit 0 (was $($result.ExitCode)):`n$($result.Output)"
    Assert-Match $result.Output 'Dry run' 'dry run is announced'
    Assert-Equal (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0] $headBefore 'HEAD unchanged by a dry run'
    $tracked = Invoke-RepoGit $repo @('ls-files', '--', '.work')
    Assert-True (@($tracked).Count -eq 1) '.work still tracked after a dry run'
}

Test-Case 'refuses a target that does not ignore .work' {
    $repo = New-TempRepo
    Set-FileContent (Join-Path $repo 'src/app.ts') 'v1'
    Set-FileContent (Join-Path $repo '.work/Tasks_Queue.md') 'OLD'
    # .gitignore deliberately omits .work/ — the guard must catch it.
    Set-FileContent (Join-Path $repo '.gitignore') "node_modules/`n"
    Invoke-RepoGit $repo @('add', 'src/app.ts', '.gitignore') | Out-Null
    Invoke-RepoGit $repo @('add', '-f', '.work/Tasks_Queue.md') | Out-Null
    Invoke-RepoGit $repo @('commit', '-q', '-m', 'commit1') | Out-Null
    $headBefore = (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0]

    $result = Invoke-Migration -Repo $repo -ExtraArgs @('-TargetRef', 'main', '-NoBackup')

    Assert-True ($result.ExitCode -ne 0) "migration must refuse a target that does not ignore .work (exit was $($result.ExitCode))"
    Assert-Match $result.Output 'does not ignore .work' 'error explains the missing ignore rule'
    Assert-Equal (Invoke-RepoGit $repo @('rev-parse', 'HEAD'))[0] $headBefore 'HEAD unchanged after refusal'
}

# ----------------------------------------------------------------------------------------------
# Summary + cleanup
# ----------------------------------------------------------------------------------------------

Write-Host ''
Write-Host "Ran $($script:TestCount) test(s): $($script:PassCount) passed, $($script:Failures.Count) failed."

try {
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
