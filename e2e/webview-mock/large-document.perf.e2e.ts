import { generateLargeDoc } from "../fixtures/large-doc";
import { expect, test } from "../lib/fixtures";
import { waitForGeometrySettle } from "../lib/geometry";
import { loadDoc } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";
import type { Page } from "@playwright/test";

/**
 * Layer 1 interactivity BUDGET scenario for large documents (T-085). It opens a large generated
 * document and proves two things stay within budget and do not grow quadratically with document size:
 *
 *   1. Reconciliation — opening the document and letting both panes reach a settled, spacer-stable
 *      geometry (the height-sync reconcile that builds the anchors + scroll maps + applies spacers). This
 *      is where a super-linear regression in the geometry machinery would surface, so it carries the
 *      explicit sub-quadratic scaling assertion across a 2x size step.
 *   2. Scroll-synchronization — a single genuine scroll gesture coupling the sibling pane. By design a
 *      settled pane's per-scroll couple is a cheap O(log n) map lookup (the maps are rebuilt once on a
 *      geometry change, not per scroll frame — sync-coordinator.ts / T-072), so it should stay fast and
 *      roughly FLAT as the document grows. Asserting it stays under a small absolute budget at the large
 *      size — and does not balloon relative to the small size — catches a regression that reintroduces
 *      per-scroll O(n) work (the classic interactive-scroll quadratic).
 *
 * Why these budgets (calibrated to avoid CI false positives, per the task's calibration criterion):
 *  - Timings are measured MIN-of-N with a discarded warmup run, so a transient CI slowdown (a GC pause,
 *    a noisy shared runner) only ever ADDS to a sample and cannot drag the reported minimum up; a genuine
 *    regression raises the minimum too.
 *  - The absolute ceilings are deliberately an order of magnitude above a healthy local run (a large-doc
 *    reconcile settles in low single-digit seconds; a couple in tens of milliseconds), so only a real
 *    blowup trips them — not slow hardware.
 *  - The growth guard is 3x for a 2x size step: linear scaling is ~2x and quadratic is ~4x, so 3x sits
 *    between them, failing on quadratic while tolerating measurement noise and fixed per-frame floors.
 *  - This is a NIGHTLY / on-demand stage, not the normal CI gate (see .github/workflows/perf.yml and the
 *    `*.perf.e2e.ts` exclusion in playwright.config.ts), so it never slows an ordinary run. Per KB K-003,
 *    a single red nightly on a Layer 1 job should be cross-checked against the base-commit run before it
 *    is treated as a real regression rather than known environment flakiness.
 */

// The scaling pair: SMALL and its 2x. Both are far larger (dozens of sections) than the ~60-line geometry
// fixtures, so the reconcile cost dominates the fixed rAF-settle floor and the ratio is meaningful. The
// .NET benchmarks cover the full 5–10k-line range on native code; the e2e keeps to a size that renders
// reliably inside a single CI-hosted Chromium without approaching the settle timeout.
const SMALL_LINES = 2000;
const LARGE_LINES = 4000;

// Budgets (see the rationale above). The absolute reconcile ceiling is deliberately loose (a healthy
// large-doc settle is low single-digit seconds even on a slower hosted runner) — the precise,
// hardware-independent regression signal is the growth ratio below, not this catastrophe guard.
const RECONCILE_BUDGET_MS = 20_000;
const COUPLE_BUDGET_MS = 1_500;
const GROWTH_GUARD = 3;
// Absorbs rAF frame-quantization noise in the (tiny, near-constant) couple times so their ratio can't
// false-fail when both samples sit at the few-frame floor; a genuine size-dependent couple still exceeds
// GROWTH_GUARD * small + this floor.
const COUPLE_NOISE_FLOOR_MS = 150;

// A large-document reconcile needs more frames to reach its fixed point than the small geometry fixtures.
const SETTLE_TIMEOUT_MS = 30_000;

const RECONCILE_REPEATS = 2;
const COUPLE_REPEATS = 3;
const COUPLE_FRACTIONS = [0.3, 0.5, 0.7];

/** Boot a fresh page and open a `lines`-line generated document, returning once both panes have settled.
 *  Uses the default reveal (as the geometry scenarios do) so the document's Split view — not the startup
 *  Start screen — is the active, coupled surface the scroll measurement then exercises. */
async function loadAndSettle(page: Page, lines: number): Promise<void> {
  await page.goto(BASE_URL);
  await loadDoc(page, { path: `large-${lines}.md`, text: generateLargeDoc(lines) });
  await waitForGeometrySettle(page, SETTLE_TIMEOUT_MS);
}

/** Min wall-clock of a fresh load → settle for `lines`, discarding one warmup run so V8 is warm. */
async function measureReconcileMs(page: Page, lines: number): Promise<number> {
  let best = Number.POSITIVE_INFINITY;
  for (let run = 0; run <= RECONCILE_REPEATS; run++) {
    const started = Date.now();
    await loadAndSettle(page, lines);
    const elapsed = Date.now() - started;
    if (run > 0) {
      best = Math.min(best, elapsed);
    }
  }
  return best;
}

/**
 * One genuine scroll of the formatted pane, timed until the sibling code pane couples (its scrollTop
 * moves). Runs entirely in-page so the measurement excludes CDP round-trip latency and captures only the
 * couple's own cost. `fraction` picks a target within the formatted pane's scroll range.
 */
async function coupleOnce(page: Page, fraction: number): Promise<{ ms: number; coupled: boolean }> {
  return page.evaluate(async (frac) => {
    const formatted = document.querySelector("#formatted") as HTMLElement | null;
    const codeScroller = document.querySelector("#editor .cm-scroller") as HTMLElement | null;
    if (!formatted || !codeScroller) {
      return { ms: -1, coupled: false };
    }
    const before = codeScroller.scrollTop;
    const target = Math.round((formatted.scrollHeight - formatted.clientHeight) * frac);
    const t0 = performance.now();
    formatted.scrollTop = target;
    formatted.dispatchEvent(new Event("scroll"));
    const DEADLINE_MS = 2_000;
    await new Promise<void>((resolve) => {
      const check = (): void => {
        const moved = Math.abs(codeScroller.scrollTop - before) > 0.5;
        if (moved || performance.now() - t0 > DEADLINE_MS) {
          resolve();
        } else {
          requestAnimationFrame(check);
        }
      };
      requestAnimationFrame(check);
    });
    return { ms: performance.now() - t0, coupled: Math.abs(codeScroller.scrollTop - before) > 0.5 };
  }, fraction);
}

/** Min couple latency over a few scroll gestures on the currently-loaded document; asserts each coupled. */
async function measureCoupleMs(page: Page, label: string): Promise<number> {
  let best = Number.POSITIVE_INFINITY;
  for (let i = 0; i < COUPLE_REPEATS; i++) {
    const fraction = COUPLE_FRACTIONS[i % COUPLE_FRACTIONS.length] ?? 0.5;
    const { ms, coupled } = await coupleOnce(page, fraction);
    expect(coupled, `${label}: scrolling the formatted pane couples the code pane`).toBe(true);
    best = Math.min(best, ms);
  }
  return best;
}

test("large-document reconciliation and scroll-sync stay within budget and scale sub-quadratically", async ({
  page,
}) => {
  // Multiple fresh boots of a multi-thousand-line document across two sizes — well past the default
  // per-test timeout.
  test.setTimeout(180_000);

  const smallReconcileMs = await measureReconcileMs(page, SMALL_LINES);
  const smallCoupleMs = await measureCoupleMs(page, `small(${SMALL_LINES})`);

  const largeReconcileMs = await measureReconcileMs(page, LARGE_LINES);
  const largeCoupleMs = await measureCoupleMs(page, `large(${LARGE_LINES})`);

  const summary = {
    smallLines: SMALL_LINES,
    largeLines: LARGE_LINES,
    smallReconcileMs,
    largeReconcileMs,
    reconcileGrowth: Number((largeReconcileMs / smallReconcileMs).toFixed(2)),
    smallCoupleMs: Number(smallCoupleMs.toFixed(1)),
    largeCoupleMs: Number(largeCoupleMs.toFixed(1)),
    coupleGrowth: Number((largeCoupleMs / smallCoupleMs).toFixed(2)),
  };
  // Surfaced in the runner output and the artifact bundle so a red run is diagnosable from the numbers.
  console.log(`[perf] large-document budgets: ${JSON.stringify(summary)}`);
  await test
    .info()
    .attach("large-document-perf", { body: JSON.stringify(summary, null, 2), contentType: "application/json" });

  // Absolute budgets at the large size.
  expect(largeReconcileMs, "large-document reconcile within absolute budget").toBeLessThan(
    RECONCILE_BUDGET_MS,
  );
  expect(largeCoupleMs, "large-document scroll couple within absolute budget").toBeLessThan(
    COUPLE_BUDGET_MS,
  );

  // Sub-quadratic scaling across the 2x size step.
  expect(
    largeReconcileMs,
    "reconcile time grows sub-quadratically with document size",
  ).toBeLessThan(GROWTH_GUARD * smallReconcileMs);
  expect(
    largeCoupleMs,
    "per-scroll couple time does not balloon with document size (no reintroduced per-scroll O(n) work)",
  ).toBeLessThan(GROWTH_GUARD * smallCoupleMs + COUPLE_NOISE_FLOOR_MS);
});
