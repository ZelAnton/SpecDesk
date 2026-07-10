# Tooling tests

Tests for the repository maintenance scripts under [`scripts/`](../../scripts). These exercise the
scripts against throwaway git repositories under the system temp directory; they do not touch this
working copy. They are standalone PowerShell scripts (no external framework) and are not part of the
`dotnet test` / `npm test` suites.

## `restore-main-worktree.tests.ps1`

Covers [`scripts/restore-main-worktree.ps1`](../../scripts/restore-main-worktree.ps1), which detaches
the orchestrator's local `.work/` state from the versioned tree and fast-forwards the working copy to
the published `main`. Run it directly with either PowerShell host:

```powershell
pwsh -File tests/tooling/restore-main-worktree.tests.ps1
# or Windows PowerShell:
powershell -ExecutionPolicy Bypass -File tests/tooling/restore-main-worktree.tests.ps1
```

Exit code is `0` when every case passes, `1` otherwise. Cases: preserving the live `.work/` (content
and checksums), fast-forwarding a working copy stuck on an older ancestor, refusing when there are
un-committed changes outside `.work/`, a safe no-op second run (idempotency), a non-mutating dry run,
and refusing a target that does not ignore `.work/`.

## `assert-main-current.tests.ps1`

Covers [`scripts/assert-main-current.ps1`](../../scripts/assert-main-current.ps1), the read-only,
fail-closed gate that refuses to plan, build, or run from a repository main working copy that has
drifted behind the local published `main`. Run it directly with either PowerShell host:

```powershell
pwsh -File tests/tooling/assert-main-current.tests.ps1
# or Windows PowerShell:
powershell -ExecutionPolicy Bypass -File tests/tooling/assert-main-current.tests.ps1
```

Exit code is `0` when every case passes, `1` otherwise. Cases: flagging a main working copy that lags
behind `main` as stale (exit 1, naming both revisions and the recovery command), passing a re-synced
copy, read-only behaviour when un-committed local changes are present, **no** false failure in an
isolated task worktree (a git linked worktree and a jj-style nested workspace) even after `main`
advanced, not flagging a diverged topic checkout, passing when there is no local `main`, the
machine-readable JSON result, and an explicit `-Role` override.
