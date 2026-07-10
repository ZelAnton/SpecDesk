import { beforeEach, describe, expect, it, vi } from "vitest";
import { formatMarkdown } from "../../src/editors/md-format.js";
import { serializeWithSplice } from "../../src/editors/md-splice.js";
import { parser } from "../../src/editors/pm-markdown.js";
import { type DiffSurface, ReviewController, type ReviewDeps } from "../../src/review/review.js";
import { type ScrollAnchor, ScrollMap } from "../../src/sync/scroll-map.js";
import {
  type EditorScrollTarget,
  type FormattedScrollTarget,
  SplitSync,
} from "../../src/sync/sync-coordinator.js";
import { log } from "../../src/util/log.js";
import { type TraceEntry, trace } from "../../src/util/trace.js";
import { IpcClient } from "../../src/wire/ipc.js";

// The splice fallback (and the trace error-forward) reach the `log` channel; mock it so the test can
// assert the fallback streamed a log frame without dragging in the IPC transport.
vi.mock("../../src/util/log.js", () => ({
  log: { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

const entries = (): TraceEntry[] => trace.snapshot().entries;
const eventNames = (): string[] => entries().map((entry) => entry.event);
const find = (event: string): TraceEntry | undefined => entries().find((e) => e.event === event);

beforeEach(() => {
  trace.clear();
  trace.enabled = true;
  trace.verbose = false;
});

describe("trace.v (verbose-gated helper)", () => {
  it("is a no-op unless verbose is on, then records", () => {
    trace.v("scroll", "scroll.skip", { verdict: "epsilon" });
    expect(entries()).toHaveLength(0);

    trace.setVerbose(true);
    trace.v("scroll", "scroll.skip", { verdict: "epsilon" });
    expect(eventNames()).toEqual(["scroll.skip"]);
  });
});

describe("format instrumentation (md-format)", () => {
  it("a selection inside existing bold traces format.inline{decision:'unwrap-node'}", () => {
    formatMarkdown("**bold**", 2, 6, "bold");
    expect(find("format.inline")?.data).toMatchObject({ decision: "unwrap-node" });
  });

  it("bold on plain text traces format.inline{decision:'wrap'}", () => {
    formatMarkdown("plain", 0, 5, "bold");
    expect(find("format.inline")?.data).toMatchObject({ decision: "wrap" });
  });

  it("converting a bullet list to ordered traces format.linePrefix{decision:'convert'}", () => {
    formatMarkdown("- item", 0, 6, "ordered");
    expect(find("format.linePrefix")?.data).toMatchObject({ decision: "convert" });
  });
});

describe("splice instrumentation (md-splice)", () => {
  it("a block/node count mismatch traces splice.fallback + streams a log.warn", () => {
    const edited = parser.parse("a\n\nb\n\nc"); // three top-level nodes
    expect(edited).not.toBeNull();
    if (edited === null) {
      return;
    }
    serializeWithSplice("one block", edited); // one original block → 1 vs 3

    expect(find("splice.fallback")?.data).toMatchObject({ reason: "count-mismatch" });
    expect(vi.mocked(log.warn)).toHaveBeenCalled();
  });
});

/** A scriptable stand-in for both Split panes, mapping scrollTop↔line through its own anchors. */
class FakePane implements EditorScrollTarget, FormattedScrollTarget {
  scroll = 0;
  private readonly map: ScrollMap;

  constructor(private readonly anchors: ScrollAnchor[]) {
    this.map = new ScrollMap(anchors);
  }

  topLine(): number {
    return this.map.lineForPx(this.scroll);
  }
  topsForLines(lines: readonly number[]): number[] {
    return lines.map((line) => this.map.pxForLine(line));
  }
  blockAnchors(): readonly ScrollAnchor[] {
    return this.anchors;
  }
  scrollTop(): number {
    return this.scroll;
  }
  setScrollTop(px: number): void {
    this.scroll = px;
  }
  reveal(): void {}
  scrollToLine(): void {}
  userScrollTo(px: number): void {
    this.scroll = px;
  }
}

describe("scroll instrumentation (sync-coordinator, hot path)", () => {
  it("a genuine scroll traces drive→write; its echo traces neither a write nor a repeat drive", () => {
    const anchors: ScrollAnchor[] = [
      { line: 0, px: 0 },
      { line: 10, px: 200 },
    ];
    const editor = new FakePane(anchors);
    const formatted = new FakePane(anchors);
    const sync = new SplitSync(editor, formatted);

    editor.userScrollTo(100);
    sync.onEditorScroll();
    expect(find("scroll.drive")?.data).toMatchObject({ verdict: "drive" });
    expect(eventNames()).toContain("scroll.write");

    // The couple wrote the formatted pane to px 100, so its own scroll event is an echo.
    trace.clear();
    sync.onFormattedScroll();
    expect(eventNames()).not.toContain("scroll.write");
    expect(find("scroll.drive")?.data).toMatchObject({ verdict: "echo" });

    // A steady run of echoes is edge-triggered → zero further drive entries.
    trace.clear();
    sync.onFormattedScroll();
    expect(entries().filter((e) => e.event === "scroll.drive")).toHaveLength(0);
  });
});

describe("reconcile instrumentation (sync-coordinator)", () => {
  it("reconciled(null) traces reconcile.gated; a snapshot traces reconcile.adopted", () => {
    const anchors: ScrollAnchor[] = [
      { line: 0, px: 0 },
      { line: 10, px: 200 },
    ];
    const sync = new SplitSync(new FakePane(anchors), new FakePane(anchors));

    sync.reconciled(null);
    expect(eventNames()).toContain("reconcile.gated");

    trace.clear();
    sync.reconciled({ formatted: anchors, editor: anchors, changed: true });
    expect(find("reconcile.adopted")?.data).toMatchObject({ anchors: 2, changed: true });
  });
});

describe("review instrumentation (review)", () => {
  it("a stale applyResult traces review.result{stale:true}", () => {
    let version = 3;
    const surface: DiffSurface = {
      getText: () => "",
      setDiff: () => {},
      clearDiff: () => {},
      hasPendingChange: () => false,
    };
    const deps: ReviewDeps = {
      surfaces: [surface, surface],
      setPressed: () => {},
      requestCompare: () => {},
      docVersion: () => version,
      onEmptyState: () => {},
      onOverflow: () => {},
    };
    const review = new ReviewController(deps);
    review.toggle(); // enter review at version 3

    trace.clear();
    version = 5; // the document moved on before the result arrived
    review.applyResult(3, []); // a result computed against the now-stale version 3

    expect(find("review.result")?.data).toMatchObject({ stale: true, version: 3, docVersion: 5 });
  });
});

describe("ipc instrumentation (wire/ipc)", () => {
  it("ipc.send skips the log channel but traces other kinds", () => {
    const sent: string[] = [];
    const client = new IpcClient({
      sendMessage: (message: string) => {
        sent.push(message);
      },
      receiveMessage: () => {},
    });

    client.send("log", { level: "info", message: "x" });
    expect(eventNames()).not.toContain("ipc.send");

    client.send("ready", null);
    expect(find("ipc.send")?.data).toMatchObject({ kind: "ready" });
  });
});
