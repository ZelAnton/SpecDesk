/**
 * Assembles the collapsible-panel workspace (design concept §9): the central-frame host, its views (the
 * editor plus a Start view), the three docks with their tools, persistence, and the bridges to index.ts —
 * a ResizeObserver that re-measures the editor when the centre's size changes, plus notifications when the
 * active central view changes (so index.ts can gate editor-only chrome). This is the one module that knows
 * all the pieces at once; each piece (CentralFrame, Dock, DockStore, Navigator) stays independently
 * testable.
 *
 * The left rail's navigator is the working tool this stage adds; it substitutes the centre between the
 * editor and the Start view. The other dock tools are placeholders until later stages fill them.
 */

import { CENTRAL_VIEW_EDITOR, CentralFrame } from "./central-frame.js";
import { Dock } from "./dock.js";
import { DOCK_EDGES, type DockEdge, type WorkspaceDocksState } from "./dock-state.js";
import type { DockStore } from "./dock-store.js";
import { icon } from "./icons.js";
import { type PanelTool, placeholderTool } from "./panel-tool.js";
import { buildHomeView } from "./tools/home-view.js";
import { type NavDestination, Navigator } from "./tools/navigator.js";

/** The central view id of the Start screen — the concrete second view the navigator substitutes in. */
export const CENTRAL_VIEW_HOME = "home";

/** The navigator's destinations, in list order: the document editor and the Start screen. */
const NAV_DESTINATIONS: readonly NavDestination[] = [
  { id: CENTRAL_VIEW_EDITOR, label: "Document", hint: "The spec you're editing" },
  { id: CENTRAL_VIEW_HOME, label: "Start", hint: "Open or pick a spec" },
];

/** The DOM the workspace wires. The docks/toggles and the Start view are optional so a reduced test/host
 *  DOM still boots. */
export interface WorkspaceElements {
  readonly centralFrame: HTMLElement;
  /** The editor central view — wraps the formatting toolbar and the panes (see index.html #editor-view). */
  readonly editorView: HTMLElement;
  readonly homeView: HTMLElement | null;
  readonly docks: Record<DockEdge, HTMLElement | null>;
  readonly toggles: Record<DockEdge, HTMLButtonElement | null>;
}

export interface WorkspaceCallbacks {
  /** Re-measure the editor after the centre's size changes (a dock opened, collapsed, or resized). */
  onCentreResize(): void;
  /** The active central view changed — index.ts gates editor-only chrome (the view-mode switch) on it. */
  onCentralViewChange(viewId: string): void;
  /** Run the "Open…" action (for the Start view's button — the same action as the toolbar). */
  onOpenDocument(): void;
}

/**
 * Build the central-frame host (editor + Start views), the navigator that switches between them, and the
 * docks; wire persistence, the centre-resize bridge, and the active-view notifications. Returns the
 * CentralFrame for any later use. Docks / the Start view whose element is absent are simply skipped.
 */
export function setupWorkspace(
  elements: WorkspaceElements,
  store: DockStore,
  callbacks: WorkspaceCallbacks,
): CentralFrame {
  // The navigator's onNavigate and the frame's onChange reference each other, so forward-declare the frame;
  // both closures only fire on later user interaction, by which point it is assigned.
  let centralFrame: CentralFrame;
  const navigator = new Navigator(NAV_DESTINATIONS, (id) => centralFrame.show(id));

  centralFrame = new CentralFrame(elements.centralFrame, (id) => {
    navigator.setActive(id);
    callbacks.onCentralViewChange(id);
  });
  centralFrame.register({ id: CENTRAL_VIEW_EDITOR, el: elements.editorView });
  if (elements.homeView !== null) {
    // Opening a spec from the Start screen runs the same action as the toolbar; index.ts returns the centre
    // to the editor on the resulting doc.loaded (so cancelling the file dialog leaves the author on Start,
    // not a blank editor).
    buildHomeView(elements.homeView, () => callbacks.onOpenDocument());
    centralFrame.register({ id: CENTRAL_VIEW_HOME, el: elements.homeView });
  }
  centralFrame.show(CENTRAL_VIEW_EDITOR);
  // Seed the navigator's highlight (the initial show above is a no-op when the editor is already active, so
  // its onChange doesn't fire — set the current destination explicitly).
  navigator.setActive(CENTRAL_VIEW_EDITOR);

  const toolsByEdge: Record<DockEdge, readonly PanelTool[]> = {
    left: [
      navigator,
      placeholderTool(
        "outline",
        "Outline",
        icon("outline"),
        "The document's outline will appear here.",
      ),
    ],
    right: [
      placeholderTool(
        "assistant",
        "Assistant",
        icon("assistant"),
        "Tools that act on the active document will appear here, starting with an AI assistant.",
      ),
      placeholderTool("tools", "Tools", icon("tools"), "More document tools will appear here."),
    ],
    bottom: [
      placeholderTool(
        "log",
        "Log",
        icon("log"),
        "Large output will appear here — logs and other long text that doesn't fit inline in the editor.",
      ),
      placeholderTool(
        "comment",
        "Comment",
        icon("comment"),
        "The full text of a selected comment will appear here.",
      ),
    ],
  };

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
      new Dock(el, edge, toolsByEdge[edge], persisted[edge], elements.toggles[edge], {
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
