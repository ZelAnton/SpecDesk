import { describe, expect, it } from "vitest";
import type { MarkdownEditor } from "../../src/editors/editor.js";
import type { BlockGeometry } from "../../src/review/preview.js";
import { type EditorSpacer, type GeometrySource, HeightSync } from "../../src/sync/height-sync.js";
import type { ScrollAnchor } from "../../src/sync/scroll-map.js";
import {
  type EditorScrollTarget,
  type FormattedScrollTarget,
  SplitSync,
} from "../../src/sync/sync-coordinator.js";

/**
 * Structural read/write BUDGET tests for one Split reconcile generation, driven through the REAL HeightSync
 * and SplitSync (not the pure-math fakes the sibling suites use). The fakes below are measurement spies: they
 * count every expensive layout op (a Formatted DOM measure, a CodeMirror tops read, a spacer dispatch, a
 * scrollTop write) and log their order, so the tests pin the invariants the task fixes — one measure batch
 * per pane, at most one passive write, no re-measure AFTER the write, and zero writes once settled — as
 * structural facts rather than a flaky wall-clock threshold.
 */

// A reference geometry whose editor natural tops underestimate the second block, so the reconcile plants a
// 160px spacer below line 5 and the editor's line-7 anchor is padded from 40 up to the preview's 200.
const geometry: BlockGeometry[] = [
  { lineStart: 0, lineEnd: 5, top: 0, height: 200 },
  { lineStart: 7, lineEnd: 7, top: 200, height: 40 },
];
const naturalTops = new Map<number, number>([
  [0, 0],
  [7, 40],
]);
// The padded tops the couple path's topsForLines would re-read (natural + the 160px spacer above line 7),
// plus the trailing anchor — only consulted if the coordinator ever re-measures, which these tests forbid.
const paddedTops = new Map<number, number>([
  [0, 0],
  [7, 200],
  [8, 240],
]);

/** A shared ordered log of the expensive ops, tagged read vs write, so a test can assert no read follows a
 *  write within one reconcile. Scalar scrollTop reads are deliberately NOT logged — the invariant is about
 *  re-measuring block geometry after the write, not reading back a scroll offset for echo/couple. */
type Op =
  | "measure:formatted"
  | "read:editorTops"
  | "read:viewportLine"
  | "write:spacers"
  | "write:scroll:editor"
  | "write:scroll:formatted";

class FakeFormatted implements GeometrySource, FormattedScrollTarget {
  scroll = 0;
  measures = 0;
  private cacheStale = true;
  constructor(private readonly log: Op[]) {}

  /** Model the FormattedEditor cache: a genuine geometry change re-measures, an unchanged layout reuses. */
  invalidate(): void {
    this.cacheStale = true;
  }

  private measure(): void {
    this.measures += 1;
    this.cacheStale = false;
    this.log.push("measure:formatted");
  }

  blockGeometry(): BlockGeometry[] {
    // The reconcile path always takes the freshest measurement (and refreshes the cache).
    this.measure();
    return geometry;
  }

  blockAnchors(): readonly ScrollAnchor[] {
    if (this.cacheStale) {
      this.measure();
    }
    const anchors: ScrollAnchor[] = geometry.map((block) => ({
      line: block.lineStart,
      px: block.top,
    }));
    const last = geometry[geometry.length - 1];
    if (last) {
      anchors.push({ line: last.lineEnd + 1, px: last.top + last.height });
    }
    return anchors;
  }

  contentWidth(): number {
    return 800;
  }

  hasPendingChange(): boolean {
    return false;
  }

  getText(): string {
    return "same";
  }

  topLine(): number {
    return 0;
  }

  scrollTop(): number {
    return this.scroll;
  }

  setScrollTop(px: number): void {
    this.scroll = px;
    this.log.push("write:scroll:formatted");
  }

  reveal(): void {
    return;
  }

  scrollToLine(): void {
    return;
  }
}

class FakeEditor implements EditorScrollTarget {
  scroll = 0;
  naturalReads = 0;
  topsForLinesReads = 0;
  spacerDispatches = 0;
  private viewportLine = 0;
  constructor(private readonly log: Op[]) {}

  hasPendingChange(): boolean {
    return false;
  }

  getText(): string {
    return "same";
  }

  contentWidth(): number {
    return 800;
  }

  naturalLineTops(lines: number[]): (number | null)[] {
    this.naturalReads += 1;
    this.log.push("read:editorTops");
    return lines.map((line) => naturalTops.get(line) ?? 0);
  }

  topVisibleLine(): number {
    this.log.push("read:viewportLine");
    return this.viewportLine;
  }

  setSpacers(_spacers: EditorSpacer[], _lead = 0): void {
    this.spacerDispatches += 1;
    this.log.push("write:spacers");
  }

  adjustScrollTop(delta: number): void {
    this.scroll += delta;
  }

  topLine(): number {
    return this.scroll / 10;
  }

  topsForLines(lines: readonly number[]): number[] {
    this.topsForLinesReads += 1;
    this.log.push("read:editorTops");
    return lines.map((line) => paddedTops.get(line) ?? 0);
  }

  scrollTop(): number {
    return this.scroll;
  }

  setScrollTop(px: number): void {
    this.scroll = px;
    this.log.push("write:scroll:editor");
  }

  reveal(): void {
    return;
  }

  scrollToLine(): void {
    return;
  }
}

function harness(): {
  editor: FakeEditor;
  formatted: FakeFormatted;
  height: HeightSync;
  sync: SplitSync;
  log: Op[];
  reconcile: () => void;
} {
  const log: Op[] = [];
  const editor = new FakeEditor(log);
  const formatted = new FakeFormatted(log);
  const height = new HeightSync(editor as unknown as MarkdownEditor, formatted);
  const sync = new SplitSync(editor, formatted);
  const reconcile = (): void => sync.reconciled(height.reconcile());
  return { editor, formatted, height, sync, log, reconcile };
}

describe("Split reconcile generation budget (T-104)", () => {
  it("measures each pane once, writes the passive once, and never re-reads layout after the write", () => {
    const { editor, formatted, log, reconcile } = harness();
    editor.scroll = 100; // the editor leads at line 10, so the passive formatted pane must be coupled

    reconcile();

    // One Formatted DOM measure batch and one CodeMirror tops read batch for the whole generation.
    expect(formatted.measures).toBe(1);
    expect(editor.naturalReads).toBe(1);
    // The coordinator adopted the snapshot — it never re-read the editor's own padded tops.
    expect(editor.topsForLinesReads).toBe(0);
    // At most one passive scrollTop write (formatted coupled from the editor's line 10 → px 100).
    expect(log.filter((op) => op === "write:scroll:formatted")).toHaveLength(1);
    expect(formatted.scroll).toBe(100);

    // No expensive layout read appears AFTER the first write in the same generation — the read→write→read
    // forced layout this fixes is gone.
    const firstWrite = log.findIndex((op) => op.startsWith("write:"));
    const reads = log
      .map((op, index) => ({ op, index }))
      .filter(({ op }) => op.startsWith("measure:") || op.startsWith("read:"));
    expect(firstWrite).toBeGreaterThanOrEqual(0);
    expect(reads.every(({ index }) => index < firstWrite)).toBe(true);
  });

  it("makes ZERO writes on a stable repeat reconcile (a fixed point)", () => {
    const { editor, formatted, log, reconcile } = harness();
    editor.scroll = 100;

    reconcile(); // first pass: dispatches the spacer and couples the passive
    const measuresAfterFirst = formatted.measures;
    const dispatchesAfterFirst = editor.spacerDispatches;
    log.length = 0; // watch only the second, settled pass

    reconcile(); // geometry unchanged → identical spacer set → no dispatch, passive already on target

    expect(editor.spacerDispatches).toBe(dispatchesAfterFirst); // no new spacer dispatch
    expect(log.filter((op) => op.startsWith("write:"))).toHaveLength(0); // zero writes at all
    // The read phase still measures (that is how it detects the fixed point), but only once more.
    expect(formatted.measures).toBe(measuresAfterFirst + 1);
  });

  it("a pure scroll on the reconciled maps forces no Formatted measure and no editor tops read", () => {
    const { editor, formatted, sync, reconcile } = harness();
    editor.scroll = 100;
    reconcile(); // establishes the maps from the snapshot (dirty cleared)

    const measuresBefore = formatted.measures;
    const naturalBefore = editor.naturalReads;

    // The author now genuinely scrolls the editor (within the mapped range): the coordinator couples the
    // passive through the CACHED maps — a scroll does not move blocks, so it re-measures nothing.
    editor.scroll = 150;
    sync.onEditorScroll();

    expect(formatted.measures).toBe(measuresBefore); // no getBoundingClientRect-equivalent
    expect(editor.naturalReads).toBe(naturalBefore); // no CodeMirror tops read
    expect(editor.topsForLinesReads).toBe(0);
    expect(formatted.scroll).toBe(150); // still coupled correctly from the cached maps (identity geometry)
  });
});
