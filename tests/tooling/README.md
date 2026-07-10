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
