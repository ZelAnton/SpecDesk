/**
 * Assembles the collapsible-panel workspace (design concept §9): the central-frame host plus the three
 * docks, their persistence, and the one bridge to the editor — a ResizeObserver on the centre that asks the
 * owner to re-measure the editor whenever a dock open/close/resize changes the centre's size, so Split stays
 * aligned. This is the only module that knows about all the pieces at once; each piece (CentralFrame, Dock,
 * DockStore) stays independently testable, and index.ts just hands this the resolved elements, a store, and
 * a relayout callback.
 *
 * The dock tools are placeholders in this pass. Their ids match the persisted default modes (dock-state.ts)
 * and the tools a later stage will slot in, so a switch made now survives that upgrade.
 */

import { CENTRAL_VIEW_EDITOR, CentralFrame } from "./central-frame.js";
import { Dock } from "./dock.js";
import { DOCK_EDGES, type DockEdge, type WorkspaceDocksState } from "./dock-state.js";
import type { DockStore } from "./dock-store.js";
import { type PanelTool, placeholderTool } from "./panel-tool.js";

/** The tools each dock offers, in switcher order; the first is the default mode (see dock-state.ts). */
const DOCK_TOOLS: Record<DockEdge, readonly PanelTool[]> = {
  left: [
    placeholderTool(
      "navigator",
      "Navigator",
      "Navigation tools will appear here — a file navigator and document outline that can replace the centre with what you pick.",
    ),
    placeholderTool("outline", "Outline", "The document's outline will appear here."),
  ],
  right: [
    placeholderTool(
      "assistant",
      "Assistant",
      "Tools that act on the active document will appear here, starting with an AI assistant.",
    ),
    placeholderTool("tools", "Tools", "More document tools will appear here."),
  ],
  bottom: [
    placeholderTool(
      "log",
      "Log",
      "Large output will appear here — logs and other long text that doesn't fit inline in the editor.",
    ),
    placeholderTool("comment", "Comment", "The full text of a selected comment will appear here."),
  ],
};

/** The DOM the workspace wires. The docks/toggles are optional so a reduced test/host DOM still boots. */
export interface WorkspaceElements {
  readonly centralFrame: HTMLElement;
  readonly panes: HTMLElement;
  readonly docks: Record<DockEdge, HTMLElement | null>;
  readonly toggles: Record<DockEdge, HTMLButtonElement | null>;
}

export interface WorkspaceCallbacks {
  /** Re-measure the editor after the centre's size changes (a dock opened, collapsed, or resized). */
  onCentreResize(): void;
}

/**
 * Build the central-frame host and the docks, wire persistence and the centre-resize bridge, and return the
 * CentralFrame so a later stage can register alternate views and drive navigation. Docks whose element is
 * absent are simply skipped.
 */
export function setupWorkspace(
  elements: WorkspaceElements,
  store: DockStore,
  callbacks: WorkspaceCallbacks,
): CentralFrame {
  const centralFrame = new CentralFrame(elements.centralFrame);
  centralFrame.register({ id: CENTRAL_VIEW_EDITOR, el: elements.panes });
  centralFrame.show(CENTRAL_VIEW_EDITOR);

  const persisted = store.load();
  const docks = new Map<DockEdge, Dock>();

  const currentState = (): WorkspaceDocksState => ({
    left: docks.get("left")?.state() ?? persisted.left,
    right: docks.get("right")?.state() ?? persisted.right,
    bottom: docks.get("bottom")?.state() ?? persisted.bottom,
  });
  const persist = (): void => store.save(currentState());

  for (const edge of DOCK_EDGES) {
    const el = elements.docks[edge];
    if (el === null) {
      continue;
    }
    docks.set(
      edge,
      new Dock(el, edge, DOCK_TOOLS[edge], persisted[edge], elements.toggles[edge], {
        onChange: persist,
      }),
    );
  }

  // A dock open/close/resize changes the centre's box; observing it (rather than each dock) catches all
  // three uniformly and coalesces a live drag into one re-measure per frame. Guarded for jsdom, which has
  // no ResizeObserver — those tests exercise the editor relayout through the window-resize path instead.
  if (typeof ResizeObserver !== "undefined") {
    const observer = new ResizeObserver(() => callbacks.onCentreResize());
    observer.observe(elements.centralFrame);
  }

  return centralFrame;
}
