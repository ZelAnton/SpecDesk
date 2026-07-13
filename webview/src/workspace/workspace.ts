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

import type { WorkspaceItem } from "../wire/protocol.js";
import { CENTRAL_VIEW_EDITOR, CentralFrame } from "./central-frame.js";
import { Dock } from "./dock.js";
import { DOCK_EDGES, type DockEdge, type WorkspaceDocksState } from "./dock-state.js";
import type { DockStore } from "./dock-store.js";
import { icon } from "./icons.js";
import { type PanelTool, placeholderTool } from "./panel-tool.js";
import { buildHomeView, type HomeView } from "./tools/home-view.js";
import { type NavDestination, Navigator } from "./tools/navigator.js";
import { buildNotificationsView } from "./tools/notifications-view.js";
import { Outline } from "./tools/outline.js";

/** The central view id of the Start screen — the concrete second view the navigator substitutes in. */
export const CENTRAL_VIEW_HOME = "home";
/** The central Notifications list opened from the global toolbar. */
export const CENTRAL_VIEW_NOTIFICATIONS = "notifications";

/** The navigator's destinations, in list order: the document editor and the Start screen. */
const NAV_DESTINATIONS: readonly NavDestination[] = [
  { id: CENTRAL_VIEW_EDITOR, label: "Document", hint: "The spec you're editing" },
  { id: CENTRAL_VIEW_HOME, label: "Start", hint: "Open or pick a spec" },
];

/** The DOM the workspace wires. The docks and the Start view are optional so a reduced test/host DOM still
 *  boots. Each dock's persistent mode rail owns expansion/collapse; there are no duplicate toolbar toggles. */
export interface WorkspaceElements {
  readonly centralFrame: HTMLElement;
  /** The editor central view — wraps the formatting toolbar and the panes (see index.html #editor-view). */
  readonly editorView: HTMLElement;
  readonly homeView: HTMLElement | null;
  readonly notificationsView: HTMLElement | null;
  readonly docks: Record<DockEdge, HTMLElement | null>;
}

export interface WorkspaceCallbacks {
  /** Re-measure the editor after the centre's size changes (a dock opened, collapsed, or resized). */
  onCentreResize(): void;
  /** The active central view changed — index.ts gates editor-only chrome (the view-mode switch) on it. */
  onCentralViewChange(viewId: string): void;
  /** Open a single spec file from the Start screen (host file picker) — same action as the toolbar "Open…". */
  onOpenFile(): void;
  /** Open a folder as the workspace from the Start screen (host folder picker); fills the file navigator. */
  onOpenFolder(): void;
  /** Open a recent item from the Start screen (a folder → `folder.open`, a file → `doc.open`). */
  onOpenItem(item: WorkspaceItem): void;
  /** Scroll the editor to a 0-based source line (an outline heading was clicked). */
  onOutlineNavigate(line: number): void;
}

/** What setupWorkspace hands back to index.ts: the central-frame host, the outline tool to feed, and the
 *  Start view (when present) so index.ts can feed it the recent items. */
export interface WorkspaceHandle {
  readonly centralFrame: CentralFrame;
  readonly outline: Outline;
  /** The Start view, when its element was present — index.ts feeds it recents from `workspace.state`. */
  readonly home?: HomeView;
  /** Reveal a dock tool programmatically: open the dock on `edge` and switch it to `toolId` (a no-op if that
   *  dock or tool is absent). Lets index.ts surface a panel — e.g. the Files navigator when a workspace opens. */
  readonly revealTool: (edge: DockEdge, toolId: string) => void;
}

/** The real dock tools index.ts builds (they need IPC/host wiring, which stays out of the workspace).
 *  Any tool left absent falls back to a placeholder, so a reduced test/host DOM still boots. */
export interface WorkspaceTools {
  /** The right rail's AI assistant chat (replaces the "assistant" placeholder). */
  readonly assistant?: PanelTool;
  readonly versions?: PanelTool;
  readonly comments?: PanelTool;
  readonly history?: PanelTool;
  /** The left rail's workspace file navigator (the folder tree). Absent → a placeholder in a reduced DOM. */
  readonly files?: PanelTool;
  /** The left rail's Recent panel (recently-opened files/folders). Absent → a placeholder. */
  readonly recent?: PanelTool;
  /** The left rail's Favorites panel (starred files/folders). Absent → a placeholder. */
  readonly favorites?: PanelTool;
  /** The left rail's Repositories panel (registered GitHub repos). Absent → a placeholder. */
  readonly repositories?: PanelTool;
  /** Review requests assigned directly or through a known GitHub team. */
  readonly reviews?: PanelTool;
  /** Open pull requests authored by or otherwise involving the signed-in user. */
  readonly pullRequests?: PanelTool;
}

/**
 * Build the central-frame host (editor + Start views), the navigator that switches between them, and the
 * docks; wire persistence, the centre-resize bridge, and the active-view notifications. Returns a handle
 * (the CentralFrame + the outline tool to feed). Docks / the Start view whose element is absent are skipped.
 */
export function setupWorkspace(
  elements: WorkspaceElements,
  store: DockStore,
  callbacks: WorkspaceCallbacks,
  tools: WorkspaceTools = {},
): WorkspaceHandle {
  // The navigator's onNavigate and the frame's onChange reference each other, so forward-declare the frame;
  // both closures only fire on later user interaction, by which point it is assigned.
  let centralFrame: CentralFrame;
  // The Start screen is built before the docks. Its repository action closes over this function, which is
  // assigned once the docks exist later in this synchronous setup.
  let revealRepositories: () => void = () => {};
  const navigator = new Navigator(NAV_DESTINATIONS, (id) => centralFrame.show(id));
  // The document-outline tool (right rail): index.ts feeds it headings via the returned handle, and a click
  // scrolls the editor through onOutlineNavigate.
  const outline = new Outline((line) => callbacks.onOutlineNavigate(line));

  centralFrame = new CentralFrame(elements.centralFrame, (id) => {
    navigator.setActive(id);
    callbacks.onCentralViewChange(id);
  });
  centralFrame.register({ id: CENTRAL_VIEW_EDITOR, el: elements.editorView });
  // The Start view's handle (when its element is present), so index.ts can feed it the recent items.
  let home: HomeView | undefined;
  if (elements.homeView !== null) {
    // Opening a spec from the Start screen runs the same action as the toolbar; index.ts returns the centre
    // to the editor on the resulting doc.loaded (so cancelling the file dialog leaves the author on Start,
    // not a blank editor). A recent item opens through the same path the left-rail panels use.
    home = buildHomeView(elements.homeView, {
      onOpenFile: () => callbacks.onOpenFile(),
      onOpenFolder: () => callbacks.onOpenFolder(),
      onOpenItem: (item) => callbacks.onOpenItem(item),
      onOpenRepositories: () => revealRepositories(),
    });
    centralFrame.register({ id: CENTRAL_VIEW_HOME, el: elements.homeView });
  }
  if (elements.notificationsView !== null) {
    buildNotificationsView(elements.notificationsView);
    centralFrame.register({ id: CENTRAL_VIEW_NOTIFICATIONS, el: elements.notificationsView });
  }
  centralFrame.show(CENTRAL_VIEW_EDITOR);
  // Seed the navigator's highlight (the initial show above is a no-op when the editor is already active, so
  // its onChange doesn't fire — set the current destination explicitly).
  navigator.setActive(CENTRAL_VIEW_EDITOR);

  const toolsByEdge: Record<DockEdge, readonly PanelTool[]> = {
    left: [
      navigator,
      // The real workspace file navigator when index.ts wired it; a placeholder in a reduced DOM (tests/host).
      tools.files ??
        placeholderTool(
          "files",
          "Files",
          icon("files"),
          "The folders and specs of an opened workspace will appear here.",
        ),
      // Recent / Favorites / Repositories: the real tools when index.ts wired them, else placeholders whose
      // ids/labels match, so the reduced DOM boots and a persisted active mode still resolves.
      tools.recent ??
        placeholderTool(
          "recent",
          "Recent",
          icon("recent"),
          "Files and folders you open will appear here.",
        ),
      tools.favorites ??
        placeholderTool(
          "favorites",
          "Favorites",
          icon("favorites"),
          "Star a file or folder to keep it here.",
        ),
      tools.repositories ??
        placeholderTool(
          "repositories",
          "Repositories",
          icon("repositories"),
          "Register a repository to keep it handy.",
        ),
      tools.reviews ??
        placeholderTool(
          "reviews",
          "Review",
          icon("review"),
          "Reviews waiting for you will appear here.",
        ),
      tools.pullRequests ??
        placeholderTool(
          "pullRequests",
          "Pull Requests",
          icon("pullRequests"),
          "Open pull requests involving you will appear here.",
        ),
    ],
    right: [
      // The real AI assistant chat when index.ts wired it; the placeholder in a reduced DOM (tests/host).
      tools.assistant ??
        placeholderTool(
          "assistant",
          "Assistant",
          icon("assistant"),
          "Tools that act on the active document will appear here, starting with an AI assistant.",
        ),
      outline,
      tools.versions ??
        placeholderTool(
          "versions",
          "Versions",
          icon("versions"),
          "Saved versions will appear here.",
        ),
      tools.comments ??
        placeholderTool(
          "comments",
          "Comments",
          icon("comment"),
          "Document comments will appear here.",
        ),
      tools.history ??
        placeholderTool(
          "history",
          "Change history",
          icon("history"),
          "Saved changes will appear here.",
        ),
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
      new Dock(el, edge, toolsByEdge[edge], persisted[edge], {
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

  // Reveal a dock tool: open its dock and switch to the tool (both no-ops if absent). index.ts uses this to
  // surface the Files navigator when a workspace (folder or repo) opens — also fixing the case where opening
  // a folder gave no visible feedback because the left dock was collapsed.
  const revealTool = (edge: DockEdge, toolId: string): void => {
    const dock = docks.get(edge);
    if (dock === undefined) {
      return;
    }
    dock.setOpen(true);
    dock.setMode(toolId);
  };
  revealRepositories = () => revealTool("left", "repositories");

  // exactOptionalPropertyTypes: only include `home` when the Start view was actually built (never assign it
  // an explicit undefined).
  return home !== undefined
    ? { centralFrame, outline, home, revealTool }
    : { centralFrame, outline, revealTool };
}
