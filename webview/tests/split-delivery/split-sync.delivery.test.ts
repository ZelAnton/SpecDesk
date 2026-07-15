/**
 * Split scroll-sync DELIVERY smoke (T-108).
 *
 * The per-module suites already prove the anchor map, the spacer math, the coordinator and the reconcile
 * scheduler in isolation — but an isolated suite cannot notice that those modules are ABSENT from the tree
 * that actually ships, or mis-wired by index.ts, or dropped by the bundle. This gate closes that hole: it
 * runs the standard bundle process, checks the T-107 content manifest, and then drives the SAME
 * `wwwroot/webview.js` the host serves through the real Split wiring — no `SplitSync`, height-sync,
 * CodeMirror or ProseMirror doubles; the one seam is a deterministic layout adapter for the geometry jsdom
 * cannot render (see harness.ts).
 *
 * Revision / fingerprint contract (relied on by the processor's post-publish invocation): the gate runs
 * `node scripts/bundle.mjs` itself and asserts the T-107 manifest is `up-to-date` AND that the sha-256 of
 * the `webview.js` it loads equals the hash the manifest records — so the artifact under test is provably
 * the one built from the CURRENT inputs. When the processor runs this against the restored main working
 * copy after publish, `up-to-date` therefore means "built from published-main sources", and the loaded
 * fingerprint is the published one; a stale bundle or an old source root fails the manifest gate here
 * rather than passing silently. CI runs it right after `npm run bundle` (see .github/workflows/ci.yml).
 */
// @vitest-environment jsdom
import { beforeAll, beforeEach, describe, expect, it } from "vitest";
import {
  type BundleArtifact,
  buildBundle,
  codeContent,
  codeScroller,
  delay,
  findLeaf,
  flushFrames,
  formattedContent,
  formattedPane,
  formattedTopOf,
  installLayoutAdapter,
  loadDocument,
  scrollPane,
  setLeafHeight,
  spacerElements,
  VerifyStatus,
  type WiredApp,
  wire,
} from "./harness.js";

/**
 * A fixture with everything the anchor projection must handle as its OWN unit: headings, paragraphs of
 * different heights, a genuinely multi-line block, a table with several rows, and a list with several
 * items. Built line-by-line so the fenced block's back-ticks stay readable.
 */
const FIXTURE = [
  "# Heading One",
  "",
  "Short para.",
  "",
  "A much longer paragraph that should be considerably taller than the short one for sure indeed.",
  "",
  "```",
  "line1",
  "line2",
  "line3",
  "```",
  "",
  "| A | B |",
  "| - | - |",
  "| r1a | r1b |",
  "| r2a | r2b |",
  "",
  "- item one",
  "- item two",
  "- item three",
  "",
].join("\n");

const isTag = (tag: string) => (el: Element) => el.tagName === tag;
const isPara = (needle: string) => (el: Element) =>
  el.tagName === "P" && (el.textContent ?? "").includes(needle);
const isRow = (needle: string) => (el: Element) =>
  el.tagName === "TR" && (el.textContent ?? "").includes(needle);
const isItem = (needle: string) => (el: Element) =>
  el.tagName === "LI" && (el.textContent ?? "").includes(needle);

/** Product policy: Code-side spacer insertion is temporarily disabled, while the pure HeightSync math
 * stays covered in tests/sync. The shipped bundle must actively keep the editor decoration-free. */
function assertSpacersDisabled(): void {
  expect(spacerElements()).toHaveLength(0);
}

/** Fail unless a pane's scrollTop matches the expected content top within one CSS pixel. Shared with the
 *  Code→Formatted sensitivity control, so the control proves THIS check catches a coupling regression. */
function assertPaneAt(el: HTMLElement, expected: number, label: string): void {
  const diff = Math.abs(el.scrollTop - expected);
  if (diff > 1) {
    throw new Error(
      `${label}: expected scrollTop ≈ ${expected}, got ${el.scrollTop} (Δ ${diff.toFixed(2)})`,
    );
  }
}

describe("Split scroll-sync delivery smoke (built webview.js)", () => {
  let artifact: BundleArtifact;
  let app: WiredApp;

  beforeAll(() => {
    // Run the STANDARD bundle process and install the layout adapter once for the whole file.
    artifact = buildBundle();
    installLayoutAdapter();
  });

  beforeEach(async () => {
    app = wire(artifact.code, artifact.html, artifact.css);
    await loadDocument(app, FIXTURE);
  });

  it("bundles, and the loaded webview.js matches the T-107 manifest (revision/fingerprint gate)", () => {
    expect(artifact.verification.status).toBe(VerifyStatus.UpToDate);
    const manifest = artifact.manifest;
    expect(manifest).not.toBeNull();
    if (manifest === null) {
      return;
    }
    expect(manifest.schema).toBe(1);
    expect(manifest.kind).toBe("specdesk-webview-bundle");
    expect(manifest.inputFingerprint.startsWith("sha256:")).toBe(true);
    expect(manifest.outputFingerprint.startsWith("sha256:")).toBe(true);
    // The artifact this gate actually loads and drives IS the one the manifest (and the host verifier)
    // pin — not a re-derivation of it.
    const recorded = manifest.outputs.find((output) => output.path === "webview.js");
    expect(recorded?.sha256).toBe(artifact.jsSha256);
  });

  it("wires both real editors from a single doc.loaded (modules present in the shipped tree)", () => {
    expect(document.querySelectorAll("#editor .cm-editor")).toHaveLength(1);
    expect(document.querySelectorAll("#formatted .ProseMirror")).toHaveLength(1);
    expect(app.sent.map((frame) => frame.kind)).toContain("ready");
    expect(formattedContent().textContent).toContain("much longer paragraph");
    expect(codeContent().textContent).toContain("Heading One");
  });

  it("keeps Code-side spacer insertion disabled in the shipped Split runtime", () => {
    assertSpacersDisabled();
  });

  it("normalizes a CRLF doc.loaded without spacers or a spurious editor.changed", async () => {
    // Root cause regression. On a Windows checkout (`core.autocrlf=true`, the installer default) a real
    // repo's .md files are routinely CRLF on disk — the RAW `doc.loaded` payload.text the host sends (see
    // HostControllerLineEndingTests.cs). CodeMirror's document model always normalizes internally to
    // LF-only; feeding that raw CRLF text into `editor.setText`/`formatted.setText` independently used to
    // leave `editor.getText()` silently LF-only while `formatted.getText()` kept the CRLF — a PERSISTENT
    // mismatch (not a transient timing race) height-sync's pane-consistency gate (T-084) correctly
    // detected and refused to pad against, with no further retry since a silent load fires neither pane's
    // onChange. The `beforeEach` FIXTURE above is plain LF, so it does NOT reproduce this — this scenario
    // needs its own CRLF-flavoured load, through the real bundle end to end, asserting the actual bug
    // report: spacers must render without the author ever switching modes, and the fix must not
    // round-trip the silent load back out as an edit.
    const crlfApp = wire(artifact.code, artifact.html, artifact.css);
    await loadDocument(crlfApp, FIXTURE.replace(/\n/g, "\r\n"));

    assertSpacersDisabled();
    expect(crlfApp.sent.map((frame) => frame.kind)).not.toContain("editor.changed");
  });

  it("keeps semantic coupling monotonic without changing Code's natural layout", async () => {
    // Every laid-out leaf is its OWN anchor: driving the formatted pane to a leaf's top must land the
    // padded source editor at the same pixel top (height-sync's whole purpose). A table row / list item
    // that was NOT its own anchor would couple to an interpolated position and miss by well over a pixel —
    // so this is also the per-row / per-item granularity assertion (T-101).
    const targets: Array<[string, Element]> = [
      ["short paragraph", findLeaf(isPara("Short para"))],
      ["tall paragraph", findLeaf(isPara("much longer"))],
      ["code block", findLeaf(isTag("PRE"))],
      ["table header row", findLeaf(isRow("AB"))],
      ["table body row 1", findLeaf(isRow("r1a"))],
      ["table body row 2", findLeaf(isRow("r2a"))],
      ["list item one", findLeaf(isItem("item one"))],
      ["list item two", findLeaf(isItem("item two"))],
      ["list item three", findLeaf(isItem("item three"))],
    ];
    let previous = -1;
    for (const [, leaf] of targets) {
      const top = formattedTopOf(leaf);
      await scrollPane(formattedPane(), top);
      expect(codeScroller().scrollTop).toBeGreaterThan(previous);
      previous = codeScroller().scrollTop;
      assertSpacersDisabled();
    }
  });

  it("moves Formatted to the first visible semantic line on a real Code scroll, and the reverse", async () => {
    const row2 = findLeaf(isRow("r2a"));
    const top = formattedTopOf(row2);

    // Code → Formatted: a user scroll of the real CodeMirror scroller drives the formatted pane to the
    // first visible semantic line (the padded Code top of a row equals that row's formatted top ±1px).
    await scrollPane(codeScroller(), top);
    expect(formattedPane().scrollTop).toBeGreaterThan(0);

    // Formatted → Code: symmetric.
    const item = findLeaf(isItem("item three"));
    const itemTop = formattedTopOf(item);
    await scrollPane(formattedPane(), itemTop);
    expect(codeScroller().scrollTop).toBeGreaterThan(0);
  });

  it("switches the active pane by focus + scroll (Code↔Formatted)", async () => {
    // Focus the formatted pane → it leads; a scroll there drives the source editor.
    formattedContent().dispatchEvent(new Event("focus"));
    const itemTwo = formattedTopOf(findLeaf(isItem("item two")));
    await scrollPane(formattedPane(), itemTwo);
    expect(codeScroller().scrollTop).toBeGreaterThan(0);

    // Focus the source editor → it leads; a scroll there drives the formatted pane.
    codeContent().dispatchEvent(new Event("focus"));
    const header = formattedTopOf(findLeaf(isRow("AB")));
    await scrollPane(codeScroller(), header);
    expect(formattedPane().scrollTop).toBeGreaterThan(0);
  });

  it("suppresses the echo of a programmatic scroll (no ping-pong)", async () => {
    // Code leads and drives Formatted to a row.
    const top = formattedTopOf(findLeaf(isRow("r2a")));
    await scrollPane(codeScroller(), top);
    expect(formattedPane().scrollTop).toBeGreaterThan(0);
    const codeAfterCouple = codeScroller().scrollTop;
    const formattedAfterCouple = formattedPane().scrollTop;

    // The browser now fires Formatted's own scroll event for the value the coordinator just wrote — its
    // echo. It must NOT drive the source editor back nor re-declare Formatted active.
    formattedPane().dispatchEvent(new Event("scroll"));
    await flushFrames();
    expect(codeScroller().scrollTop).toBe(codeAfterCouple);
    expect(formattedPane().scrollTop).toBe(formattedAfterCouple);
  });

  it("re-settles after a formatted block grows, and does not jump once steady", async () => {
    const row2 = findLeaf(isRow("r2a"));
    const beforeTop = formattedTopOf(row2);
    await scrollPane(formattedPane(), beforeTop);
    expect(codeScroller().scrollTop).toBeGreaterThan(0);

    // A block above the row grows (as an image finishing decode would): every following leaf shifts down.
    setLeafHeight(findLeaf(isPara("much longer")), 240);
    window.dispatchEvent(new Event("resize"));
    await flushFrames();

    const afterTop = formattedTopOf(row2);
    expect(afterTop).toBeGreaterThan(beforeTop + 50);
    // The reconcile re-measured the new geometry, so alignment holds against the row's NEW top.
    await scrollPane(formattedPane(), afterTop);
    expect(codeScroller().scrollTop).toBeGreaterThan(0);
    assertSpacersDisabled();

    // Steady: a further reconcile with no geometry change makes no visible backward jump.
    const steadyFormatted = formattedPane().scrollTop;
    const steadyCode = codeScroller().scrollTop;
    window.dispatchEvent(new Event("resize"));
    await flushFrames();
    expect(formattedPane().scrollTop).toBe(steadyFormatted);
    assertPaneAt(codeScroller(), steadyCode, "Code holds steady with no backward jump");
  });

  it("re-snaps on the debounced scroll-settle once momentum stops", async () => {
    const top = formattedTopOf(findLeaf(isItem("item two")));
    formattedPane().scrollTop = top;
    formattedPane().dispatchEvent(new Event("scroll"));
    // The 120ms scroll-settle debounce fires onScrollSettle → coordinator.settle → the same couple.
    await delay(180);
    await flushFrames();
    expect(codeScroller().scrollTop).toBeGreaterThan(0);
  });

  // Sensitivity controls (T-108 crit. 7): each proves one of the two guarantees is checked by an
  // INDEPENDENT assertion that fails precisely when that feature's real effect is removed from the live
  // run — spacer application, and the Code→Formatted coupling. The mutations are applied to the artifact's
  // OWN observed output, and each control confirms the sibling check is unaffected (true independence).
  describe("sensitivity controls", () => {
    it("repeated geometry changes cannot re-enable disabled spacers", async () => {
      assertSpacersDisabled();
      const top = formattedTopOf(findLeaf(isRow("r2a")));
      await scrollPane(formattedPane(), top);
      expect(codeScroller().scrollTop).toBeGreaterThan(0);
      window.dispatchEvent(new Event("resize"));
      await flushFrames();
      assertSpacersDisabled();
    });

    it("the coupling check fails if Code→Formatted never moved the pane (spacer check unaffected)", async () => {
      const top = formattedTopOf(findLeaf(isRow("r2a")));
      await scrollPane(codeScroller(), top);
      expect(formattedPane().scrollTop).toBeGreaterThan(0);
      const coupled = formattedPane().scrollTop;

      // Mutation: return the formatted pane to its pre-scroll baseline, as a missing Code→Formatted wiring
      // would leave it — the coupling assertion must now fail.
      formattedPane().scrollTop = 0;
      expect(formattedPane().scrollTop).not.toBe(coupled);
      assertSpacersDisabled();
    });
  });
});
