/**
 * One collapsible dock (design concept §9): a left/right rail or the full-width bottom panel. The dock
 * builds its own chrome inside the container it is handed — a vertical icon rail (the mode switcher, when it
 * has more than one tool) on its outer edge, beside a main column: a header (the active mode's title +
 * collapse) over a body that shows the active tool. It also creates the splitter that resizes it. Every user
 * change (open/close, mode switch, resize) is reported through {@link
 * DockCallbacks.onChange} so the owner can persist it; the owner also observes the centre's size to
 * re-measure the editor, so the dock stays free of any editor/sync knowledge.
 *
 * Visibility uses the `hidden` attribute (the app's idiom): the CSS `.dock[hidden]` rule out-specifies the
 * dock's base `display:flex` so a collapsed dock leaves the layout, and its splitter is hidden alongside it
 * (the splitter has no base display, so its `[hidden]` is the plain user-agent rule). The size is an inline
 * width (side rails) or height (bottom) on a `flex:none` box, so the dock occupies exactly its clamped size
 * and the centre flexes into the rest.
 */

import { SegmentedControl, type SegmentedOption } from "../chrome/segmented-control.js";
import { clampDockSize, DOCK_SIZE_BOUNDS, type DockEdge, type DockState } from "./dock-state.js";
import type { PanelTool } from "./panel-tool.js";

export interface DockCallbacks {
  /** Persist this dock's state after a user change (open/close, mode switch, resize-end). */
  onChange(): void;
}

/** How far one arrow-key press resizes the dock (px) — a coarse, predictable keyboard step. */
const KEYBOARD_RESIZE_STEP = 16;

/** A plain-language name for each edge, woven into the chrome's accessible names so a screen-reader user
 *  with several panels open can tell the "Resize"/"Collapse"/mode controls of one rail from another. */
const EDGE_LABEL: Record<DockEdge, string> = {
  left: "left panel",
  right: "right panel",
  bottom: "bottom panel",
};

// Text-selection suppression during a splitter drag is DOCUMENT-wide (one `document.body`), but several
// docks can be dragged at once (multi-touch on different splitters). A per-drag save/restore would let the
// second drag capture the first's already-suppressed "none" and then restore THAT — leaving selection dead
// app-wide. So suppression is ref-counted across all drags: the first active drag saves the real value and
// suppresses; only the last to finish restores it.
let activeDragCount = 0;
let savedUserSelect = "";

function suppressTextSelection(): void {
  if (activeDragCount === 0) {
    savedUserSelect = document.body.style.userSelect;
    document.body.style.userSelect = "none";
  }
  activeDragCount += 1;
}

function restoreTextSelection(): void {
  activeDragCount = Math.max(0, activeDragCount - 1);
  if (activeDragCount === 0) {
    document.body.style.userSelect = savedUserSelect;
  }
}

export class Dock {
  private isOpen: boolean;
  private size: number;
  private modeId: string;
  private readonly toolBodies = new Map<string, HTMLElement>();
  private readonly modeControl: SegmentedControl<string> | null;
  private readonly splitter: HTMLElement;
  // The header title element, showing the active mode's label — assigned in buildChrome (always present
  // after construction) and updated on a mode switch.
  private titleEl: HTMLElement | null = null;
  // The pointer id of an in-progress splitter drag, or null when idle — a re-entrancy guard so a second
  // pointer (a second finger) can't start an overlapping drag and leave text selection disabled.
  private dragPointerId: number | null = null;

  constructor(
    private readonly el: HTMLElement,
    private readonly edge: DockEdge,
    private readonly tools: readonly PanelTool[],
    initial: DockState,
    private readonly toggleButton: HTMLButtonElement | null,
    private readonly callbacks: DockCallbacks,
  ) {
    // A persisted mode is honoured only if it still names one of this dock's tools (the tool set can change
    // between releases); otherwise fall back to the first tool. An empty dock has no active mode.
    this.modeId = tools.some((tool) => tool.id === initial.mode)
      ? initial.mode
      : (tools[0]?.id ?? "");
    this.size = clampDockSize(edge, initial.size);
    // An empty dock cannot be opened (nothing to show).
    this.isOpen = initial.open && tools.length > 0;

    const built = this.buildChrome();
    this.modeControl = built.modeControl;
    this.splitter = built.splitter;

    this.applyOpen();
    this.applySize();
    this.showActiveTool();
    this.modeControl?.setSelected(this.modeId);
    this.updateTitle();

    this.toggleButton?.addEventListener("click", () => this.toggle());
    this.splitter.addEventListener("pointerdown", (event) => this.onSplitterPointerDown(event));
    this.splitter.addEventListener("keydown", (event) => this.onSplitterKeyDown(event));
  }

  /** This dock's current state, for persistence. */
  state(): DockState {
    return { open: this.isOpen, size: this.size, mode: this.modeId };
  }

  /** Whether the dock is currently open. */
  get open(): boolean {
    return this.isOpen;
  }

  /** Flip open↔collapsed (the toolbar toggle / collapse button), persisting the result. */
  toggle(): void {
    this.setOpen(!this.isOpen);
  }

  /** Open or collapse the dock, persisting only on an actual change. A dock with no tools stays collapsed. */
  setOpen(open: boolean): void {
    const next = open && this.tools.length > 0;
    if (next === this.isOpen) {
      return;
    }
    // Collapsing hides the dock (and its in-dock collapse button); if focus is inside it, move focus to the
    // toolbar toggle first so a keyboard user isn't dropped to <body> mid-tab-order.
    if (!next && this.toggleButton !== null && this.el.contains(document.activeElement)) {
      this.toggleButton.focus();
    }
    this.isOpen = next;
    this.applyOpen();
    this.callbacks.onChange();
  }

  /** Switch to the mode with `id`, persisting the change; an unknown or already-active id is a no-op (the
   *  switcher is re-synced to the true mode either way, so a rejected click can't leave it mis-highlighted). */
  setMode(id: string): void {
    if (id === this.modeId || !this.toolBodies.has(id)) {
      this.modeControl?.setSelected(this.modeId);
      return;
    }
    this.modeId = id;
    this.showActiveTool();
    this.modeControl?.setSelected(id);
    this.updateTitle();
    this.callbacks.onChange();
  }

  /** Reflect the active mode's label in the header title. */
  private updateTitle(): void {
    if (this.titleEl !== null) {
      this.titleEl.textContent = this.tools.find((tool) => tool.id === this.modeId)?.label ?? "";
    }
  }

  /** Set the dock size (clamped to the edge's bounds); a no-op when the clamped size is unchanged (e.g. an
   *  arrow-key press already at the bound). Persists only when `persist` is set, so a live drag updates the
   *  layout every frame and the drag-end (which calls onChange directly) persists once. */
  setSize(size: number, persist: boolean): void {
    const next = clampDockSize(this.edge, size);
    if (next === this.size) {
      return;
    }
    this.size = next;
    this.applySize();
    if (persist) {
      this.callbacks.onChange();
    }
  }

  private applyOpen(): void {
    this.el.hidden = !this.isOpen;
    this.splitter.hidden = !this.isOpen;
    this.toggleButton?.setAttribute("aria-pressed", String(this.isOpen));
  }

  private applySize(): void {
    if (this.edge === "bottom") {
      this.el.style.height = `${this.size}px`;
    } else {
      this.el.style.width = `${this.size}px`;
    }
    // Expose the live size on the separator so a screen reader announces it (and its bounds, set once in
    // buildSplitter) as the arrow keys / drag change it.
    this.splitter.setAttribute("aria-valuenow", String(this.size));
  }

  private showActiveTool(): void {
    for (const [id, body] of this.toolBodies) {
      body.hidden = id !== this.modeId;
    }
  }

  private buildChrome(): { modeControl: SegmentedControl<string> | null; splitter: HTMLElement } {
    // Main column: a header (active-mode title + collapse) over the body (the active tool fills it).
    const main = document.createElement("div");
    main.className = "dock-main";

    const header = document.createElement("div");
    header.className = "dock-header";

    const title = document.createElement("span");
    title.className = "dock-title";
    this.titleEl = title;
    header.appendChild(title);

    const collapse = document.createElement("button");
    collapse.type = "button";
    collapse.className = "dock-collapse";
    collapse.setAttribute("aria-label", `Collapse ${EDGE_LABEL[this.edge]}`);
    collapse.title = `Collapse ${EDGE_LABEL[this.edge]}`;
    collapse.textContent = "×";
    collapse.addEventListener("click", () => this.setOpen(false));
    header.appendChild(collapse);

    const body = document.createElement("div");
    body.className = "dock-body";
    for (const tool of this.tools) {
      const toolBody = document.createElement("div");
      toolBody.className = "dock-tool";
      toolBody.dataset.tool = tool.id;
      tool.mount(toolBody);
      this.toolBodies.set(tool.id, toolBody);
      body.appendChild(toolBody);
    }

    main.append(header, body);

    // The icon rail (mode switcher) is added only when there is more than one tool; a single-tool dock just
    // shows its title. The rail sits on the dock's OUTER edge (a left rail's rail on the left, a right rail's
    // on the right, the bottom dock's on the left) so the content stays adjacent to the centre.
    const rail = this.tools.length >= 2 ? this.buildRail() : null;
    if (this.edge === "right") {
      this.el.appendChild(main);
      if (rail !== null) {
        this.el.appendChild(rail.el);
      }
    } else {
      if (rail !== null) {
        this.el.appendChild(rail.el);
      }
      this.el.appendChild(main);
    }

    return { modeControl: rail?.control ?? null, splitter: this.buildSplitter() };
  }

  /** A vertical icon rail: one icon button per tool, in an ARIA radiogroup (reusing SegmentedControl's
   *  roving-tabindex + arrow-key selection). Scales to many modes where a horizontal switcher can't. */
  private buildRail(): { el: HTMLElement; control: SegmentedControl<string> } {
    const rail = document.createElement("div");
    rail.className = "dock-rail";
    rail.setAttribute("role", "radiogroup");
    rail.setAttribute("aria-orientation", "vertical");
    rail.setAttribute("aria-label", `${EDGE_LABEL[this.edge]} mode`);

    const options: SegmentedOption<string>[] = this.tools.map((tool) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "dock-rail-btn";
      button.setAttribute("role", "radio");
      button.setAttribute("aria-checked", "false");
      button.tabIndex = -1;
      // Icon-only: the label is the accessible name AND the hover tooltip (icons carry no text). The icon
      // markup is a trusted in-repo constant (workspace/icons.ts).
      button.innerHTML = tool.icon;
      button.setAttribute("aria-label", tool.label);
      button.title = tool.label;
      rail.appendChild(button);
      return { el: button, value: tool.id };
    });

    return { el: rail, control: new SegmentedControl(options, (id) => this.setMode(id)) };
  }

  private buildSplitter(): HTMLElement {
    const splitter = document.createElement("div");
    splitter.className = `dock-splitter dock-splitter-${this.edge}`;
    splitter.setAttribute("role", "separator");
    splitter.setAttribute("aria-orientation", this.edge === "bottom" ? "horizontal" : "vertical");
    splitter.setAttribute("aria-label", `Resize ${EDGE_LABEL[this.edge]}`);
    splitter.setAttribute("aria-valuemin", String(DOCK_SIZE_BOUNDS[this.edge].min));
    splitter.setAttribute("aria-valuemax", String(DOCK_SIZE_BOUNDS[this.edge].max));
    splitter.tabIndex = 0;
    // The splitter sits between the dock and the centre: after a left rail, before a right rail or the
    // bottom dock. It is a sibling in the same flex container as the dock (see index.html / styles.css).
    this.el.insertAdjacentElement(this.edge === "left" ? "afterend" : "beforebegin", splitter);
    return splitter;
  }

  private onSplitterPointerDown(event: PointerEvent): void {
    // Ignore non-primary buttons (a right-click must not resize) and any pointer while a drag is already in
    // progress — so a second finger can't start an overlapping drag whose end would clobber the restored
    // text-selection state.
    if (event.button !== 0 || this.dragPointerId !== null) {
      return;
    }
    event.preventDefault();
    this.dragPointerId = event.pointerId;
    const axisStart = this.edge === "bottom" ? event.clientY : event.clientX;
    const sizeStart = this.size;
    // Suppress text selection for the drag (ref-counted, so concurrent drags don't clobber each other's
    // restore — see suppressTextSelection).
    suppressTextSelection();
    // Capture the pointer so a release OUTSIDE the window still ends the drag (delivers pointerup/cancel to
    // the splitter, which bubbles to the window listeners below). Best-effort — an inactive pointer id can
    // be rejected; the window listeners drive the common in-window case regardless.
    try {
      this.splitter.setPointerCapture(event.pointerId);
    } catch {
      // Pointer id not capturable (e.g. already released) — nothing to route; the window listeners suffice.
    }

    // Track on `window` (the drag leaves the 1px splitter immediately) and filter to this drag's pointer id
    // so an unrelated pointer's events can't perturb the size.
    const onMove = (move: PointerEvent): void => {
      if (move.pointerId !== this.dragPointerId) {
        return;
      }
      const axisNow = this.edge === "bottom" ? move.clientY : move.clientX;
      const delta = axisNow - axisStart;
      // Growing the dock means dragging away from its edge: a left rail grows as the pointer moves right
      // (+delta); a right rail and the bottom dock grow as it moves toward their edge (−delta).
      const raw = this.edge === "left" ? sizeStart + delta : sizeStart - delta;
      this.setSize(raw, false);
    };
    const onUp = (up: PointerEvent): void => {
      if (up.pointerId !== this.dragPointerId) {
        return;
      }
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onUp);
      restoreTextSelection();
      try {
        this.splitter.releasePointerCapture(up.pointerId);
      } catch {
        // Capture may never have been acquired, or was released by the release itself — nothing to undo.
      }
      this.dragPointerId = null;
      // Persist only if the drag actually moved the size (a zero-movement click leaves it untouched).
      if (this.size !== sizeStart) {
        this.callbacks.onChange();
      }
    };
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onUp);
  }

  private onSplitterKeyDown(event: KeyboardEvent): void {
    let delta = 0;
    if (this.edge === "bottom") {
      if (event.key === "ArrowUp") {
        delta = KEYBOARD_RESIZE_STEP;
      } else if (event.key === "ArrowDown") {
        delta = -KEYBOARD_RESIZE_STEP;
      }
    } else {
      // A left rail grows with ArrowRight, a right rail with ArrowLeft (away from its own edge).
      const grow = this.edge === "left" ? "ArrowRight" : "ArrowLeft";
      const shrink = this.edge === "left" ? "ArrowLeft" : "ArrowRight";
      if (event.key === grow) {
        delta = KEYBOARD_RESIZE_STEP;
      } else if (event.key === shrink) {
        delta = -KEYBOARD_RESIZE_STEP;
      }
    }
    if (delta !== 0) {
      event.preventDefault();
      this.setSize(this.size + delta, true);
    }
  }
}
