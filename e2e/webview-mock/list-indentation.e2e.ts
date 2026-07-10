import { GEOMETRY_DOC, LIST_LINES } from "../fixtures/docs";
import { expect, test } from "../lib/fixtures";
import { formattedListIndent, sourceListIndent, waitForGeometrySettle } from "../lib/geometry";
import { loadDoc } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";

// How much deeper a nested item must render than its parent to count as genuinely indented (a real
// nesting step is a whole indent unit; this rules out sub-pixel noise being read as indentation).
const MIN_INDENT_STEP = 6;

test.beforeEach(async ({ page }) => {
  await page.goto(BASE_URL);
  await loadDoc(page, { path: "geometry.md", text: GEOMETRY_DOC });
  await waitForGeometrySettle(page);
});

test("renders a nested list item deeper than its parent in BOTH panes", async ({ page }) => {
  // Formatted pane: the nested <li> box sits inside its parent <li>, so its real left edge is further
  // right. This is the "why did the indentation come out wrong" signal jsdom's rigged geometry can't
  // show — it reads the real rendered position.
  const formatted = await formattedListIndent(page);
  expect(formatted, "nested + parent list items found in the formatted pane").not.toBeNull();
  if (formatted) {
    expect(formatted.nestedLeft).toBeGreaterThan(formatted.parentLeft + MIN_INDENT_STEP);
  }

  // Code pane: the nested source line's first glyph is rendered to the right of the parent's, because
  // its Markdown carries leading indentation.
  const source = await sourceListIndent(page, LIST_LINES.parent, LIST_LINES.nested);
  expect(source, "nested + parent source lines found in the code pane").not.toBeNull();
  if (source) {
    expect(source.nestedLeft).toBeGreaterThan(source.parentLeft + MIN_INDENT_STEP);
  }
});
