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
  // The highest scrollTop this pane can reach (document height − viewport). A write past it clamps, the
  // way a real scroll container does — the coordinator then records the clamped read-back (boundary tests).
  maxScroll = Number.POSITIVE_INFINITY;
  // When set, topLine() reports THIS instead of the map's own lineForPx(scroll) — used to prove that
  // coupling reads the line through the pane's MAP, not through this separate viewport-top read (the two
  // are NOT mutually inverse on real panes, which is the drift the reversible-map contract removes).
  reportedTopLine: number | null = null;
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

  private clamp(px: number): number {
    return Math.max(0, Math.min(px, this.maxScroll));
  }

  /** Simulate a genuine user scroll (moves scrollTop off any coordinator-written value), clamped to range. */
  userScrollTo(px: number): void {
    this.scroll = this.clamp(px);
  }

  topLine(): number {
    return this.reportedTopLine ?? this.map.lineForPx(this.scroll);
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
    this.scroll = this.clamp(px);
  }

  reveal(line: number): void {
    this.reveals.push(line);
  }

  scrollToLine(line: number): void {
    this.scrolledToLine.push(line);
    this.scroll = this.clamp(this.map.pxForLine(line));
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

describe("SplitSync active/passive ownership (the coordinator owns which pane leads)", () => {
  it("makes the last non-echo scroll the active pane", () => {
    const { ed, fm, sync } = make();
    expect(sync.activePane()).toBe("editor"); // default before any interaction

    fm.userScrollTo(300);
    sync.onFormattedScroll();
    expect(sync.activePane()).toBe("formatted");

    ed.userScrollTo(200);
    sync.onEditorScroll();
    expect(sync.activePane()).toBe("editor");
  });

  it("does not let a coordinator echo re-declare active", () => {
    const { ed, sync } = make();
    ed.userScrollTo(200);
    sync.onEditorScroll(); // editor active, formatted coupled (echo pending)
    expect(sync.activePane()).toBe("editor");

    sync.onFormattedScroll(); // the coupling echo — must NOT flip active to formatted
    expect(sync.activePane()).toBe("editor");
  });

  it("makes a focused pane active and best-effort syncs the passive from it", () => {
    const { ed, fm, sync } = make();
    fm.userScrollTo(300); // formatted sits at line 30
    sync.onFocus("formatted");
    expect(sync.activePane()).toBe("formatted");
    expect(ed.scroll).toBe(300); // editor (now passive) synced from the focused pane's line
  });

  it("makes the edited pane active on a mirror re-align (syncFrom)", () => {
    const { sync } = make();
    sync.syncFrom("formatted");
    expect(sync.activePane()).toBe("formatted");
    sync.syncFrom("editor");
    expect(sync.activePane()).toBe("editor");
  });
});

// Asymmetric pane geometry (T-101): the two panes disagree pixel-for-pixel — tighter block margins in
// Formatted, a WRAPPED source line inflating one Code gap, and a TALL block inserted between two table
// rows in Formatted. Both axes still ascend, so each map is invertible.
const codeGeom: ScrollAnchor[] = [
  { line: 0, px: 0 },
  { line: 5, px: 100 }, // small gap
  { line: 6, px: 250 }, // a wrapped source line — 150px for one line in Code
  { line: 12, px: 400 },
];
const fmtGeom: ScrollAnchor[] = [
  { line: 0, px: 0 },
  { line: 5, px: 60 }, // tighter block margins in Formatted
  { line: 6, px: 460 }, // a tall block inserted between two table rows — 400px in Formatted
  { line: 12, px: 520 },
];

describe("SplitSync reversible map — intercepting active never changes the result", () => {
  it("round-trips a viewport position identically through both directions (no jump on interception)", () => {
    const { ed, fm, sync } = make(codeGeom, fmtGeom);

    // Code leads: viewport at editor px 175 = line 5.5 → formatted px 260.
    ed.userScrollTo(175);
    sync.syncFrom("editor");
    expect(fm.scroll).toBe(260);

    // The author now intercepts the formatted pane (it becomes the source). Coupling BACK lands the editor
    // exactly where it already was — the maps are exact inverses of one geometry, so no jump.
    sync.syncFrom("formatted");
    expect(ed.scroll).toBe(175);

    // Intercept again the other way: still the identity.
    sync.syncFrom("editor");
    expect(fm.scroll).toBe(260);
  });

  it("is a consistent bijection Code↔Formatted at the same vertical point", () => {
    const { ed, fm, sync } = make(codeGeom, fmtGeom);

    ed.userScrollTo(175);
    sync.onEditorScroll();
    const coupledFormatted = fm.scroll; // 260

    // Reset and drive from the other side at the coupled point — the inverse lands the editor back at 175.
    const two = make(codeGeom, fmtGeom);
    two.fm.userScrollTo(coupledFormatted);
    two.sync.onFormattedScroll();
    expect(two.ed.scroll).toBe(175);
  });

  it("couples through the panes' MAPS, not each pane's own viewport-top read", () => {
    const { ed, fm, sync } = make(codeGeom, fmtGeom);
    // The old coordinator read the source pane's own topLine() (CodeMirror's real per-line height map),
    // which is NOT the exact inverse of the sparse-anchor map it wrote through — the drift this task
    // removes. Poison topLine() with a wildly wrong value: the couple must ignore it and read the line
    // from the MAP (edMap.lineForPx(175) = 5.5 → formatted px 260), not from topLine (which would clamp
    // to the last anchor's pixel).
    ed.reportedTopLine = 99;
    ed.userScrollTo(175);
    sync.onEditorScroll();
    expect(fm.scroll).toBe(260); // the map's value, proving topLine was not consulted
  });
});

describe("SplitSync.reconciled (height-sync re-align from the active pane)", () => {
  it("re-aligns the passive pane from the active pane and absorbs the editor's compensation nudge", () => {
    const { ed, fm, sync } = make();
    ed.userScrollTo(200);
    sync.onEditorScroll(); // editor active, formatted coupled to 200

    // Height-sync reconciles: it nudged the editor's scrollTop (spacer-weight compensation keeping the
    // content in place) and changed geometry. reconciled() re-couples the passive from the active editor.
    ed.scroll = 220; // the compensation nudge
    sync.reconciled();
    expect(fm.scroll).toBe(220); // formatted re-aligned to the editor's compensated line

    // The editor's own (nudge-induced) scroll event is now a suppressed echo — it does not drive back.
    sync.onEditorScroll();
    expect(fm.scroll).toBe(220);
  });

  it("re-aligns the editor (passive) from the formatted pane when formatted is active", () => {
    const { ed, fm, sync } = make();
    fm.userScrollTo(300);
    sync.onFormattedScroll(); // formatted active, editor coupled to 300

    ed.scroll = 315; // a stray editor nudge from the reconcile
    sync.reconciled();
    expect(ed.scroll).toBe(300); // editor (passive) re-aligned to the active formatted pane, nudge overridden
  });
});

describe("SplitSync.readingLine (mode switch reads from the pane that owns the reading position)", () => {
  it("reads the ACTIVE pane in Split, not unconditionally the source editor", () => {
    const { ed, fm, sync } = make();
    fm.userScrollTo(250);
    sync.onFormattedScroll(); // formatted becomes active
    ed.userScrollTo(120); // editor sits at a DIFFERENT line — must be ignored in Split

    expect(sync.readingLine(true, true)).toBe(25); // the active formatted pane's line (px 250 → line 25)
    expect(sync.activePane()).toBe("formatted");
  });

  it("reads the sole visible pane in a single-pane mode and re-seats active there", () => {
    const { ed, fm, sync } = make();
    fm.userScrollTo(250);
    sync.onFormattedScroll(); // active = formatted

    ed.userScrollTo(120);
    expect(sync.readingLine(true, false)).toBe(12); // Code-only: read the editor (line 12), regardless of active
    expect(sync.activePane()).toBe("editor");

    fm.userScrollTo(450);
    expect(sync.readingLine(false, true)).toBe(45); // Formatted-only: read the formatted pane (line 45)
    expect(sync.activePane()).toBe("formatted");
  });
});

describe("SplitSync.settle (symmetric final-position re-sync)", () => {
  it("re-syncs the passive pane to the active pane's settled final position — both directions", () => {
    // Editor momentum: the last rAF frame trailed; settle catches the true final position.
    const a = make();
    a.ed.userScrollTo(500);
    a.sync.onEditorScroll();
    a.ed.userScrollTo(560); // momentum overshoots past the last coupled frame
    a.sync.settle("editor");
    expect(a.fm.scroll).toBe(560);

    // Formatted momentum: the same path, the other way.
    const b = make();
    b.fm.userScrollTo(500);
    b.sync.onFormattedScroll();
    b.fm.userScrollTo(560);
    b.sync.settle("formatted");
    expect(b.ed.scroll).toBe(560);
  });

  it("suppresses an echo settle (the passive pane settling on our write does not drive back)", () => {
    const { ed, sync } = make();
    ed.userScrollTo(400);
    sync.onEditorScroll(); // fm coupled to 400 (its settle would be an echo)
    sync.settle("formatted");
    expect(ed.scroll).toBe(400); // the passive pane's settle did not drive the active editor back
  });
});

describe("SplitSync deterministic echo edge cases (no timing window)", () => {
  it("suppresses the echo after several writes before the pane's scroll event is delivered", () => {
    const { ed, sync } = make();
    ed.userScrollTo(200);
    sync.onEditorScroll(); // writes formatted → 200
    ed.userScrollTo(300);
    sync.onEditorScroll(); // writes formatted → 300, before formatted's first scroll event was delivered

    // Formatted's (coalesced) scroll event finally fires: it is at 300, the LAST written value → echo.
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(300); // no drive-back; the editor stays where the author left it
  });

  it("drives the sibling on a genuine user scroll immediately after an echo", () => {
    const { ed, fm, sync } = make();
    ed.userScrollTo(200);
    sync.onEditorScroll(); // fm → 200 (echo pending)
    sync.onFormattedScroll(); // echo, suppressed
    expect(ed.scroll).toBe(200);

    fm.userScrollTo(350); // genuine formatted scroll right after the echo
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(350);
    expect(sync.activePane()).toBe("formatted");
  });

  it("keeps active correct under alternating focus with no mutual echo (identity, no jump)", () => {
    const { ed, fm, sync } = make();
    ed.userScrollTo(200);
    sync.onFocus("editor");
    expect(sync.activePane()).toBe("editor");
    expect(fm.scroll).toBe(200);

    sync.onFocus("formatted"); // couples the editor back — identity, so it does not move
    expect(sync.activePane()).toBe("formatted");
    expect(ed.scroll).toBe(200);

    // Neither focus-couple's echo drives the other pane.
    sync.onEditorScroll();
    expect(fm.scroll).toBe(200);
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(200);
  });

  it("suppresses a stale sibling scroll event arriving before the genuine one in the same frame", () => {
    const { ed, fm, sync } = make();
    ed.userScrollTo(300);
    sync.onEditorScroll(); // fm coupled to 300, lastWritten[formatted] = 300

    // New frame: the editor genuinely moves, and BOTH panes report. The formatted event (fm still at its
    // last-written 300) arrives first — it must be read as an echo, not a genuine formatted scroll.
    ed.userScrollTo(400);
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(400); // the stale formatted event did not drive the editor back
    expect(sync.activePane()).toBe("editor");

    sync.onEditorScroll(); // the genuine editor frame drives formatted
    expect(fm.scroll).toBe(400);
  });

  it("writes one stable best-effort position at a document boundary — no ping-pong, active untouched", () => {
    const { ed, fm, sync } = make();
    fm.maxScroll = 500; // the formatted document is short — its scroll clamps at 500

    ed.userScrollTo(700); // editor at line 70; the mapped formatted target (700) is past the clamp
    sync.onEditorScroll();
    expect(fm.scroll).toBe(500); // one stable best-effort write at the clamp

    // The clamped write is recorded as the echo (read-back), so the formatted pane's own event is suppressed
    // and the active editor is never driven back.
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(700);

    // Scrolling the editor further keeps the passive pane pinned at the clamp — stable, no oscillation.
    ed.userScrollTo(760);
    sync.onEditorScroll();
    expect(fm.scroll).toBe(500);
    sync.onFormattedScroll();
    expect(ed.scroll).toBe(760);
  });
});
