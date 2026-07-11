import { describe, expect, it } from "vitest";
import type { MarkdownEditor } from "../../src/editors/editor.js";
import type { BlockGeometry } from "../../src/review/preview.js";
import {
  type AnchorMetrics,
  computeGapAdjustments,
  computeScrollCompensation,
  containerGroups,
  type EditorSpacer,
  type GeometrySource,
  HeightSync,
} from "../../src/sync/height-sync.js";

function anchor(lineEnd: number, editorTop: number, previewTop: number): AnchorMetrics {
  return { lineEnd, editorTop, previewTop };
}

describe("computeGapAdjustments", () => {
  it("pads the editor when the rendered block is taller", () => {
    const result = computeGapAdjustments([anchor(2, 0, 0), anchor(5, 20, 120)]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([{ lineEnd: 2, height: 100 }]);
  });

  it("does NOT move the preview when the source is taller (editor-only padding)", () => {
    const result = computeGapAdjustments([anchor(2, 0, 0), anchor(5, 120, 20)]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([]);
  });

  it("adds nothing when the tops already line up", () => {
    const result = computeGapAdjustments([anchor(2, 0, 0), anchor(5, 50, 50)]);
    expect(result).toEqual({ editorLead: 0, editorSpacers: [] });
  });

  it("pads the editor lead when the first preview block starts lower", () => {
    // editor first line at 4px, preview first block at 21px → 17px lead in the editor.
    const result = computeGapAdjustments([anchor(0, 4, 21), anchor(3, 24, 61)]);
    expect(result.editorLead).toBe(17);
    // remaining: cumulative target = previewTop(37 needed) clamped monotonic → spacer of 20 below block 0
    expect(result.editorSpacers).toEqual([{ lineEnd: 0, height: 20 }]);
  });

  it("never pads the preview lead when the first editor line starts lower", () => {
    const result = computeGapAdjustments([anchor(0, 30, 5), anchor(3, 50, 25)]);
    expect(result).toEqual({ editorLead: 0, editorSpacers: [] });
  });

  it("returns empty for no anchors", () => {
    expect(computeGapAdjustments([])).toEqual({ editorLead: 0, editorSpacers: [] });
  });

  it("keeps the cumulative pad monotonic across a taller-then-shorter run", () => {
    const result = computeGapAdjustments([
      anchor(1, 0, 0), // lead 0
      anchor(3, 10, 60), // needs +50 → spacer 50 below block 0
      anchor(6, 90, 80), // editor now taller (90 > 80) → no extra pad (preview fixed)
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([{ lineEnd: 1, height: 50 }]);
  });

  // T-061 first-block lead. The reference pane's scroll origin sits its structural `padding-top` above
  // its content box, and the scroll coupling ALREADY consumes that inset (it scrolls the pane past its
  // padding to bring a line flush). So the plan measures alignment against the CONTENT box: the
  // `referenceInset` (the first rendered block's own top — it hugs the content box, its margin reset in
  // styles.css §5) is subtracted from every previewTop, so the lead does NOT reproduce it. Reproducing it
  // too would leave the first source line a `padding-top` below the flush rendered block — the ~24px
  // top-of-document misalignment this fixes.
  describe("first-block lead (T-061)", () => {
    it("does not reproduce the reference pane's structural inset as a lead (the double-count)", () => {
      // First rendered block at the pane's padding-top (20); the source editor's first line at the code
      // pane's own content padding (4). Told 20 is the reference inset, the plan applies NO lead.
      const result = computeGapAdjustments([anchor(0, 4, 20), anchor(3, 24, 60)], 20);
      expect(result.editorLead).toBe(0);
      // The inter-block spacer is still a difference against that content-box baseline: (60−20) − 24 = 16.
      expect(result.editorSpacers).toEqual([{ lineEnd: 0, height: 16 }]);
    });

    it("reproduced the inset before the fix — with no reference inset the double-count returns", () => {
      // The SAME geometry with no inset subtracted: the old plan reproduced the pane padding as a lead
      // (16 = previewTop 20 − editorTop 4), the bug the reference inset removes.
      const result = computeGapAdjustments([anchor(0, 4, 20), anchor(3, 24, 60)]);
      expect(result.editorLead).toBe(16);
    });

    it("still leads for genuine leading content above the first block (no inset to subtract)", () => {
      // With referenceInset 0 the first rendered block genuinely sits 20px below the first source line, so
      // a lead of 20 IS correct — the general behaviour computeGapAdjustments keeps for a non-pane caller.
      const result = computeGapAdjustments([anchor(0, 0, 20), anchor(3, 40, 60)]);
      expect(result.editorLead).toBe(20);
    });

    it("is 0 when the first source line and first rendered block already coincide", () => {
      const result = computeGapAdjustments([anchor(0, 0, 0), anchor(3, 40, 40)]);
      expect(result.editorLead).toBe(0);
    });

    it("never emits a negative lead when the first source line starts below the rendered block", () => {
      const result = computeGapAdjustments([anchor(0, 30, 5), anchor(3, 50, 25)], 5);
      expect(result.editorLead).toBe(0);
    });

    it("subtracting the inset leaves inter-block spacers (differences) intact — only the lead drops", () => {
      // Subtracting one constant (the 20px reference inset) from every previewTop shifts the plan's
      // baseline down uniformly: the lead falls from 20 to 0 while the inter-block spacers — increments of
      // the running maximum, i.e. differences — are byte-for-byte identical to the no-inset run.
      const withInset = computeGapAdjustments(
        [anchor(0, 0, 20), anchor(2, 20, 76), anchor(5, 40, 106)],
        20,
      );
      const baseline = computeGapAdjustments([
        anchor(0, 0, 20),
        anchor(2, 20, 76),
        anchor(5, 40, 106),
      ]);
      expect(baseline.editorLead).toBe(20);
      expect(withInset.editorLead).toBe(0);
      expect(withInset.editorSpacers).toEqual(baseline.editorSpacers);
    });
  });
});

// T-102: the padding above each anchor is the RUNNING MAXIMUM of its required shift, not the sum of
// every local positive gap difference. These pin the invariant and reproduce the over-padding the old
// per-gap algorithm produced — each test notes the (wrong) spacer the per-gap scheme would have emitted.
describe("computeGapAdjustments running-maximum cumulative plan (T-102)", () => {
  it("Code runs ahead then Formatted catches up: realigns with NO new spacer", () => {
    // required = [0, −40, 0]: at anchor 1 the editor is intrinsically taller (its natural top 100 sits
    // below the preview's 60 — unreachable, no negative spacer); at anchor 2 the preview catches back up
    // and the two coincide naturally (120 == 120). The running maximum never rose above 0, so nothing is
    // padded and anchor 2 lines up. The old per-gap scheme saw the +40 positive gap difference from
    // anchor 1→2 and emitted a spurious { lineEnd: 3, height: 40 }, locking the transient lead in as drift.
    const result = computeGapAdjustments([
      anchor(0, 0, 0),
      anchor(3, 100, 60),
      anchor(6, 120, 120),
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([]);
  });

  it("does not re-pad when Formatted catches up while still under the accumulated maximum", () => {
    // required = [0, +50, +10, +40]: anchor 1 needs 50 (padded); anchors 2 and 3 need less than the 50
    // already applied, so no further padding — the running maximum stays 50. Per-gap would ALSO add a
    // { lineEnd: 5, height: 30 } for the anchor 2→3 positive difference (10→40), over-padding to 80 total.
    const result = computeGapAdjustments([
      anchor(1, 0, 0),
      anchor(3, 100, 150),
      anchor(5, 200, 210),
      anchor(7, 300, 340),
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([{ lineEnd: 1, height: 50 }]);
  });

  it("emits one spacer per increase of the running maximum across several alternating perturbations", () => {
    // required = [0, +40, +10, +40, +70]: the maximum rises 0→40 (spacer at slot 0), stays 40 through the
    // two dips (no spacer), then rises 40→70 (spacer at slot 3). Two spacers, totalling exactly 70 — the
    // final anchor's need — not the sum of all positive jumps (40+30+30 = 100 the per-gap scheme adds).
    const result = computeGapAdjustments([
      anchor(1, 0, 0),
      anchor(2, 100, 140),
      anchor(3, 200, 210),
      anchor(4, 300, 340),
      anchor(5, 400, 470),
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([
      { lineEnd: 1, height: 40 },
      { lineEnd: 4, height: 30 },
    ]);
  });

  it("keeps unreachable alignment monotonic and never negative (Code below target, height can't be removed)", () => {
    // required = [0, −30, −30, +10]: the editor sits 30px below its target at anchors 1 and 2 (unreachable
    // — no negative spacer), then the preview pulls 10px ahead at anchor 3. Only the reachable +10 is
    // padded, at its placement slot; nothing negative is ever emitted and the applied padding is monotonic.
    const result = computeGapAdjustments([
      anchor(1, 0, 0),
      anchor(2, 50, 20),
      anchor(3, 100, 70),
      anchor(4, 150, 160),
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([{ lineEnd: 3, height: 10 }]);
    expect(result.editorSpacers.every((s) => s.height > 0)).toBe(true);
  });

  it("rounds the cumulative total, not each gap — subpixel steps do not lose padding to rounding", () => {
    // required grows by a sub-pixel 0.4 per anchor to a fractional 2.0 total. Rounding each gap
    // independently (the per-gap scheme) rounds every 0.4 to 0 and emits NOTHING, losing the whole 2px.
    // Rounding only the cumulative boundaries keeps the total: round(applied[k]) crosses an integer twice,
    // so two 1px spacers are emitted, summing to round(2.0) = 2 — no accumulated rounding error.
    const result = computeGapAdjustments([
      anchor(1, 0, 0),
      anchor(2, 10, 10.4),
      anchor(3, 20, 20.8),
      anchor(4, 30, 31.2),
      anchor(5, 40, 41.6),
      anchor(6, 50, 52.0),
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([
      { lineEnd: 2, height: 1 },
      { lineEnd: 4, height: 1 },
    ]);
    const total = result.editorSpacers.reduce((sum, s) => sum + s.height, 0);
    expect(total).toBe(2);
  });

  it("mixed wrapped-code → table rows → list items: no spurious spacer where the preview only catches up", () => {
    // A realistic Split geometry: a heading, a fenced code block that wraps TALLER in the editor than it
    // renders (so the following anchors' natural editor tops fall below the preview — required goes
    // negative), two table rows the preview then catches up on, and two list items — the last of which
    // genuinely needs padding. required = [0, 0, −50, −50, 0, +30]. The running maximum stays 0 until the
    // final +30, so exactly one spacer is emitted, at the list-item placement slot. The old per-gap scheme
    // would ALSO plant a { lineEnd: 9, height: 50 } at the table→list boundary (the +50 positive gap
    // difference as the preview catches up), the exact over-padding this fix removes.
    const result = computeGapAdjustments([
      anchor(2, 0, 0), // heading
      anchor(6, 40, 40), // fenced code (top of block still aligned)
      anchor(8, 200, 150), // table row 1 — editor pushed 50px past the preview by the tall wrapped code
      anchor(9, 230, 180), // table row 2 — still 50px past
      anchor(11, 260, 260), // list item 1 — preview has caught back up, natural coincidence
      anchor(13, 290, 320), // list item 2 — preview now genuinely 30px ahead
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([{ lineEnd: 11, height: 30 }]);
  });
});

// T-112: the container-tail floor. Padding accumulated ABOVE a table/list counts toward its rows'
// required, so a row whose own required never reaches the running maximum gets no spacer — and the
// container's LAST row then drifts against its rendered counterpart inside one viewport (the T-110/T-111
// finding). The author-accepted contract: intermediate rows may drift (additive padding cannot lift
// them), but each container's last row is floored to the container's own internal growth, so the
// container ends in step in both panes.
describe("computeGapAdjustments container-tail floor (T-112)", () => {
  it("floors a globally unreachable container tail to the container's internal growth", () => {
    // required = [0, +100, +80, +70, +90]: earlier content pads 100; the table's rows (anchors 2..4)
    // all stay under it — the plain running maximum would leave the whole table spacer-less, its last
    // row drifting +10 against the header inside one viewport. The floor pins the tail to the
    // container's internal growth: applied[first]=100 plus (90 − 80) = 110 → one 10px spacer at the
    // last row's placement slot. Intermediate rows stay untouched (accepted drift).
    const result = computeGapAdjustments(
      [
        anchor(0, 0, 0),
        anchor(1, 10, 110), // tall rendered block above — running max 100
        anchor(4, 40, 120), // header row — required 80, unreachable (group first)
        anchor(5, 60, 130), // body row — required 70, unreachable
        anchor(6, 80, 170), // LAST row — required 90, unreachable globally; floored to 110
      ],
      0,
      [{ first: 2, last: 4 }],
    );
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([
      { lineEnd: 0, height: 100 },
      { lineEnd: 5, height: 10 },
    ]);
  });

  it("keeps a globally reachable tail EXACTLY aligned — the floor never over-pads it", () => {
    // required = [0, −6, −14, +13]: the tail's own required exceeds the running maximum (0), so plain
    // global alignment applies (13px, exact). The floor (applied[first] 0 + (13 − (−6)) = 19) must NOT
    // replace it — the grouped result is byte-identical to the ungrouped one.
    const anchors = [anchor(0, 0, 0), anchor(2, 10, 4), anchor(3, 30, 16), anchor(4, 50, 63)];
    const grouped = computeGapAdjustments(anchors, 0, [{ first: 1, last: 3 }]);
    const ungrouped = computeGapAdjustments(anchors);
    expect(grouped).toEqual(ungrouped);
    expect(grouped.editorSpacers).toEqual([{ lineEnd: 3, height: 13 }]);
  });

  it("adds nothing when the container internally shrinks (the source-taller direction stays out of scope)", () => {
    // required = [0, +100, +90, +70]: inside the container the source outgrows the reference (a loose
    // list) — the floor (100 + (70 − 90) = 80) sinks below the running maximum and correctly adds no
    // spacer; additive padding cannot lift the tail and must not try.
    const result = computeGapAdjustments(
      [
        anchor(0, 0, 0),
        anchor(1, 10, 110),
        anchor(4, 40, 130), // first item — required 90
        anchor(6, 80, 150), // LAST item — required 70, internal growth negative
      ],
      0,
      [{ first: 2, last: 3 }],
    );
    expect(result.editorSpacers).toEqual([{ lineEnd: 0, height: 100 }]);
  });

  it("takes the highest floor when nested containers close on the same anchor", () => {
    // An outer list whose last item holds a sub-list: both groups end on the final anchor. The outer
    // floor (200 + (175 − 180) = 195) loses to the inner one (200 + (175 − 160) = 215) — the sub-list's
    // own growth is the binding constraint. One 15px spacer at the tail's placement slot.
    const result = computeGapAdjustments(
      [
        anchor(0, 0, 0),
        anchor(1, 10, 210), // running max 200
        anchor(4, 40, 220), // outer item — required 180 (outer group first)
        anchor(5, 60, 220), // sub-item — required 160 (inner group first)
        anchor(6, 80, 255), // LAST sub-item — required 175, closes both groups
      ],
      0,
      [
        { first: 2, last: 4 },
        { first: 3, last: 4 },
      ],
    );
    expect(result.editorSpacers).toEqual([
      { lineEnd: 0, height: 200 },
      { lineEnd: 5, height: 15 },
    ]);
  });

  it("phases the floor in over a ramp at the reachability boundary — no plan discontinuity", () => {
    // A tail a hair below the running maximum must NOT jump straight to the full parity floor (the two
    // regimes differ by the first row's whole residual, so a hard gate would swing the tail's padding by
    // that residual on subpixel measurement noise). required = [0, +100, +40, +99]: the tail's shortfall
    // is 1px while the full floor is 100 + (99 − 40) = 159; the ramp caps the floor at
    // runningMax + 3×shortfall = 103, so the plan moves 3px past the plain maximum, not 59.
    const nearBoundary = computeGapAdjustments(
      [anchor(0, 0, 0), anchor(1, 10, 110), anchor(4, 40, 80), anchor(6, 80, 179)],
      0,
      [{ first: 2, last: 3 }],
    );
    expect(nearBoundary.editorSpacers).toEqual([
      { lineEnd: 0, height: 100 },
      { lineEnd: 4, height: 3 },
    ]);
    // A clearly unreachable tail (shortfall beyond the ramp zone) still gets the FULL parity floor —
    // the ramp only smooths the boundary, it does not dilute the reported-scenario fix.
    const clearlyUnreachable = computeGapAdjustments(
      [anchor(0, 0, 0), anchor(1, 10, 110), anchor(4, 40, 120), anchor(6, 80, 170)],
      0,
      [{ first: 2, last: 3 }],
    );
    expect(clearlyUnreachable.editorSpacers).toEqual([
      { lineEnd: 0, height: 100 },
      { lineEnd: 4, height: 10 },
    ]);
  });

  it("is a fixed point THROUGH HeightSync — a second reconcile of settled geometry does not re-dispatch", () => {
    // The floor must stay a function of spacer-INVARIANT inputs (natural tops, measured reference
    // geometry): HeightSync.apply skips identical sets, so a settled Split must recompute the identical
    // floored plan on the next reconcile — this drives the REAL reconcile twice rather than calling the
    // pure function twice (which would hold for any deterministic function).
    const editor = new FakeEditor();
    // required = [0, +100, +80, +90]: a tall top-level block sets the maximum at 100; the two-row
    // container (anchors 2..3) sits under it, its tail floored to 100 + (90 − 80) = 110.
    const geometry: BlockGeometry[] = [
      { lineStart: 0, lineEnd: 0, top: 0, height: 110 },
      { lineStart: 1, lineEnd: 3, top: 110, height: 10 },
      { lineStart: 4, lineEnd: 4, top: 120, height: 50, containers: ["t:4-6#0"] },
      { lineStart: 5, lineEnd: 6, top: 170, height: 30, containers: ["t:4-6#0"] },
    ];
    const sync = new HeightSync(editor as unknown as MarkdownEditor, fakeSource(geometry));
    editor.setTops([
      [0, 0],
      [1, 10],
      [4, 40],
      [5, 80],
    ]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(1);
    // The floor genuinely fired: the tail spacer sits at the container's final placement slot.
    expect(editor.calls[0]?.spacers).toEqual([
      { lineEnd: 0, height: 100 },
      { lineEnd: 4, height: 10 },
    ]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(1);
  });

  it("ignores degenerate or out-of-range groups", () => {
    const anchors = [anchor(0, 0, 0), anchor(1, 10, 110), anchor(2, 40, 120)];
    const result = computeGapAdjustments(anchors, 0, [
      { first: 2, last: 2 }, // single-anchor group
      { first: 1, last: 5 }, // out of range
      { first: -1, last: 2 }, // out of range
    ]);
    expect(result).toEqual(computeGapAdjustments(anchors));
  });
});

describe("containerGroups", () => {
  const leaf = (
    lineStart: number,
    containers?: readonly string[],
  ): BlockGeometry & { containers?: readonly string[] } => ({
    lineStart,
    lineEnd: lineStart,
    top: lineStart * 10,
    height: 10,
    ...(containers === undefined ? {} : { containers }),
  });

  it("recovers each container's first/last anchor run from the stamped keys", () => {
    const groups = containerGroups([
      leaf(0),
      leaf(2, ["t:2-5"]),
      leaf(3, ["t:2-5"]),
      leaf(4, ["t:2-5"]),
      leaf(6),
      leaf(8, ["l:8-10"]),
      leaf(9, ["l:8-10"]),
    ]);
    expect(groups).toEqual([
      { first: 1, last: 3 },
      { first: 5, last: 6 },
    ]);
  });

  it("keeps nested containers as separate overlapping groups", () => {
    const groups = containerGroups([
      leaf(0, ["outer"]),
      leaf(1, ["outer", "inner"]),
      leaf(2, ["outer", "inner"]),
      leaf(3, ["outer"]),
    ]);
    expect(groups).toEqual([
      { first: 0, last: 3 },
      { first: 1, last: 2 },
    ]);
  });

  it("drops single-anchor groups and geometry without keys", () => {
    expect(containerGroups([leaf(0), leaf(1, ["solo"]), leaf(2)])).toEqual([]);
    expect(containerGroups([leaf(0), leaf(1)])).toEqual([]);
  });
});

describe("computeScrollCompensation (T-066)", () => {
  const spacer = (lineEnd: number, height: number): EditorSpacer => ({ lineEnd, height });

  it("is zero when nothing above the viewport changed (only spacers below it changed)", () => {
    // Block spacer at line 5 (below the viewport top at line 2) grows from 100 to 160 — no compensation.
    const delta = computeScrollCompensation(
      2,
      { lead: 0, spacers: [spacer(5, 100)] },
      { lead: 0, spacers: [spacer(5, 160)] },
    );
    expect(delta).toBe(0);
  });

  it("is positive (scroll further down) when a spacer above the viewport grew", () => {
    // Spacer at line 1 sits above the viewport top at line 3 in both sets; it grew by 60.
    const delta = computeScrollCompensation(
      3,
      { lead: 0, spacers: [spacer(1, 100)] },
      { lead: 0, spacers: [spacer(1, 160)] },
    );
    expect(delta).toBe(60);
  });

  it("is negative (scroll back up) when a spacer above the viewport shrank", () => {
    const delta = computeScrollCompensation(
      3,
      { lead: 0, spacers: [spacer(1, 160)] },
      { lead: 0, spacers: [spacer(1, 100)] },
    );
    expect(delta).toBe(-60);
  });

  it("counts the lead once the viewport has scrolled past the very top of the document", () => {
    const delta = computeScrollCompensation(5, { lead: 0, spacers: [] }, { lead: 40, spacers: [] });
    expect(delta).toBe(40);
  });

  it("does NOT count the lead while the viewport is still exactly at the document top (line 0)", () => {
    // Matches the position-based convention `MarkdownEditor.spacerHeightAbove` already uses for anchor
    // 0: the leading spacer is folded into line 0's own block, so it never counts as "above" line 0.
    const delta = computeScrollCompensation(0, { lead: 0, spacers: [] }, { lead: 40, spacers: [] });
    expect(delta).toBe(0);
  });

  it("nets several spacer changes above the viewport into one delta", () => {
    const delta = computeScrollCompensation(
      10,
      { lead: 20, spacers: [spacer(1, 100), spacer(3, 50)] },
      { lead: 20, spacers: [spacer(1, 150), spacer(3, 30)] },
    );
    // Lead unchanged (0); line 1 spacer +50; line 3 spacer −20 → net +30.
    expect(delta).toBe(30);
  });

  it("ignores a spacer exactly at the viewport line — it sits below, not above, the viewport top", () => {
    const delta = computeScrollCompensation(
      3,
      { lead: 0, spacers: [spacer(3, 100)] },
      { lead: 0, spacers: [spacer(3, 160)] },
    );
    expect(delta).toBe(0);
  });

  it("compensates for a spacer between rows of one table (an intra-container slot) above the viewport", () => {
    // T-102: with per-row/per-item anchors, a spacer can land at a placement slot INSIDE a container —
    // between two rows of the same table (or two items of one list). When such a spacer above the viewport
    // grows, its weight change must still shift scrollTop so the active Code content stays put, exactly as
    // a spacer between whole blocks does. Row-1→row-2 spacer at slot line 5, viewport parked at line 12.
    const delta = computeScrollCompensation(
      12,
      { lead: 0, spacers: [spacer(5, 40)] },
      { lead: 0, spacers: [spacer(5, 90)] },
    );
    expect(delta).toBe(50);
  });
});

// A scriptable stand-in for the two collaborators HeightSync drives, so reconcile()'s dispatch
// behaviour can be tested without a live CodeMirror / DOM. `naturalLineTops` is keyed by the source
// line so a test can flip a block's editor top from an estimate to its measured value between
// reconciles (what CodeMirror does when it finishes measuring a below-viewport block).
class FakeEditor {
  readonly calls: { spacers: EditorSpacer[]; lead: number }[] = [];
  readonly scrollAdjustments: number[] = [];
  private tops = new Map<number, number>();
  // Defaults to line 0 — with no viewport scroll below the document top, computeScrollCompensation
  // never counts the lead and the existing (pre-T-066) tests below never trigger a compensation call.
  private viewportLine = 0;
  // Pane-consistency gate (T-084): both default to the "settled, in sync" state so the pre-existing
  // suites below (written before the gate existed) keep exercising reconcile() unhindered; the
  // dedicated gate suite further down flips these explicitly.
  private pendingChange = false;
  private text = "";
  // Lines naturalLineTops() should refuse (return null for) instead of resolving a top for — models an
  // anchor minted against a since-diverged sibling document (T-084).
  private staleLines = new Set<number>();

  setTops(pairs: Array<[line: number, top: number]>): void {
    this.tops = new Map(pairs);
  }

  setViewportLine(line: number): void {
    this.viewportLine = line;
  }

  setPendingChange(pending: boolean): void {
    this.pendingChange = pending;
  }

  setText(text: string): void {
    this.text = text;
  }

  setStaleLines(lines: number[]): void {
    this.staleLines = new Set(lines);
  }

  naturalLineTops(lines: number[]): (number | null)[] {
    return lines.map((lineStart) =>
      this.staleLines.has(lineStart) ? null : (this.tops.get(lineStart) ?? 0),
    );
  }

  topVisibleLine(): number {
    return this.viewportLine;
  }

  hasPendingChange(): boolean {
    return this.pendingChange;
  }

  getText(): string {
    return this.text;
  }

  adjustScrollTop(delta: number): void {
    this.scrollAdjustments.push(delta);
  }

  setSpacers(spacers: EditorSpacer[], leadingHeight = 0): void {
    this.calls.push({ spacers, lead: leadingHeight });
  }

  contentWidth(): number {
    return 800;
  }
}

/** A `GeometrySource` fake that also satisfies the pane-consistency gate's `hasPendingChange`/`getText`
 *  (T-084), defaulting to "settled, matching text" so the pre-existing suites (written before the gate
 *  existed) aren't gated by default. */
function fakeSource(
  geometry: BlockGeometry[],
  overrides?: { pendingChange?: boolean; text?: string },
): GeometrySource {
  return {
    blockGeometry: () => geometry,
    contentWidth: () => 800,
    hasPendingChange: () => overrides?.pendingChange ?? false,
    getText: () => overrides?.text ?? "",
  };
}

describe("HeightSync.reconcile (T-062: self-heal after re-measure, no flicker loop)", () => {
  function make(geometry: BlockGeometry[]): { editor: FakeEditor; sync: HeightSync } {
    const editor = new FakeEditor();
    const source = fakeSource(geometry);
    const sync = new HeightSync(editor as unknown as MarkdownEditor, source);
    return { editor, sync };
  }

  // The description's scenario: a `### A code block` after a table, below the viewport. The formatted
  // pane places it 200px down; the editor first only ESTIMATED that source region, reading its top as
  // 40 (a wrapped table row counted as one line), so the gap looks like 40 and the spacer inflates to
  // 160. When CodeMirror later measures the real height the top corrects to 90 and the spacer must
  // shrink to 110 on its own — the whole point of the fix (no edit, no resize triggered it).
  const geometry: BlockGeometry[] = [
    { lineStart: 0, lineEnd: 5, top: 0, height: 200 },
    { lineStart: 7, lineEnd: 7, top: 200, height: 40 },
  ];

  it("re-pads with the corrected (smaller) spacer once an estimated editor top is measured", () => {
    const { editor, sync } = make(geometry);

    editor.setTops([
      [0, 0],
      [7, 40],
    ]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 160 }]);

    // CodeMirror finished measuring → the below-viewport top corrects. The re-reconcile (now driven by
    // the transaction-less geometryChanged the update-listener no longer swallows) shrinks the spacer.
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(2);
    expect(editor.calls[1]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });

  it("does not re-dispatch while the geometry stays put — a fixed point, so no apply→measure→apply flicker", () => {
    const { editor, sync } = make(geometry);
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);

    sync.reconcile();
    sync.reconcile();
    sync.reconcile();

    // Only the first reconcile touches the editor; the settled ones recompute the identical spacer set
    // and skip the dispatch, so CodeMirror is never nudged into the loop the old blanket guard prevented.
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });

  // T-061: the reference pane's structural top inset (its first rendered block sits `padding-top` below
  // the scroll origin) must NOT be reproduced as an editor lead — the scroll coupling already consumes
  // it, so re-adding it double-counts the pane padding (the ~24px top-of-document misalignment).
  // reconcile() reads that inset as the first block's own top and subtracts it, so the lead settles at 0
  // while the genuine inter-block spacer is unaffected. `naturalLineTops` is invariant to the spacers
  // applied (CodeMirror folds a leading block widget into line 0's block — see editor.ts
  // spacerHeightsAbove), which the FakeEditor models by line-keyed tops that don't move when spacers are
  // applied, so repeated reconciles recompute the identical set and stop re-dispatching.
  it("does not reproduce the pane's top inset as a lead, and settles (T-061)", () => {
    const insetGeometry: BlockGeometry[] = [
      { lineStart: 0, lineEnd: 0, top: 20, height: 40 }, // first rendered block at the pane inset (20)
      { lineStart: 2, lineEnd: 2, top: 80, height: 40 },
    ];
    const editor = new FakeEditor();
    const source = fakeSource(insetGeometry);
    const sync = new HeightSync(editor as unknown as MarkdownEditor, source);
    editor.setTops([
      [0, 4], // code line 0 at the source editor's own content padding
      [2, 24],
    ]);

    sync.reconcile();
    sync.reconcile();
    sync.reconcile();

    // The pane inset (20) is absorbed as the content-box origin, so NO lead is applied; the mid-document
    // gap ((80−20) − 24 = 36) still gets its spacer. Dispatched once — settled repeats recompute the same set.
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.lead).toBe(0);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 0, height: 36 }]);
  });

  it("clear() drops the spacers and a later reconcile re-applies them (leaving/returning to Split)", () => {
    const { editor, sync } = make(geometry);
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);

    sync.reconcile();
    expect(editor.calls).toHaveLength(1);

    sync.clear();
    expect(editor.calls).toHaveLength(2);
    expect(editor.calls[1]).toEqual({ spacers: [], lead: 0 });

    sync.reconcile();
    expect(editor.calls).toHaveLength(3);
    expect(editor.calls[2]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });
});

describe("HeightSync viewport scroll compensation (T-066)", () => {
  // Block 0 spans lines 0-5 (above the viewport); block 1 spans line 7 (below it). The viewport is
  // parked at line 6, i.e. between the two blocks, so only block 0's spacer sits "above" it.
  const geometry: BlockGeometry[] = [
    { lineStart: 0, lineEnd: 5, top: 0, height: 200 },
    { lineStart: 7, lineEnd: 7, top: 200, height: 40 },
  ];

  function make(): { editor: FakeEditor; sync: HeightSync } {
    const editor = new FakeEditor();
    const source = fakeSource(geometry);
    const sync = new HeightSync(editor as unknown as MarkdownEditor, source);
    return { editor, sync };
  }

  it("compensates scrollTop for the initial spacer weight above the viewport, then again as it settles", () => {
    const { editor, sync } = make();
    editor.setViewportLine(6);

    // First reconcile (an underestimated below-viewport top, as in the T-062 suite above): a 160px
    // spacer appears above the (parked) viewport where none existed before — compensate the full 160px
    // immediately so the content already visible at the top does not jump down.
    editor.setTops([
      [0, 0],
      [7, 40],
    ]);
    sync.reconcile();
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 160 }]);
    expect(editor.scrollAdjustments).toEqual([160]);

    // CodeMirror finishes measuring: the spacer above the (still parked) viewport shrinks 160 → 110, a
    // visible upward jump unless scrollTop is pulled back by the same 50px in the same pass.
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);
    sync.reconcile();
    expect(editor.calls[1]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
    expect(editor.scrollAdjustments).toEqual([160, -50]);
  });

  it("does not compensate when the spacer that changed sits below the viewport", () => {
    const { editor, sync } = make();
    editor.setViewportLine(0); // parked at the very top, above both blocks

    editor.setTops([
      [0, 0],
      [7, 40],
    ]);
    sync.reconcile();
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);
    sync.reconcile();

    // The only spacer (below line 5, i.e. below the line-0 viewport) changed, but line 0 never counts
    // anything as "above" it — so no compensation, even though a dispatch happened both times.
    expect(editor.calls).toHaveLength(2);
    expect(editor.scrollAdjustments).toEqual([]);
  });

  it("adjusts scrollTop exactly once, alongside the one settling dispatch (no repeat on later reconciles)", () => {
    const { editor, sync } = make();
    editor.setViewportLine(6);
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);

    sync.reconcile();
    sync.reconcile();
    sync.reconcile();

    // Only the first reconcile dispatches (see the T-062 suite) — for the 110px spacer that appears
    // above the parked viewport. The settled repeats skip apply() entirely, so no further adjustment
    // is ever computed for them, let alone applied.
    expect(editor.calls).toHaveLength(1);
    expect(editor.scrollAdjustments).toEqual([110]);
  });
});

describe("HeightSync.reconcile pane-consistency gate (T-084)", () => {
  const geometry: BlockGeometry[] = [
    { lineStart: 0, lineEnd: 5, top: 0, height: 200 },
    { lineStart: 7, lineEnd: 7, top: 200, height: 40 },
  ];

  function make(overrides?: { pendingChange?: boolean; text?: string }): {
    editor: FakeEditor;
    sync: HeightSync;
  } {
    const editor = new FakeEditor();
    const source = fakeSource(geometry, overrides);
    const sync = new HeightSync(editor as unknown as MarkdownEditor, source);
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);
    return { editor, sync };
  }

  it("does not apply anchors while the EDITOR has a pending (unmirrored) edit", () => {
    const { editor, sync } = make();
    editor.setPendingChange(true);

    sync.reconcile();

    expect(editor.calls).toHaveLength(0);
  });

  it("does not apply anchors while the SOURCE (formatted) pane has a pending edit", () => {
    const { editor, sync } = make({ pendingChange: true });

    sync.reconcile();

    expect(editor.calls).toHaveLength(0);
  });

  it("does not apply anchors while the panes' texts disagree, even with neither pending", () => {
    const { editor, sync } = make({ text: "formatted pane's text" });
    editor.setText("editor's (different) text");

    sync.reconcile();

    expect(editor.calls).toHaveLength(0);
  });

  it("retries and applies correct spacers once the mirror settles (matching texts, no pending)", () => {
    // The scenario the description calls out: reconcile is driven (e.g. by onContentResize) inside the
    // 120ms debounce window, before the destination pane has accepted the mirrored edit — no spacers
    // must be built off the mismatched pair. Once the mirror lands (texts converge, pending clears —
    // what onEditorChange/onFormattedChange do before their own unconditional reconcileHeights() call),
    // the very next reconcile() must produce the correct spacers, with nothing left over from the gated
    // attempt.
    const { editor, sync } = make({ text: "same text" });
    editor.setText("different text (mid-mirror)");
    editor.setPendingChange(true);

    sync.reconcile();
    expect(editor.calls).toHaveLength(0);

    // The mirror settles: pending clears, texts converge.
    editor.setPendingChange(false);
    editor.setText("same text");

    sync.reconcile();
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });

  it("does not apply anchors when one is minted against an out-of-range (stale) source line", () => {
    const { editor, sync } = make();
    editor.setStaleLines([7]); // the formatted pane's second block outlives the editor's document

    sync.reconcile();

    expect(editor.calls).toHaveLength(0);
  });

  it("recovers once the stale line becomes valid again (the editor's document catches up)", () => {
    const { editor, sync } = make();
    editor.setStaleLines([7]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(0);

    editor.setStaleLines([]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });
});

// T-109: a SILENT setText (a doc load) never fires onEditorChange/onFormattedChange, so the T-084 gate's
// ordinary recovery path above (the pending pane's own debounce re-driving reconcileHeights()) never runs
// for it — these pin the second, one-shot recovery HeightSync itself requests via the injected
// onSettleRetry hook (index.ts wires this back through reconcileHeights()/the generation-aware scheduler).
describe("HeightSync.reconcile settle retry (T-109: recovery for a silent-setText load with no onChange)", () => {
  const geometry: BlockGeometry[] = [
    { lineStart: 0, lineEnd: 5, top: 0, height: 200 },
    { lineStart: 7, lineEnd: 7, top: 200, height: 40 },
  ];

  /** Wires onSettleRetry to `retryBehavior` (run once, right before the hook's own re-`reconcile()` call)
   *  plus a counter of how many times the hook fired — the harness a caller like index.ts provides, with
   *  `retryBehavior` standing in for "whatever the real world did between the gate and the next frame". */
  function make(
    overrides?: { pendingChange?: boolean; text?: string },
    retryBehavior?: (editor: FakeEditor) => void,
  ): { editor: FakeEditor; sync: HeightSync; retryCalls: { count: number } } {
    const editor = new FakeEditor();
    const source = fakeSource(geometry, overrides);
    const retryCalls = { count: 0 };
    let sync: HeightSync;
    sync = new HeightSync(editor as unknown as MarkdownEditor, source, undefined, () => {
      retryCalls.count += 1;
      retryBehavior?.(editor);
      sync.reconcile();
    });
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);
    return { editor, sync, retryCalls };
  }

  it("requests exactly one settle retry when gated by a pane text mismatch, and applies once it resolves", () => {
    // Models the doc-load scenario: a gated reconcile() (panes momentarily/apparently disagreeing) with
    // no onChange ever coming to retry it — HeightSync's own settle-retry hook is the only way back, and
    // by the time it runs the mismatch that caused the gate has resolved (matching the always-synchronous
    // reality of setText/getText — see index.ts normalizeLineEndings' doc comment).
    const { editor, sync, retryCalls } = make({ text: "same text" }, (fakeEditor) => {
      fakeEditor.setText("same text");
    });
    editor.setText("editor's (different) text — mid-settle");

    sync.reconcile();

    expect(retryCalls.count).toBe(1);
    // The retry's OWN reconcile() call — not a second manual call from the test — is what applied these.
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });

  it("requests at most one settle retry per gated streak — a still-mismatched retry does not request a second one", () => {
    // No retryBehavior: the mismatch never resolves — models a genuinely (not just transiently) diverged
    // pair, which must stay gated rather than poll forever.
    const { editor, sync, retryCalls } = make({ text: "same text" });
    editor.setText("editor's (different) text — never settles");

    sync.reconcile();

    expect(retryCalls.count).toBe(1);
    expect(editor.calls).toHaveLength(0);
  });

  it("a later, independent gate requests its own fresh settle retry (the one-shot guard is per streak)", () => {
    const { editor, sync, retryCalls } = make({ text: "same text" }, (fakeEditor) => {
      fakeEditor.setText("same text");
    });
    editor.setText("mismatch 1");
    sync.reconcile();
    expect(retryCalls.count).toBe(1);
    expect(editor.calls).toHaveLength(1);

    // A later, unrelated mismatch (e.g. a fresh edit mid-mirror) gates again — it must get its own retry,
    // proving the one-shot guard reset once the first streak actually settled.
    editor.setText("mismatch 2");
    sync.reconcile();
    expect(retryCalls.count).toBe(2);
    expect(editor.calls).toHaveLength(1); // unchanged geometry/tops — the settled repeat dispatches nothing new
  });

  it("also requests a settle retry when gated by a pending (unmirrored) edit", () => {
    const { editor, sync, retryCalls } = make(undefined, (fakeEditor) => {
      fakeEditor.setPendingChange(false);
    });
    editor.setPendingChange(true);

    sync.reconcile();

    expect(retryCalls.count).toBe(1);
    expect(editor.calls).toHaveLength(1);
  });
});

// T-104: reconcile() is the one read phase of a Split generation — it returns the immutable geometry
// snapshot the coordinator rebuilds BOTH scroll maps from, so no second measure runs after the spacer
// write. These pin the snapshot's shape (formatted natural tops + editor PADDED tops, both sharing one
// line axis with a trailing anchor), the `changed` report, and the gated `null`.
describe("HeightSync.reconcile snapshot return (T-104)", () => {
  const geometry: BlockGeometry[] = [
    { lineStart: 0, lineEnd: 5, top: 0, height: 200 },
    { lineStart: 7, lineEnd: 7, top: 200, height: 40 },
  ];

  function make(overrides?: { pendingChange?: boolean; text?: string }): {
    editor: FakeEditor;
    sync: HeightSync;
  } {
    const editor = new FakeEditor();
    const sync = new HeightSync(
      editor as unknown as MarkdownEditor,
      fakeSource(geometry, overrides),
    );
    return { editor, sync };
  }

  it("returns the formatted natural tops and the editor PADDED tops on one shared line axis", () => {
    const { editor, sync } = make();
    // The editor's spacer-free tops underestimate the second block (40 vs the preview's 200), so a 160px
    // spacer is planted below line 5. The editor anchor for line 7 must therefore be its natural 40 PLUS
    // that 160 = 200 (the padded top a re-read would report), not the bare 40.
    editor.setTops([
      [0, 0],
      [7, 40],
    ]);
    const snapshot = sync.reconcile();
    expect(snapshot).not.toBeNull();
    // Formatted: each block's natural top plus a trailing anchor at the last block's bottom (200 + 40).
    expect(snapshot?.formatted).toEqual([
      { line: 0, px: 0 },
      { line: 7, px: 200 },
      { line: 8, px: 240 },
    ]);
    // Editor: block 0 at its natural 0, block 7 padded to 200 (40 + the 160 spacer above it), trailing
    // extended by the last block's rendered height (200 + 40) — both maps' final segment stays monotonic.
    expect(snapshot?.editor).toEqual([
      { line: 0, px: 0 },
      { line: 7, px: 200 },
      { line: 8, px: 240 },
    ]);
    expect(snapshot?.changed).toBe(true);
  });

  it("carries the editor's remaining (unreachable) shortfall so its map differs from the formatted map", () => {
    const { editor, sync } = make();
    // The editor's second block sits at 260 — BELOW its preview target of 200. Height can't be removed, so
    // no spacer is added and the editor top stays 260. The snapshot must report that real (padded) 260, not
    // snap it to the formatted 200: the two maps genuinely differ where alignment is unreachable (T-073).
    editor.setTops([
      [0, 0],
      [7, 260],
    ]);
    const snapshot = sync.reconcile();
    expect(snapshot?.editor).toEqual([
      { line: 0, px: 0 },
      { line: 7, px: 260 },
      { line: 8, px: 300 },
    ]);
    expect(snapshot?.formatted[1]).toEqual({ line: 7, px: 200 });
  });

  it("subtracts the reference inset from the editor map but not the formatted map (T-061)", () => {
    // The first rendered block sits at the pane's structural inset (20); the code line 0 sits at the
    // source editor's own content padding (4). The formatted map keeps its padding-inclusive tops (the
    // coupling scrolls the pane past its padding to bring a line flush), while the editor map carries NO
    // lead for that inset — line 0 stays at its natural 4, and only the genuine mid-doc spacer (36) lifts
    // line 2. The two maps therefore differ by the inset the coupling consumes (T-061 double-count fix).
    const editor = new FakeEditor();
    const insetGeometry: BlockGeometry[] = [
      { lineStart: 0, lineEnd: 0, top: 20, height: 40 },
      { lineStart: 2, lineEnd: 2, top: 80, height: 40 },
    ];
    const sync = new HeightSync(editor as unknown as MarkdownEditor, fakeSource(insetGeometry));
    editor.setTops([
      [0, 4],
      [2, 24],
    ]);
    const snapshot = sync.reconcile();
    expect(snapshot?.formatted).toEqual([
      { line: 0, px: 20 },
      { line: 2, px: 80 },
      { line: 3, px: 120 },
    ]);
    expect(snapshot?.editor).toEqual([
      { line: 0, px: 4 },
      { line: 2, px: 60 },
      { line: 3, px: 100 },
    ]);
    expect(editor.calls[0]?.lead).toBe(0);
  });

  it("reports changed:false on a settled repeat (a stable reconcile makes no writes)", () => {
    const { editor, sync } = make();
    editor.setTops([
      [0, 0],
      [7, 40],
    ]);
    expect(sync.reconcile()?.changed).toBe(true); // first pass dispatches the 160px spacer
    expect(sync.reconcile()?.changed).toBe(false); // settled — identical set, no dispatch
    expect(editor.calls).toHaveLength(1);
  });

  it("returns null when the pass is gated by a pending edit (no snapshot formed)", () => {
    const { editor, sync } = make({ pendingChange: true });
    editor.setTops([
      [0, 0],
      [7, 40],
    ]);
    expect(sync.reconcile()).toBeNull();
    expect(editor.calls).toHaveLength(0);
  });

  it("returns an empty-anchor snapshot for a diverged split (zero blocks)", () => {
    const editor = new FakeEditor();
    const sync = new HeightSync(editor as unknown as MarkdownEditor, fakeSource([]));
    const snapshot = sync.reconcile();
    expect(snapshot?.formatted).toEqual([]);
    expect(snapshot?.editor).toEqual([]);
    // A second pass on the still-empty geometry is settled — the empty spacer set was already applied.
    expect(sync.reconcile()?.changed).toBe(false);
  });
});
