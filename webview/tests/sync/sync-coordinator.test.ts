import { describe, expect, it } from "vitest";
import { type ScrollAnchor, ScrollMap } from "../../src/sync/scroll-map.js";
import {
  type EditorScrollTarget,
  type FormattedScrollTarget,
  REVEAL_GUARD_MS,
  SplitSync,
} from "../../src/sync/sync-coordinator.js";

/**
 * A scriptable stand-in for both Split panes: it converts scrollTop↔line through its own ScrollMap (built
 * from the same anchors the coordinator reads), so driving one pane and reading its top line behaves like
 * a real pane. Every scroll WRITE and reveal is recorded so a test can assert exactly what the coordinator
 * did — and `userScrollTo` simulates a genuine (non-coordinator) scroll before the test hands the matching
 * onScroll to the coordinator.
 */
class FakePane implements EditorScrollTarget, FormattedScrollTarget {
  scroll = 0;
  readonly reveals: number[] = [];
  readonly scrolledToLine: number[] = [];
  anchorCalls = 0;
  private anchors: ScrollAnchor[];
  private map: ScrollMap;

  constructor(anchors: ScrollAnchor[]) {
    this.anchors = anchors;
    this.map = new ScrollMap(anchors);
  }

  setAnchors(anchors: ScrollAnchor[]): void {
    this.anchors = anchors;
    this.map = new ScrollMap(anchors);
  }

  /** Simulate a genuine user scroll (moves scrollTop off any coordinator-written value). */
  userScrollTo(px: number): void {
    this.scroll = px;
  }

  topLine(): number {
    return this.map.lineForPx(this.scroll);
  }

  topsForLines(lines: readonly number[]): number[] {
    this.anchorCalls += 1;
    return lines.map((line) => this.map.pxForLine(line));
  }

  blockAnchors(): readonly ScrollAnchor[] {
    this.anchorCalls += 1;
    return this.anchors;
  }

  scrollTop(): number {
    return this.scroll;
  }

  setScrollTop(px: number): void {
    this.scroll = px;
  }

  reveal(line: number): void {
    this.reveals.push(line);
  }

  scrollToLine(line: number): void {
    this.scrolledToLine.push(line);
    this.scroll = this.map.pxForLine(line);
  }
}

/** A mutable fake clock so the reveal-vs-couple guard can be advanced deterministically. */
function fakeClock(): {
  now: () => number;
  advance: (ms: number) => void;
  set: (t: number) => void;
} {
  let t = 1_000;
  return {
    now: () => t,
    advance: (ms) => {
      t += ms;
    },
    set: (value) => {
      t = value;
    },
  };
}

// Identity geometry: px = 10·line in both panes, so a coupled scroll lands the sibling at the same px —
// keeping the arithmetic obvious. A separate suite below uses mismatched geometry to prove the map math.
const identity: ScrollAnchor[] = [
  { line: 0, px: 0 },
  { line: 100, px: 1000 },
];

function make(
  edAnchors = identity,
  fmAnchors = identity,
): { ed: FakePane; fm: FakePane; sync: SplitSync; clock: ReturnType<typeof fakeClock> } {
  const ed = new FakePane(edAnchors);
  const fm = new FakePane(fmAnchors);
  const clock = fakeClock();
  const sync = new SplitSync(ed, fm, clock.now);
  return { ed, fm, sync, clock };
}

describe("SplitSync echo suppression (deterministic, no driver lock)", () => {
  it("drives the sibling once and suppresses the resulting echo scroll", () => {
    const { ed, fm, sync } = make();

    ed.userScrollTo(200);
    sync.onEditorScroll();
    expect(fm.scroll).toBe(200); // formatted coupled to the editor's line 20

    // The write fired formatted's scroll event: it settles on exactly what we wrote → echo → must NOT
    // drive the editor back.
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(200); // untouched
  });

  it("lets a genuine scroll of the other pane drive back after a couple", () => {
    const { ed, fm, sync } = make();

    ed.userScrollTo(200);
    sync.onEditorScroll();
    sync.onFormattedScroll(); // echo, ignored

    fm.userScrollTo(500); // the author now genuinely scrolls the formatted pane
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(500);

    sync.onEditorScroll(); // the editor's own echo of that drive
    expect(fm.scroll).toBe(500); // untouched
  });

  it("suppresses every echo through a burst of active-pane scrolls (momentum)", () => {
    const { ed, fm, sync } = make();

    for (const px of [100, 140, 190, 250, 320]) {
      ed.userScrollTo(px);
      sync.onEditorScroll();
      sync.onFormattedScroll(); // the coupling's echo each frame
    }
    expect(fm.scroll).toBe(320); // followed the editor all the way
    expect(ed.scroll).toBe(320); // never yanked back by an echo
  });
});

describe("SplitSync couples through the line↔px map", () => {
  it("converts through mismatched pane geometries by line, not by raw pixel delta", () => {
    // The editor is twice as tall as the render (px = 20·line vs 10·line) — a "negative gap" the spacer
    // scheme cannot express. Coupling still lands the sibling at the correct LINE.
    const editorTall: ScrollAnchor[] = [
      { line: 0, px: 0 },
      { line: 100, px: 2000 },
    ];
    const { ed, fm, sync } = make(editorTall, identity);

    ed.userScrollTo(400); // editor px 400 = line 20
    sync.onEditorScroll();
    expect(fm.scroll).toBe(200); // render line 20 = px 200 (not 400)

    fm.userScrollTo(300); // render px 300 = line 30
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(600); // editor line 30 = px 600
  });

  it("does not scroll either pane when the split diverged (no anchors)", () => {
    const { ed, fm, sync } = make([], []);
    ed.userScrollTo(200);
    fm.scroll = 999; // a pre-existing position that must be left alone
    sync.onEditorScroll();
    expect(fm.scroll).toBe(999);
  });

  it("rebuilds the maps only after invalidate(), not on every scroll frame", () => {
    const { ed, fm, sync } = make();

    ed.userScrollTo(100);
    sync.onEditorScroll();
    ed.userScrollTo(200);
    sync.onEditorScroll();
    // One map build total (formatted anchors + editor tops read once each) across two scroll frames.
    expect(fm.anchorCalls).toBe(1);
    expect(ed.anchorCalls).toBe(1);

    sync.invalidate();
    ed.userScrollTo(300);
    sync.onEditorScroll();
    expect(fm.anchorCalls).toBe(2);
    expect(ed.anchorCalls).toBe(2);
  });

  it("picks up changed geometry after invalidate()", () => {
    const { ed, fm, sync } = make();
    ed.userScrollTo(200);
    sync.onEditorScroll();
    expect(fm.scroll).toBe(200);

    // The render pane grows: the same editor line now maps to a different pixel.
    fm.setAnchors([
      { line: 0, px: 0 },
      { line: 100, px: 2000 },
    ]);
    sync.invalidate();
    ed.userScrollTo(200); // line 20
    sync.onEditorScroll();
    expect(fm.scroll).toBe(400); // line 20 on the taller render = px 400
  });
});

describe("SplitSync.syncFrom (programmatic mirror re-align)", () => {
  it("drives the sibling to the source pane's top line without an echo check", () => {
    const { ed, fm, sync } = make();
    ed.userScrollTo(350);
    sync.syncFrom("editor");
    expect(fm.scroll).toBe(350);
    // And it records the write, so the resulting formatted scroll event is a suppressed echo.
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(350);
  });

  it("reports whether the current pane scroll is a coordinator-written echo", () => {
    const { ed, fm, sync } = make();
    ed.userScrollTo(350);
    sync.syncFrom("editor");

    expect(sync.isEcho("formatted")).toBe(true);
    fm.userScrollTo(351);
    expect(sync.isEcho("formatted")).toBe(false);
  });
});

describe("SplitSync.reveal (passive-pane reveal, guarded against active-scroll couples)", () => {
  it("reveals the passive pane on a caret move with no recent couple", () => {
    const { fm, sync, clock } = make();
    clock.advance(REVEAL_GUARD_MS); // move well clear of the initial lastCoupledAt (−∞ anyway)
    sync.reveal(30, "editor"); // caret moved in the editor → reveal the formatted (passive) pane
    expect(fm.reveals).toEqual([30]);
  });

  it("stands down while a scroll just coupled the panes (anti-judder fallback)", () => {
    const { ed, fm, sync, clock } = make();
    ed.userScrollTo(200);
    sync.onEditorScroll(); // couples at t = clock.now()

    clock.advance(REVEAL_GUARD_MS - 1); // still inside the guard window
    sync.reveal(30, "editor");
    expect(fm.reveals).toEqual([]); // suppressed

    clock.advance(1); // now exactly REVEAL_GUARD_MS since the couple → window elapsed
    sync.reveal(30, "editor");
    expect(fm.reveals).toEqual([30]);
  });

  it("records a reveal that actually scrolled the passive pane, so its echo is suppressed", () => {
    const { ed, fm, sync, clock } = make();
    clock.advance(REVEAL_GUARD_MS);
    // Make the passive pane's reveal move its scrollTop (drift pushed the line off-screen).
    fm.reveal = (line: number) => {
      fm.reveals.push(line);
      fm.scroll = 777;
    };
    sync.reveal(30, "editor");
    expect(fm.scroll).toBe(777);

    sync.onFormattedScroll(); // the reveal's own scroll event
    expect(ed.scroll).toBe(0); // suppressed as an echo — no drive-back
  });
});

describe("SplitSync.restore (mode-switch reading-position restore)", () => {
  it("scrolls each named pane to the line and suppresses the resulting echoes", () => {
    const { ed, fm, sync } = make();
    sync.restore(40, ["editor", "formatted"]);
    expect(ed.scrolledToLine).toEqual([40]);
    expect(fm.scrolledToLine).toEqual([40]);
    expect(ed.scroll).toBe(400); // line 40 → px 400 on the identity map
    expect(fm.scroll).toBe(400);

    sync.onEditorScroll();
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(400); // neither echo drove the other
    expect(fm.scroll).toBe(400);
  });

  it("restores only the visible pane (single-pane mode) without touching the hidden sibling", () => {
    const { ed, fm, sync } = make();
    fm.scroll = 123; // hidden formatted pane keeps its position
    sync.restore(40, ["editor"]);
    expect(ed.scroll).toBe(400);
    expect(fm.scrolledToLine).toEqual([]);
    expect(fm.scroll).toBe(123);
  });
});

describe("SplitSync.reset / absorb", () => {
  it("reset() parks both panes at the top and suppresses the resulting echoes", () => {
    const { ed, fm, sync } = make();
    ed.scroll = 500;
    fm.scroll = 300;
    sync.reset();
    expect(ed.scroll).toBe(0);
    expect(fm.scroll).toBe(0);

    sync.onEditorScroll();
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(0);
    expect(fm.scroll).toBe(0);
  });

  it("absorb() claims a pane's current scroll so a height-sync nudge is not read as a user scroll", () => {
    const { ed, fm, sync } = make();
    ed.scroll = 300; // height-sync just compensated the editor's scrollTop
    sync.absorb("editor");
    sync.onEditorScroll();
    expect(fm.scroll).toBe(0); // the compensation did not drive the formatted pane
  });
});

// End-to-end Split scenarios the description calls out (набор/скролл/смена режима/reveal), driven through
// the coordinator's public surface the way index.ts wires it — proving they compose without judder or a
// mutual echo, not just each primitive in isolation.
describe("SplitSync Split scenarios (no judder, no mutual echo)", () => {
  it("typing: a mirror re-align (syncFrom) picks up the just-changed geometry, then does not echo", () => {
    const { ed, fm, sync } = make();
    // A first couple settles the panes and builds the maps off the pre-edit geometry.
    ed.userScrollTo(300);
    sync.onEditorScroll();
    expect(fm.scroll).toBe(300);

    // The author types in the editor; the mirror rewrites the formatted pane, growing it (its blocks now
    // render taller) — WITHOUT the caller invalidating the coordinator. syncFrom must still couple against
    // the fresh geometry (it self-invalidates), or the re-align would target stale pixels.
    fm.setAnchors([
      { line: 0, px: 0 },
      { line: 100, px: 2000 },
    ]);
    ed.userScrollTo(200); // editor line 20 after the edit settled the viewport
    sync.syncFrom("editor");
    expect(fm.scroll).toBe(400); // line 20 on the grown render = px 400 (fresh map), not 200 (stale map)

    // And the re-align's own formatted scroll event is a suppressed echo — the typing burst can't ping-pong.
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(200);
  });

  it("scroll then caret reveal: the reveal waits out the couple, then fires once the scroll settles", () => {
    const { ed, fm, sync, clock } = make();

    // Holding an arrow key that also scrolls the editor: each frame couples the formatted pane. A coincident
    // caret reveal into the formatted pane must stand down while that couple is fresh (anti-judder).
    ed.userScrollTo(150);
    sync.onEditorScroll();
    sync.reveal(18, "editor");
    expect(fm.reveals).toEqual([]); // suppressed — the couple owns the passive scroll this frame

    // The scroll stops; past the guard window a discrete caret move reveals normally.
    clock.advance(REVEAL_GUARD_MS);
    sync.reveal(18, "editor");
    expect(fm.reveals).toEqual([18]);
  });

  it("mode switch then user scroll: the restore's recorded write does not block a later genuine scroll", () => {
    const { ed, fm, sync } = make();
    // Switch into Split restoring line 40 on both panes.
    sync.restore(40, ["editor", "formatted"]);
    expect(ed.scroll).toBe(400);
    expect(fm.scroll).toBe(400);

    // The author now genuinely scrolls the editor — it must drive the formatted pane, not be mistaken for
    // the restore's own echo.
    ed.userScrollTo(700);
    sync.onEditorScroll();
    expect(fm.scroll).toBe(700);
  });
});
