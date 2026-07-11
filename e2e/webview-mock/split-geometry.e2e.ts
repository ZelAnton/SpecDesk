import { GEOMETRY_DOC } from "../fixtures/docs";
import { expect, test } from "../lib/fixtures";
import {
  type AlignSpec,
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
  await waitForGeometrySettle(page);
});

test("applies real, non-zero source spacers whose rendered height matches their style height", async ({
  page,
}) => {
  const spacers = await spacerReport(page);
  // The formatted blocks outgrow the source lines at several boundaries (rows, items), so multiple
  // spacers are needed. Real Chromium yields 3 here — one fewer than the jsdom gate's ≥4, because that
  // gate models NO pane padding (its `referenceInset` is 0) whereas a real render has the formatted
  // pane's `padding-top`: since T-061 that inset is measured out of the alignment baseline instead of
  // being reproduced as a spurious top-of-document lead-compensation spacer, so the real count is one
  // lower than the rigged jsdom geometry's. All 3 are genuine row/item-boundary spacers.
  expect(spacers.length).toBeGreaterThanOrEqual(3);
  for (const spacer of spacers) {
    expect(spacer.styleHeight).toBeGreaterThan(0);
    // The REAL rendered height matches what height-sync declared — the check jsdom cannot make.
    expect(Math.abs(spacer.renderedHeight - spacer.styleHeight)).toBeLessThanOrEqual(1);
  }
});

test("aligns each scrolled block with its source line within a few real px", async ({ page }) => {
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

test("the spacer-height check is sensitive — collapsing the spacers breaks it (control)", async ({
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
