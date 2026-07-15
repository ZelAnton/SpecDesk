import type { Page } from "@playwright/test";
import {
  GEOMETRY_DOC,
  HEADING_TABLE_DOC,
  HEADING_TABLE_ROWS,
  PREFIXED_CONTAINERS_ANCHORS,
  PREFIXED_CONTAINERS_DOC,
} from "../fixtures/docs";
import { expect, test } from "../lib/fixtures";
import {
  type AlignSpec,
  codeLineTops,
  formattedAnchorTops,
  measureAlignment,
  scrollFormattedTo,
  spacerReport,
  waitForGeometrySettle,
  waitForScrollSettle,
} from "../lib/geometry";
import { loadDoc } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";

// Real-font alignment tolerance: a rendered block's top vs its source line's top, once the block is
// brought to the formatted viewport top and the code pane couples. Real Chromium shows a consistent
// ~4px offset (CodeMirror's content padding vs the formatted content origin), so 6px is the smallest
// robust bound — NOT the 1px the jsdom gate asserts on rigged geometry, and tight enough that a real
// misalignment (tens of px, as an unpadded or drifting block would show) fails hard.
const ALIGN_EPSILON = 6;

// Anchors deep enough to scroll to the pane top (the fixture's filler tail guarantees a viewport of
// content below each), where per-anchor top alignment is asserted within ALIGN_EPSILON. The heading
// (top of document) is a plain anchor here too: once height-sync stopped double-counting the reference
// pane's `padding-top` in its lead (T-061), the top-of-document block aligns within the same few real px
// as every mid-document anchor, so it no longer needs a pinned known-issue band.
const SCROLLABLE: AlignSpec[] = [
  { label: "heading", tag: "h1", needle: "Heading One", srcLine: "# Heading One" },
  {
    label: "tall-para",
    tag: "p",
    needle: "much longer",
    srcLine:
      "A much longer paragraph that should be considerably taller than the short one for sure indeed.",
  },
  { label: "code", tag: "pre", needle: "line1", srcLine: "```" },
  { label: "row-r2", tag: "tr", needle: "r2a", srcLine: "| r2a | r2b |" },
  { label: "item-three", tag: "li", needle: "item three", srcLine: "- item three" },
];

test.beforeEach(async ({ page }) => {
  await page.goto(BASE_URL);
  await loadDoc(page, { path: "geometry.md", text: GEOMETRY_DOC });
  // Code spacer insertion is temporarily disabled; allow the two editors and scroll maps to settle
  // without waiting for the legacy helper's required non-empty spacer set.
  await page.waitForTimeout(250);
});

test("keeps Code-side spacer insertion disabled in real Chromium", async ({
  page,
}) => {
  const spacers = await spacerReport(page);
  expect(spacers).toHaveLength(0);
});

test.skip("aligns each scrolled block with its source line within a few real px", async ({ page }) => {
  const tops = await formattedAnchorTops(page, SCROLLABLE);
  for (const anchor of SCROLLABLE) {
    const top = tops[anchor.label];
    expect(top, `formatted anchor ${anchor.label} is present`).toBeDefined();
    if (top === undefined) {
      continue;
    }
    // Bring the anchor to the formatted viewport top; the coordinator couples the padded code pane so
    // the source line lands at the same height. Then compare the two rendered tops directly.
    await scrollFormattedTo(page, top);
    await waitForScrollSettle(page);
    const align = await measureAlignment(page, anchor);
    expect(align, `${anchor.label} rendered in both panes`).not.toBeNull();
    if (align) {
      expect(
        Math.abs(align.formattedRel - align.codeRel),
        `${anchor.label} block top aligned with its source line`,
      ).toBeLessThanOrEqual(ALIGN_EPSILON);
    }
  }
});

// T-061 regression: the top-of-document heading now aligns within ALIGN_EPSILON like every other anchor
// (it is a plain member of SCROLLABLE above). The former "known ~24px lead misalignment" pin is retired —
// height-sync no longer reproduces the reference pane's `padding-top` as a code-pane lead, so the rendered
// h1 sits level with its source line once the code pane couples, not a pane-padding below it.

test.skip("the spacer-height check is sensitive — collapsing the spacers breaks it (control)", async ({
  page,
}) => {
  const before = await spacerReport(page);
  // 3 real spacers after T-061 (see the count note above) — all non-zero before the sabotage below.
  expect(before.length).toBeGreaterThanOrEqual(3);
  expect(before.every((spacer) => spacer.renderedHeight > 0)).toBe(true);

  // Sabotage: collapse the applied spacers to zero rendered height, as a build that never wired
  // height-sync would leave the tree. The SAME "rendered height > 0" measurement now reports 0 for
  // every spacer — proving the real-run check catches a missing/collapsed-spacer regression rather
  // than passing vacuously. The test stays green because it asserts the check flips.
  await page.addStyleTag({ content: "#editor .cm-sync-spacer { height: 0 !important; }" });
  const after = await spacerReport(page);
  expect(after.some((spacer) => spacer.renderedHeight > 0)).toBe(false);
});

// ---------------------------------------------------------------------------------------------------
// T-111 — heading-then-table resting per-row alignment (real-Chromium reproduction of the reported
// screenshot, `.work/Screenshot 2026-07-11 165557.png`). jsdom cannot see this: it needs the real
// rendered heights of the table rows (cell padding), CodeMirror's real line/wrap geometry, and the
// applied spacer widgets.
//
// The mechanism (T-110/T-111 diagnosis, now the ACCEPTED design with the T-112 container-tail floor):
// the non-rendered delimiter row (`| --- | --- |`), the blank line after the heading, and the heading's
// own formatted top-margin each make the CODE pane taller than the Formatted pane right at the table's
// start, so the first rows sit BELOW their Formatted target — the "source intrinsically taller"
// direction, which additive Code-pane spacers cannot pull up. Those pairs stay packed (the accepted
// limitation); the LAST rows keep step — via plain global alignment here (the running maximum climbs
// past the lead within this short document), and via the container-tail floor where earlier content
// keeps the maximum out of reach (the scenario below). Wrap on/off does not change any of it.
// ---------------------------------------------------------------------------------------------------
const BODY_ROW_SPECS = HEADING_TABLE_ROWS.map((row) => ({
  label: row.label,
  tag: "tr",
  needle: row.needle,
}));

/**
 * The resting per-consecutive-body-row drift: each Formatted row-to-row gap MINUS the (padded) Code
 * row-to-row gap. ~0 = the two rows keep step across the panes; a large positive value = the Code rows
 * are packed denser than the Formatted rows. Every top is content-relative, hence scroll-invariant, so
 * this reads the RESTING layout the screenshot shows (not a scrolled+coupled one — the coupling would
 * re-align whichever single row it is anchored to).
 */
async function restingRowGapDrift(page: Page): Promise<number[]> {
  const fTops = await formattedAnchorTops(page, BODY_ROW_SPECS);
  const cTops = await codeLineTops(
    page,
    HEADING_TABLE_ROWS.map((row) => row.srcLine),
  );
  const drifts: number[] = [];
  for (let i = 0; i < HEADING_TABLE_ROWS.length - 1; i++) {
    const a = HEADING_TABLE_ROWS[i];
    const b = HEADING_TABLE_ROWS[i + 1];
    const fa = a ? fTops[a.label] : undefined;
    const fb = b ? fTops[b.label] : undefined;
    const ca = a ? cTops[a.srcLine] : undefined;
    const cb = b ? cTops[b.srcLine] : undefined;
    if (fa == null || fb == null || ca == null || cb == null) {
      throw new Error(`missing geometry for body-row pair ${i}`);
    }
    drifts.push(fb - fa - (cb - ca));
  }
  return drifts;
}

test.describe.skip("heading-then-table resting per-row alignment (T-111)", () => {
  test("first table rows stay packed at rest, identically in both wrap states; the last pair keeps step", async ({
    page,
  }) => {
    await page.goto(BASE_URL);
    await loadDoc(page, { path: "heading-table.md", text: HEADING_TABLE_DOC });
    await waitForGeometrySettle(page);
    const wrapOn = await restingRowGapDrift(page);

    // Toggle CodeMirror soft-wrap OFF (the toolbar defaults it ON) and re-measure the SAME layout.
    await page.click("#wrap-btn");
    await waitForGeometrySettle(page);
    const wrapOff = await restingRowGapDrift(page);

    // (1) Wrap-INDEPENDENT: the resting drift is the same in both states — the behaviour is not
    //     "only in Wrap: off" (the original ambiguous lead); wrap only ever adds Code-pane height
    //     ABOVE a container, and this fixture's preamble never wraps.
    for (let i = 0; i < wrapOn.length; i++) {
      expect(
        Math.abs((wrapOn[i] ?? 0) - (wrapOff[i] ?? 0)),
        `body-row pair ${i} drift is wrap-independent`,
      ).toBeLessThanOrEqual(1);
    }

    // (2) The LAST body-row pair keeps step at rest within tolerance — here the running maximum climbs
    //     past the Code lead the delimiter/blank/heading created, so plain global alignment covers it.
    const last = wrapOn.length - 1;
    expect(
      Math.abs(wrapOn[last] ?? 0),
      "the last body-row pair keeps step across the panes at rest",
    ).toBeLessThanOrEqual(ALIGN_EPSILON);

    // (3) The FIRST body-row pair is packed noticeably denser in Code than in Formatted at rest — the
    //     documented, accepted limitation (additive spacers cannot lift a row that sits below its
    //     target). Guards that the T-112 floor stays NARROW: no per-row padding crept back in.
    expect(
      wrapOn[0] ?? 0,
      "the first body-row pair renders denser in Code than in Formatted (accepted drift)",
    ).toBeGreaterThan(ALIGN_EPSILON);
  });
});

// ---------------------------------------------------------------------------------------------------
// T-112 — the container-tail floor, in real Chromium. Content ABOVE a table/list can accumulate more
// Code padding (the running maximum) than any of the container's own rows require; the rows are then
// ALL "unreachable" and the plain running-maximum plan left the container spacer-less — its last row
// drifting against its rendered counterpart inside one viewport (the author-reported defect the
// jsdom container-tail gate engineers synthetically; this asserts it on real rendered geometry). The
// floor pins each container's LAST row to the container's internal growth: the container ends in step
// in both panes, while intermediate rows keep the accepted drift.
// ---------------------------------------------------------------------------------------------------
test.describe.skip("container-tail floor (T-112)", () => {
  /** Resting container parity: the Code-pane span from the container's first to last anchor line vs
   *  the Formatted-pane span between the same units, measured content-relative (scroll-invariant)
   *  after scrolling the container into view (CodeMirror virtualises off-screen lines). The pane is
   *  left AT the container's start, so a follow-up probe reads the same scroll state. */
  async function containerParity(
    page: Page,
    first: AlignSpec,
    last: AlignSpec,
  ): Promise<{ parity: number; codeSpan: number; formattedSpan: number }> {
    const fTops = await formattedAnchorTops(page, [first, last]);
    const fFirst = fTops[first.label];
    const fLast = fTops[last.label];
    if (fFirst === undefined || fLast === undefined) {
      throw new Error("container anchors not found in the formatted pane");
    }
    await scrollFormattedTo(page, fFirst);
    await waitForScrollSettle(page);
    const cTops = await codeLineTops(page, [first.srcLine, last.srcLine]);
    const cFirst = cTops[first.srcLine];
    const cLast = cTops[last.srcLine];
    if (cFirst == null || cLast == null) {
      throw new Error("container source lines not rendered in the code pane");
    }
    const codeSpan = cLast - cFirst;
    const formattedSpan = fLast - fFirst;
    return { parity: codeSpan - formattedSpan, codeSpan, formattedSpan };
  }

  /** Whether a spacer widget sits DIRECTLY above the given source line (block widgets render as
   *  siblings of `.cm-line`) — the floor's spacer at the container's final placement slot. */
  function spacerDirectlyAbove(page: Page, srcLine: string): Promise<boolean> {
    return page.evaluate((line) => {
      return Array.from(document.querySelectorAll("#editor .cm-sync-spacer")).some(
        (spacer) => (spacer.nextElementSibling?.textContent ?? "") === line,
      );
    }, srcLine);
  }

  test("the last table row and last list item keep step with their container's start", async ({
    page,
  }) => {
    await page.goto(BASE_URL);
    await loadDoc(page, { path: "prefixed-containers.md", text: PREFIXED_CONTAINERS_DOC });
    await waitForGeometrySettle(page);
    const { tableFirst, tableLast, listFirst, listLast } = PREFIXED_CONTAINERS_ANCHORS;

    // The user-visible contract: each container's Code span equals its Formatted span, so the
    // container ENDS in step even where none of its rows can reach the accumulated global padding.
    const table = await containerParity(page, { ...tableFirst }, { ...tableLast });
    expect(
      Math.abs(table.parity),
      `table spans keep step (code ${table.codeSpan}px vs formatted ${table.formattedSpan}px)`,
    ).toBeLessThanOrEqual(ALIGN_EPSILON);
    // And the floor's spacer sits physically INSIDE the container, right above its last unit — proof
    // the scenario exercised the floor (an unreachable tail), not plain global alignment. Read in the
    // scroll state containerParity left behind (the container is in view).
    expect(
      await spacerDirectlyAbove(page, tableLast.srcLine),
      "a spacer sits directly above the table's last row",
    ).toBe(true);

    const list = await containerParity(page, { ...listFirst }, { ...listLast });
    expect(
      Math.abs(list.parity),
      `list spans keep step (code ${list.codeSpan}px vs formatted ${list.formattedSpan}px)`,
    ).toBeLessThanOrEqual(ALIGN_EPSILON);
    expect(
      await spacerDirectlyAbove(page, listLast.srcLine),
      "a spacer sits directly above the list's last item",
    ).toBe(true);
  });
});
