import { GEOMETRY_DOC } from "../fixtures/docs";
import { expect, test } from "../lib/fixtures";
import {
  type AlignSpec,
  codeLineTop,
  formattedAnchorTops,
  measureAlignment,
  scrollCodeTo,
  scrollFormattedTo,
  scrollTops,
  waitForGeometrySettle,
  waitForScrollSettle,
} from "../lib/geometry";
import { loadDoc } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";

const ALIGN_EPSILON = 6;

const ROW: AlignSpec = { label: "row-r2", tag: "tr", needle: "r2a", srcLine: "| r2a | r2b |" };
const ITEM: AlignSpec = { label: "item-three", tag: "li", needle: "item three", srcLine: "- item three" };

test.beforeEach(async ({ page }) => {
  await page.goto(BASE_URL);
  await loadDoc(page, { path: "geometry.md", text: GEOMETRY_DOC });
  await waitForGeometrySettle(page);
});

test("Formatted → Code: scrolling the formatted pane to a row couples the code pane to that row", async ({
  page,
}) => {
  const tops = await formattedAnchorTops(page, [ROW]);
  const top = tops[ROW.label];
  expect(top).toBeDefined();
  if (top === undefined) {
    return;
  }
  await scrollFormattedTo(page, top);
  await waitForScrollSettle(page);
  const align = await measureAlignment(page, ROW);
  expect(align, "row rendered in both panes after the Formatted scroll").not.toBeNull();
  if (align) {
    expect(Math.abs(align.formattedRel - align.codeRel)).toBeLessThanOrEqual(ALIGN_EPSILON);
  }
});

test("Code → Formatted: scrolling the code pane to a line couples the formatted pane to that block", async ({
  page,
}) => {
  const top = await codeLineTop(page, ITEM.srcLine);
  expect(top, "the source line is rendered").not.toBeNull();
  if (top === null) {
    return;
  }
  await scrollCodeTo(page, top);
  await waitForScrollSettle(page);
  const align = await measureAlignment(page, ITEM);
  expect(align, "item rendered in both panes after the Code scroll").not.toBeNull();
  if (align) {
    expect(Math.abs(align.formattedRel - align.codeRel)).toBeLessThanOrEqual(ALIGN_EPSILON);
  }
});

test("a real mouse wheel over the formatted pane drives the code pane the same way", async ({
  page,
}) => {
  // Exact-anchor coupling uses programmatic scrollTop for pixel precision (a wheel can't land on an
  // exact anchor top); this scenario proves the REAL wheel-event → scroll → couple path with a
  // directional assertion: after a genuine wheel gesture, the sibling followed downward too.
  const box = await page.locator("#formatted").boundingBox();
  expect(box).not.toBeNull();
  if (!box) {
    return;
  }
  const before = await scrollTops(page);
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.wheel(0, 400);
  await waitForScrollSettle(page);
  const after = await scrollTops(page);

  expect(after.formatted, "the formatted pane actually scrolled under the wheel").toBeGreaterThan(
    before.formatted,
  );
  expect(after.editor, "the code pane followed the wheeled formatted pane").toBeGreaterThan(
    before.editor,
  );
});
