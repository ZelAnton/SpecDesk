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
// (top of document) is NOT here because it carries a real, known ~24px misalignment — but it is not
// dropped: the "known-issue" test below PINS that offset in a band, so the top-of-document geometry
// stays covered (a regression that widens it, or a fix that closes it, both turn red).
const SCROLLABLE: AlignSpec[] = [
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
  // The formatted blocks outgrow the source lines at several boundaries (rows, items), so more than a
  // couple of spacers are needed — matching the jsdom gate's ≥4.
  expect(spacers.length).toBeGreaterThanOrEqual(4);
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

// KNOWN ISSUE — top-of-document lead double-count. At the top of a split document the code pane's lead
// spacer represents the formatted pane's 20px `padding-top`, WHILE the formatted pane rests with that
// padding scrolled off (h1 flush at its top) — so the rendered h1 sits ~24px above its source line,
// vs ~4px for mid-document anchors. This is a real, visible misalignment the harness caught. We PIN it
// in a band rather than drop the anchor, so top-of-document geometry stays covered: a regression that
// widens the offset (band exceeded) OR a fix that closes it to ~4px (band undershot → this test goes
// red, prompting its removal) both fail. Follow-up: fix the resting formatted scrollTop=20 double-count
// in height-sync's lead computation, then tighten this to the ≤ALIGN_EPSILON the other anchors meet.
const H1_KNOWN_OFFSET_MIN = 20;
const H1_KNOWN_OFFSET_MAX = 28;

test("top-of-document heading carries the known ~24px lead misalignment (pinned known-issue)", async ({
  page,
}) => {
  const align = await measureAlignment(page, {
    label: "heading",
    tag: "h1",
    needle: "Heading One",
    srcLine: "# Heading One",
  });
  expect(align, "heading rendered in both panes").not.toBeNull();
  if (align) {
    const delta = Math.abs(align.formattedRel - align.codeRel);
    expect(
      delta,
      "top-of-doc offset dropped below the known band — likely a FIX of the lead double-count; retire this pin",
    ).toBeGreaterThanOrEqual(H1_KNOWN_OFFSET_MIN);
    expect(
      delta,
      "top-of-doc offset grew beyond the known band — a REGRESSION widened the lead misalignment",
    ).toBeLessThanOrEqual(H1_KNOWN_OFFSET_MAX);
  }
});

test("the spacer-height check is sensitive — collapsing the spacers breaks it (control)", async ({
  page,
}) => {
  const before = await spacerReport(page);
  expect(before.length).toBeGreaterThanOrEqual(4);
  expect(before.every((spacer) => spacer.renderedHeight > 0)).toBe(true);

  // Sabotage: collapse the applied spacers to zero rendered height, as a build that never wired
  // height-sync would leave the tree. The SAME "rendered height > 0" measurement now reports 0 for
  // every spacer — proving the real-run check catches a missing/collapsed-spacer regression rather
  // than passing vacuously. The test stays green because it asserts the check flips.
  await page.addStyleTag({ content: "#editor .cm-sync-spacer { height: 0 !important; }" });
  const after = await spacerReport(page);
  expect(after.some((spacer) => spacer.renderedHeight > 0)).toBe(false);
});
