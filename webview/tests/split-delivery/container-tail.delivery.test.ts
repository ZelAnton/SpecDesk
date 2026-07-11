/**
 * Container-tail alignment (T-112) through the SHIPPED bundle — the delivery-level check that the
 * container-tail floor survives the real wiring (sync-anchors containers → formatted geometry →
 * height-sync groups), not just the unit-tested plan math.
 *
 * The scenario is the T-110/T-111 report shape: padding accumulated by content ABOVE a table/list
 * exceeds every row's own requirement, so the plain running-maximum plan left the whole container
 * spacer-less and its LAST row drifted against its rendered counterpart inside one viewport (the
 * author-reported screenshot). The accepted contract: intermediate rows may drift (additive padding
 * cannot lift them), but the container's last row is floored to the container's internal growth, so the
 * container ends in step in both panes.
 *
 * Geometry is engineered via the harness height overrides (jsdom's CodeMirror lines are a uniform
 * 14px): a 400px paragraph sets the running maximum high above the table's requirements; three blank
 * source lines before the table (code-only height) sink the rows' requirements below it; the row
 * heights make the container's internal growth positive but SMALLER than that gap, so the tail is
 * globally unreachable yet floor-reachable — exactly the case the fix covers. Same shape for the list.
 */
// @vitest-environment jsdom
import { beforeAll, describe, expect, it } from "vitest";
import {
  type BundleArtifact,
  buildBundle,
  codeScroller,
  findLeaf,
  flushFrames,
  formattedPane,
  formattedTopOf,
  installLayoutAdapter,
  loadDocument,
  scrollPane,
  setLeafHeight,
  spacerElements,
  type WiredApp,
  wire,
} from "./harness.js";

const FIXTURE = [
  "# Heading One", // line 0
  "",
  "A tall rendered paragraph that sets the accumulated padding high.", // line 2 → 400px
  "",
  "Second para.", // line 4 → 30px (content-derived)
  "",
  "",
  "", // three blank lines: code-only height, sinking the table's requirements below the maximum
  "| A | B |", // line 8 — header row → 10px
  "| - | - |",
  "| r1a | r1b |", // line 10 → 30px
  "| r2a | r2b |", // line 11 → 30px
  "| r3a | r3b |", // line 12 — the LAST row (default 30px; only its top matters)
  "",
  "- item one", // line 14 → 10px
  "- item two", // line 15 → 34px
  "- item three", // line 16 — the LAST item
  "",
].join("\n");

const isRow = (needle: string) => (el: Element) =>
  el.tagName === "TR" && (el.textContent ?? "").includes(needle);
const isItem = (needle: string) => (el: Element) =>
  el.tagName === "LI" && (el.textContent ?? "").includes(needle);
const isPara = (needle: string) => (el: Element) =>
  el.tagName === "P" && (el.textContent ?? "").includes(needle);

/** One complete user scroll of the formatted pane (the harness's shared couple+settle gesture),
 *  returning where the code pane coupled to. */
async function coupledCodeTopFor(formattedTop: number): Promise<number> {
  await scrollPane(formattedPane(), formattedTop);
  return codeScroller().scrollTop;
}

describe("container-tail alignment through the shipped bundle (T-112)", () => {
  let artifact: BundleArtifact;
  let app: WiredApp;

  beforeAll(() => {
    artifact = buildBundle();
    installLayoutAdapter();
  });

  it("keeps the last table row and last list item in step with the container start", async () => {
    app = wire(artifact.code, artifact.html, artifact.css);
    await loadDocument(app, FIXTURE);
    expect(app.sent.map((frame) => frame.kind)).toContain("ready");

    setLeafHeight(findLeaf(isPara("tall rendered paragraph")), 400);
    setLeafHeight(findLeaf(isRow("AB")), 10);
    setLeafHeight(findLeaf(isRow("r1a")), 30);
    setLeafHeight(findLeaf(isRow("r2a")), 30);
    setLeafHeight(findLeaf(isItem("item one")), 10);
    setLeafHeight(findLeaf(isItem("item two")), 34);
    window.dispatchEvent(new Event("resize"));
    await flushFrames();

    // The floor's spacers are physically INSIDE the containers: one right above the last table row,
    // one right above the last list item (CodeMirror block widgets sit as siblings of the lines).
    const beforeLine = (spacer: HTMLElement) => spacer.nextElementSibling?.textContent ?? "";
    expect(spacerElements().some((s) => beforeLine(s) === "| r3a | r3b |")).toBe(true);
    expect(spacerElements().some((s) => beforeLine(s) === "- item three")).toBe(true);

    // The user-visible contract: coupling the formatted pane to the container start and to its last
    // row moves the code pane by the SAME distance — the container ends in step in both panes, even
    // though its intermediate rows stay (acceptedly) adrift. Without the floor the code-side distance
    // is the containers' unpadded source span — 14px short for the table, 16px for the list.
    const headerTop = formattedTopOf(findLeaf(isRow("AB")));
    const lastRowTop = formattedTopOf(findLeaf(isRow("r3a")));
    const codeAtHeader = await coupledCodeTopFor(headerTop);
    const codeAtLastRow = await coupledCodeTopFor(lastRowTop);
    expect(Math.abs(codeAtLastRow - codeAtHeader - (lastRowTop - headerTop))).toBeLessThanOrEqual(
      1,
    );

    const firstItemTop = formattedTopOf(findLeaf(isItem("item one")));
    const lastItemTop = formattedTopOf(findLeaf(isItem("item three")));
    const codeAtFirstItem = await coupledCodeTopFor(firstItemTop);
    const codeAtLastItem = await coupledCodeTopFor(lastItemTop);
    expect(
      Math.abs(codeAtLastItem - codeAtFirstItem - (lastItemTop - firstItemTop)),
    ).toBeLessThanOrEqual(1);
  });
});
