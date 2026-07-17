# Verification ladder & diagnostics

SpecDesk verifies the editor at five rungs of increasing fidelity, and instruments the webview with an
always-on diagnostic trace. This document is the map: **which rung proves what, when you must run it,
and how to read what it produces.** The guiding rule is at the bottom and it is non-negotiable — read it.

## The ladder

Each rung is strictly higher-fidelity (and slower) than the one below. A change is only "verified" once
it has passed the rung that actually exercises what it changed — not merely the cheap rungs beneath it.

| # | Rung | Command | What it proves | Where it runs |
|---|------|---------|----------------|---------------|
| 1 | **Unit** | `dotnet test SpecDesk.slnx` · `cd webview && npm test` | Logic in isolation (F#/C# domain, TS modules). Fastest. | dev + CI (.NET all-OS; webview ubuntu) |
| 2 | **Contract fixtures** | part of the unit runs | The native↔webview wire contract can't drift silently (C#/F# ↔ TS pinned by JSON fixtures). | dev + CI |
| 3 | **jsdom delivery gate** | `cd webview && npm run test:delivery` | The *shipped* `webview.js` bundle is present and wired — real modules, real `index.ts`, but **rigged (synthetic) layout**. | dev + CI |
| 4 | **Layer 1 — Playwright + real Chromium** | `cd e2e && npm run e2e` | **Real rendered geometry** of the bundle against a mock host: the current no-spacer Split policy, selection overlays, list indentation, and scroll coupling — the things jsdom can't render. Screenshots. | dev + ubuntu CI |
| 5 | **Layer 2 — full app over CDP** | `cd e2e && npm run e2e:app` | The **real `SpecDesk.Host.exe`** (Photino + WebView2) over a disposable git repo: native startup (ready → auto-load → lifecycle-from-git → render), and **native effects** (autosave-to-disk, git commit). | Windows-local + nightly CI |

Rungs 1–4 run in CI on every push/PR. **Rung 5 is Windows-only**, run locally on demand and, since
`.github/workflows/layer2-nightly.yml` (T-087), also **nightly + on demand in CI** on a `windows-latest`
hosted runner — see "Layer 2 in CI" below. It does not run on every push/PR (a full native launch is too
slow for that cadence); `ci.yml` still typechecks the `e2e/` package on every push/PR so the Layer 2 code
itself can't rot between nightly runs.

### When you must climb which rung

- Change **F#/C# domain or TS module logic** → rung 1 (+ rung 2 if you touched the wire contract; regenerate
  fixtures with `scripts/update-contract-fixtures.cmd` — a whole-solution run, never a narrowed `--filter`).
- Change **webview UI, layout, formatting, scroll/height-sync, the review overlay** → **rung 4** (real
  geometry), and **read the screenshot** (see the rule below). Rung 3 alone proves wiring, not pixels.
- Change **native startup, lifecycle, IPC, git/GitHub effects, or anything spanning native↔webview** →
  **rung 5** on a Windows machine, and read the screenshot + the native `app-log.txt` on failure.

## Layer 1 — `e2e/` webview-mock (rung 4)

Real Chromium (Playwright's own) runs the built `wwwroot/webview.js` against a mock host
(`globalThis.external`), with injected IPC frames. `global-setup` rebuilds and verifies the bundle first,
so Layer 1 can never validate a stale build. Assertions are **geometry deltas**, not pixel baselines
(cross-platform font rendering differs); screenshots are evidence, not the gate.

```sh
cd e2e && npm install && npm run e2e:install   # one-time: deps + Playwright's Chromium
npm run e2e                                     # all Layer 1 scenarios (headless)
npm run e2e:headed                              # watch it run
npx playwright test webview-mock/split-geometry.e2e.ts   # one scenario
```

## Layer 2 — `e2e/` full-app over CDP (rung 5)

Launches the built `SpecDesk.Host.exe` with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port`
(WebView2 exposes CDP), an isolated `WEBVIEW2_USER_DATA_FOLDER`, and `SPECDESK_DATA_ROOT` pointing at a
disposable temp data root seeded with a git fixture repo. Playwright attaches via `connectOverCDP` and
drives the real page. It asserts native effects the mock host can't: real Windows edge/corner cursors and
pointer-driven sizing, border-free non-resizable maximized edges, work-area-bounded maximize, autosave writing to the fixture `welcome.md` on disk,
and Save-version committing to the real repo on the host-suggested `spec/…` branch. The native resize
scenario also writes structured HWND/style/cursor/geometry evidence beside its success screenshot.

```sh
cd e2e && npm run e2e:app         # builds the host, launches it, attaches, runs the full-app specs
E2E_SKIP_BUILD=1 npm run e2e:app  # skip the dotnet build (fast inner loop; the app is disposable)
```

**Always run Layer 2 via `npm run e2e:app`** — never a bare `playwright test --project=full-app`. The
`--workers=1` in that script is the real concurrency guard: the harness sweeps stale temp run-dirs, and
running two full-app files in parallel workers could otherwise cross-delete a live run's dir. (The sweep
is age-aware — it only reaps dirs older than 5 min — as a backstop, but it assumes a run finishes within
that window; `--workers=1` is what makes it safe.) Layer 2 does not use Playwright's own trace/screenshot
(those attach to the fixture page, not the CDP-attached one) — the explicit failure bundle below does.

The guards (`MainWorktreeGuard`, `WebviewBundleGuard`) stay **armed** in Layer 2: a stale build or a stale
main working copy fails the launch loudly rather than being tested. Do not set the `SPECDESK_*_ALLOW_STALE`
overrides for E2E.

### Layer 2 in CI (T-087)

`.github/workflows/layer2-nightly.yml` runs Layer 2 on a `windows-latest` hosted runner: nightly
(`schedule`) and on demand (`workflow_dispatch`, e.g. from the Actions tab). It builds `SpecDesk.Host`
once (`dotnet build src/SpecDesk.Host/SpecDesk.Host.csproj -c Debug`, webview bundled), then runs
`npm run e2e:app` with `E2E_SKIP_BUILD=1` so the per-spec-file `buildHost()` calls don't rebuild the
already-current host. Artifacts (screenshots, `geometry.json`, `trace-ring.json`, `app-log.txt`) are
uploaded via `actions/upload-artifact` on every outcome, same as the local failure bundle below.

GitHub's own hosted Windows runner images ship the Microsoft Edge WebView2 Runtime and run as a real,
non-headless interactive session (unlike the Linux runners, which need a virtual display for GUI apps),
so a Photino/WebView2 window is expected to come up there the same as on a Windows dev machine or a
self-hosted runner. **This has not yet been confirmed by a live run**: the workflow can only be exercised
by GitHub Actions itself once it exists on a ref GitHub knows about, which is outside what a coding agent
without push access can trigger — so the first real signal is the first nightly run (or an immediate
`workflow_dispatch` after merge). If that first run shows the hosted runner genuinely cannot create a
Photino/WebView2 window (rather than a fixable test/harness bug — check `app-log.txt` and the Playwright
output first), do not leave the job permanently red: disable the `schedule` trigger, record the concrete
failure mode here, and stand up a **self-hosted Windows runner** instead (a Windows box — physical or a
VM with GUI, e.g. a persistent Windows 11 VM or an existing dev machine — registered as a self-hosted
runner is the natural fallback since it is just a real desktop session, which is exactly what Layer 2
needs and what a hosted Linux-style headless runner can't offer).

## Failure artifacts

On a failing E2E scenario, an evidence bundle lands under **`artifacts/e2e/<spec>-<title>-<project>/`**
(repo-root, git-ignored, wiped at the start of each Layer 1 run). Read these instead of re-running:

| File | What it is | Layer |
|------|-----------|-------|
| `failure.png` | Full-page screenshot at the moment of failure | 1 + 2 |
| `final.png` | Success-path screenshot (always written, so there is always something to read) | 1 + 2 |
| `error.txt` | The failing assertion (what was expected vs what was measured) | 1 + 2 |
| `geometry.json` | Pane rects/scrollTops, spacer style-vs-rendered heights, per-anchor alignment deltas | 1 + 2 |
| `console.log` | Page console + uncaught errors | 1 + 2 |
| `trace-ring.json` | The webview's **causal trace** (`window.__specdeskTrace`) — WHY it happened | 1 + 2 |
| `app-log.txt` | The real app's Serilog tail (native side of a Layer 2 failure) | 2 |

## Diagnostics: the trace ring

The webview keeps an **always-on ring buffer** of the editor's hot paths — formatting decisions, the
cross-pane mirror, block-splice fallbacks, scroll-sync verdicts, height-sync reconcile, render, review,
and IPC — so when something misbehaves the ring holds *why*. It is IPC-free (lives in the page) and cheap
(no serialization on the record path).

- **In a browser / CDD / devtools:** `window.__specdeskTrace.snapshot()` returns the ring; `get(n)`,
  `clear()`, `mark(label)`, `setVerbose(true)` (per-frame events like scroll couple-skips are verbose-gated
  off by default).
- **From the running app:** the **Export log** button dumps the ring to `specdesk-trace-<stamp>.json` beside
  the log **and** appends a wall-clock-stamped tail to the exported log, so the native Serilog timeline and
  the webview trace line up in one file.
- **Unhandled `window.onerror` / promise rejections** are captured to the log live (rate-limited).

## Environment variables

All are operator/dev opt-ins; unset, behaviour is the shipped default.

| Variable | Effect |
|----------|--------|
| `SPECDESK_DATA_ROOT` | Move the data root (sample repo + auth + logs) to a directory — used by Layer 2 to isolate a run. Malformed → falls back to the default; unset → `%LOCALAPPDATA%\SpecDesk`. |
| `SPECDESK_LOG_DIR` | Redirect the rolling log file (wins over `SPECDESK_DATA_ROOT`'s `…/logs`). |
| `SPECDESK_LOG_LEVEL` | File-sink minimum: `verbose`/`trace`, `debug`, `info`/`information`, `warning`/`warn`, `error`, `fatal`/`critical`, or `off`/`none`/`silent` (fatal-only). Default `debug`. |
| `SPECDESK_DEVTOOLS` | `1`/`true`/`yes`/`on` → enable WebView2 devtools + right-click menu (Debug and Release). Off by default; a shipped app exposes neither. |
| `E2E_SKIP_BUILD` | `1` → Layer 2 skips the `dotnet build` before launch (fast inner loop). |
| `SPECDESK_DELIVERY_PREBUILT` | `1` → delivery and E2E gates consume an already-built bundle but still require its manifest to verify as current. Use only when the sandbox forbids esbuild child-process spawn; build first with the native esbuild executable. |
| `SPECDESK_ALLOW_STALE_WORKTREE` / `SPECDESK_WEBVIEW_ALLOW_STALE` | Override the currency/bundle guards to run a deliberately stale build. **Never set these for E2E.** |

## Guard-failure recovery

The build/run guards refuse a stale main working copy or bundle. If a build or launch fails with a currency
message, re-sync the main working copy (`scripts/restore-main-worktree.ps1`) and rebuild — do **not** set the
`ALLOW_STALE` overrides to paper over it (they run the *old* UI). See AGENTS.md → "Working copy currency".

---

## The rule (non-negotiable)

**Before claiming a UI, layout, formatting, or scroll change works, run the rung that renders it (Layer 1,
or Layer 2 for native-spanning changes) and READ the produced screenshot.** The geometry assertions are the
gate; the screenshot is the proof you looked. "The tests pass" is not the same as "I saw it render
correctly" — the whole point of rungs 4–5 is that an agent can look at the same pixels the user looks at.
Do not claim a visual change works on the strength of the unit or jsdom rungs alone.
