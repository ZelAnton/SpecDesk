/**
 * The review/compare overlay state machine (PoC-6). "Show changes" diffs the working copy against the
 * last saved version and washes the changed lines/blocks in BOTH editors. The overlay is a snapshot
 * taken at the docVersion of the click: any genuine edit (or a version save / reload) invalidates it,
 * so {@link ReviewController.clear} drops it and un-presses the button; the author clicks Show changes
 * again to recompute. It owns only the `reviewing` flag and the editors' diff marks — the integrator
 * keeps all ipc/Kinds/decoder knowledge and feeds parsed results in (see index.ts), mirroring how the
 * inline prompt bars reach the host only through callbacks (dialogs.ts).
 */

import { expandDiffMarks } from "./diff-marks.js";
import type { DiffMark } from "./editor.js";
import type { DiffEntryPayload } from "./protocol.js";

/** The slice of an editor the overlay paints — both MarkdownEditor and FormattedEditor satisfy it. */
export interface DiffSurface {
  getText(): string;
  setDiff(marks: DiffMark[]): void;
  clearDiff(): void;
}

export interface ReviewDeps {
  /** The editable panes the overlay washes (Split shows both at once); at least one. The first is the
   *  canonical head whose text the diff is expanded against — in Split the panes are kept mirrored, so
   *  either reads the same source. */
  surfaces: readonly [DiffSurface, ...DiffSurface[]];
  /** Reflect the toggle's pressed state on the "Show changes" button. */
  setPressed: (on: boolean) => void;
  /** Ask the host to diff the working copy against the last saved version. The integrator stamps the
   *  current docVersion on the request so a stale reply is dropped by {@link ReviewController.applyResult}. */
  requestCompare: () => void;
  /** The live monotonic document version, for the result's version-gate. */
  docVersion: () => number;
}

export class ReviewController {
  private reviewing = false;

  constructor(private readonly deps: ReviewDeps) {}

  /** Toggle the overlay: clear a showing one, or start a fresh compare (press the button and ask the
   *  host to diff — the marks arrive later via {@link applyResult}). */
  toggle(): void {
    if (this.reviewing) {
      this.clear();
      return;
    }
    this.reviewing = true;
    this.deps.setPressed(true);
    this.deps.requestCompare();
  }

  /** Drop the overlay: un-press the button and clear the marks in every surface. A genuine edit, a
   *  version save, a discard, or a document reload calls this; a no-op when nothing is showing. */
  clear(): void {
    if (!this.reviewing) {
      return;
    }
    this.reviewing = false;
    this.deps.setPressed(false);
    for (const surface of this.deps.surfaces) {
      surface.clearDiff();
    }
  }

  /** Apply a `diff.result`. Dropped unless the overlay is still showing and the result matches the live
   *  version — i.e. the author hasn't edited past the snapshot the request was taken at (the same
   *  version-gate the preview uses). Marks are expanded against the current head — the version-gate
   *  guarantees it matches the diff's head — so a changed list/table resolves to per-row/item marks
   *  rather than a whole-container wash. */
  applyResult(version: number, entries: DiffEntryPayload[]): void {
    if (!this.reviewing || version !== this.deps.docVersion()) {
      return;
    }
    const [head] = this.deps.surfaces;
    const marks = expandDiffMarks(entries, head.getText());
    for (const surface of this.deps.surfaces) {
      surface.setDiff(marks);
    }
  }
}
