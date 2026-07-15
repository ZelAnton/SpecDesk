/**
 * One collapsible dock (design concept §9): a left/right rail or the full-width bottom panel. The dock
 * builds its own chrome inside the container it is handed — a vertical icon rail (the mode switcher, when it
 * has more than one tool) on its outer edge, beside a main column: a header (the active mode's title +
 * collapse) over a body that shows the active tool. It also creates the splitter that resizes it. Every user
 * change (open/close, mode switch, resize) is reported through {@link
 * DockCallbacks.onChange} so the owner can persist it; the owner also observes the centre's size to
 * re-measure the editor, so the dock stays free of any editor/sync knowledge.
 *
 * A collapsed side dock keeps its mode rail visible, so the same active icon that collapsed it can expand
 * again. A dock can instead omit its rail and leave layout completely when closed; the workspace uses that
 * form for the bottom panel because its toggle lives at the foot of the right rail.
 */

import { SegmentedControl, type SegmentedOption } from "../chrome/segmented-control.js";
import { clampDockSize, DOCK_SIZE_BOUNDS, type DockEdge, type DockState } from "./dock-state.js";
import type { PanelTool } from "./panel-tool.js";

export interface DockCallbacks {
  /** Persist this dock's state after a user change (open/close, mode switch, resize-end). */
  onChange(): void;
}

export interface DockOptions {
  /** Omit this dock's own mode rail. Used by the bottom panel, which is toggled from the right rail. */
  readonly showRail?: boolean;
  /** Remove a closed dock from layout instead of retaining a collapsed rail. */
  readonly hideWhenClosed?: boolean;
  /** Stable focus destination when a rail-less dock is closed from inside its panel. */
  readonly focusAfterClose?: () => void;
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
  // Context fallbacks must not erase the author's preferred mode. Restore it when it applies again.
  private preferredModeId: string;
  private availableToolIds: ReadonlySet<string>;
  private readonly toolBodies = new Map<string, HTMLElement>();
  private readonly railButtons = new Map<string, HTMLButtonElement>();
  private readonly modeControl: SegmentedControl<string> | null;
  private readonly railEl: HTMLElement | null;
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
    private readonly callbacks: DockCallbacks,
    private readonly options: DockOptions = {},
  ) {
    // A persisted mode is honoured only if it still names one of this dock's tools (the tool set can change
    // between releases); otherwise fall back to the first tool. An empty dock has no active mode.
    this.modeId = tools.some((tool) => tool.id === initial.mode)
      ? initial.mode
      : (tools[0]?.id ?? "");
    this.preferredModeId = this.modeId;
    this.availableToolIds = new Set(tools.map((tool) => tool.id));
    this.size = clampDockSize(edge, initial.size);
    // An empty dock cannot be opened (nothing to show).
    this.isOpen = initial.open && tools.length > 0;

    const built = this.buildChrome();
    this.modeControl = built.modeControl;
    this.railEl = built.railEl;
    this.splitter = built.splitter;

    this.applyOpen();
    this.applySize();
    this.showActiveTool();
    this.modeControl?.setSelected(this.modeId);
    this.updateTitle();
    if (this.isOpen) {
      this.activeTool()?.onShow?.();
    }

    this.splitter.addEventListener("pointerdown", (event) => this.onSplitterPointerDown(event));
    this.splitter.addEventListener("keydown", (event) => this.onSplitterKeyDown(event));
  }

  /** This dock's current state, for persistence. */
  state(): DockState {
    return { open: this.isOpen, size: this.size, mode: this.preferredModeId };
  }

  /** Whether the dock is currently open. */
  get open(): boolean {
    return this.isOpen;
  }

  /** Flip open↔collapsed (the active rail icon / in-panel collapse button), persisting the result. */
  toggle(): void {
    this.setOpen(!this.isOpen);
  }

  /** Open or collapse the dock, persisting only on an actual change. A dock with no tools stays collapsed. */
  setOpen(open: boolean): void {
    const next = open && this.availableToolIds.size > 0;
    if (next === this.isOpen) {
      return;
    }
    // Collapsing hides the main panel. If focus is inside it, move focus to the still-visible active mode
    // icon first so a keyboard user isn't dropped to <body> mid-tab-order.
    const activeButton = this.railButtons.get(this.modeId);
    if (!next && this.el.contains(document.activeElement)) {
      if (activeButton !== undefined) {
        activeButton.focus();
      } else {
        this.options.focusAfterClose?.();
      }
    }
    if (!next) {
      this.activeTool()?.onHide?.();
    }
    this.isOpen = next;
    this.applyOpen();
    if (next) {
      this.activeTool()?.onShow?.();
    }
    this.callbacks.onChange();
  }

  /** Switch to the mode with `id`, persisting the change; an unknown or already-active id is a no-op (the
   *  switcher is re-synced to the true mode either way, so a rejected click can't leave it mis-highlighted). */
  setMode(id: string): void {
    if (!this.toolBodies.has(id) || !this.availableToolIds.has(id)) {
      this.modeControl?.setSelected(this.modeId);
      return;
    }
    this.preferredModeId = id;
    if (id === this.modeId) {
      this.modeControl?.setSelected(this.modeId);
      return;
    }
    if (this.isOpen) {
      this.activeTool()?.onHide?.();
    }
    this.modeId = id;
    this.showActiveTool();
    this.modeControl?.setSelected(id);
    this.updateTitle();
    this.applyOpen();
    if (this.isOpen) {
      this.activeTool()?.onShow?.();
    }
    this.callbacks.onChange();
  }

  /** Apply the tools admitted by the active context without persisting a temporary fallback. */
  setAvailableTools(ids: ReadonlySet<string>): void {
    const previousMode = this.modeId;
    const previousTool = this.tools.find((tool) => tool.id === previousMode);
    const previousBody = this.toolBodies.get(previousMode);
    const focusWasInPreviousBody = previousBody?.contains(document.activeElement) ?? false;
    const wasOpen = this.isOpen;
    this.availableToolIds = new Set(
      this.tools.filter((tool) => ids.has(tool.id)).map((tool) => tool.id),
    );
    const focusedMode = Array.from(this.railButtons.entries()).find(([, button]) =>
      button.contains(document.activeElement),
    )?.[0];
    for (const tool of this.tools) {
      const available = this.availableToolIds.has(tool.id);
      const button = this.railButtons.get(tool.id);
      if (button !== undefined) {
        button.hidden = !available;
        button.setAttribute("aria-hidden", String(!available));
      }
    }

    if (this.availableToolIds.has(this.preferredModeId)) {
      this.modeId = this.preferredModeId;
    } else if (!this.availableToolIds.has(this.modeId)) {
      this.modeId = this.tools.find((tool) => this.availableToolIds.has(tool.id))?.id ?? "";
    }
    const modeChanged = previousMode !== this.modeId;
    if (wasOpen && (modeChanged || this.availableToolIds.size === 0)) {
      previousTool?.onHide?.();
    }
    if (this.availableToolIds.size === 0) {
      this.isOpen = false;
    }
    this.showActiveTool();
    this.modeControl?.setSelected(this.modeId);
    this.updateTitle();
    this.applyOpen();
    if (wasOpen && modeChanged && this.availableToolIds.size > 0) {
      this.activeTool()?.onShow?.();
    }

    if (
      (focusedMode !== undefined && !this.availableToolIds.has(focusedMode)) ||
      (focusWasInPreviousBody && modeChanged)
    ) {
      this.railButtons.get(this.modeId)?.focus();
    }
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

  /** Add a non-mode action to the end of a side rail. It deliberately sits outside the radiogroup so its
   *  pressed state and keyboard semantics remain those of a toggle button, not a selectable panel mode. */
  addRailAction(
    id: string,
    label: string,
    iconMarkup: string,
    onActivate: () => void,
  ): HTMLButtonElement | null {
    if (this.railEl === null) return null;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "dock-rail-action";
    button.dataset.action = id;
    button.innerHTML = iconMarkup;
    button.setAttribute("aria-label", label);
    button.setAttribute("aria-pressed", "false");
    button.title = label;
    button.addEventListener("click", onActivate);
    this.railEl.appendChild(button);
    return button;
  }

  private applyOpen(): void {
    this.el.hidden =
      this.availableToolIds.size === 0 || (this.options.hideWhenClosed === true && !this.isOpen);
    this.el.classList.toggle("dock--collapsed", !this.isOpen);
    this.splitter.hidden = !this.isOpen;
    this.railEl
      ?.querySelector(".dock-mode-list")
      ?.setAttribute(
        "aria-orientation",
        this.edge === "bottom" && !this.isOpen ? "horizontal" : "vertical",
      );
    for (const [id, button] of this.railButtons) {
      button.setAttribute("aria-expanded", String(id === this.modeId && this.isOpen));
    }
    this.applySize();
  }

  private applySize(): void {
    if (this.edge === "bottom") {
      this.el.style.height = this.isOpen ? `${this.size}px` : "";
    } else {
      this.el.style.width = this.isOpen ? `${this.size}px` : "";
    }
    // Expose the live size on the separator so a screen reader announces it (and its bounds, set once in
    // buildSplitter) as the arrow keys / drag change it.
    this.splitter.setAttribute("aria-valuenow", String(this.size));
  }

  private showActiveTool(): void {
    for (const [id, body] of this.toolBodies) {
      body.hidden = id !== this.modeId || !this.availableToolIds.has(id);
    }
  }

  private activeTool(): PanelTool | undefined {
    return this.availableToolIds.has(this.modeId)
      ? this.tools.find((tool) => tool.id === this.modeId)
      : undefined;
  }

  private buildChrome(): {
    modeControl: SegmentedControl<string> | null;
    railEl: HTMLElement | null;
    splitter: HTMLElement;
  } {
    // Main column: a header (active-mode title + collapse) over the body (the active tool fills it).
    const main = document.createElement("div");
    main.className = "dock-main";
    main.id = `${this.el.id || `${this.edge}-dock`}-main`;

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

    // Every non-empty dock has a mode rail: besides switching tools, its active icon is the dock's sole
    // expand/collapse control while the main panel is hidden. The rail sits on the dock's OUTER edge.
    const rail = this.tools.length > 0 && this.options.showRail !== false ? this.buildRail() : null;
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

    return {
      modeControl: rail?.control ?? null,
      railEl: rail?.el ?? null,
      splitter: this.buildSplitter(),
    };
  }

  /** A vertical icon rail: one icon button per tool, in an ARIA radiogroup (reusing SegmentedControl's
   *  roving-tabindex + arrow-key selection). Scales to many modes where a horizontal switcher can't. */
  private buildRail(): { el: HTMLElement; control: SegmentedControl<string> } {
    const rail = document.createElement("div");
    rail.className = "dock-rail";
    const modes = document.createElement("div");
    modes.className = "dock-mode-list";
    modes.setAttribute("role", "radiogroup");
    modes.setAttribute("aria-orientation", "vertical");
    modes.setAttribute("aria-label", `${EDGE_LABEL[this.edge]} mode`);

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
      button.dataset.tool = tool.id;
      button.setAttribute("aria-controls", `${this.el.id || `${this.edge}-dock`}-main`);
      this.railButtons.set(tool.id, button);
      modes.appendChild(button);
      return { el: button, value: tool.id };
    });

    rail.appendChild(modes);
    return { el: rail, control: new SegmentedControl(options, (id) => this.activateMode(id)) };
  }

  /** A rail click selects a different tool and opens it, or toggles the panel when its active icon is
   * clicked. Switching a mode and opening is one persisted user action rather than two intermediate saves. */
  private activateMode(id: string): void {
    if (!this.availableToolIds.has(id)) {
      this.modeControl?.setSelected(this.modeId);
      return;
    }
    if (id === this.modeId) {
      this.toggle();
      return;
    }
    if (!this.toolBodies.has(id)) {
      this.modeControl?.setSelected(this.modeId);
      return;
    }
    if (this.isOpen) {
      this.activeTool()?.onHide?.();
    }
    this.preferredModeId = id;
    this.modeId = id;
    this.isOpen = true;
    this.showActiveTool();
    this.modeControl?.setSelected(id);
    this.updateTitle();
    this.applyOpen();
    this.activeTool()?.onShow?.();
    this.callbacks.onChange();
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
