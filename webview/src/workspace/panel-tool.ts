/**
 * A panel tool = one mode of a dock (workspace/dock.ts). A dock hosts several and switches between them
 * from its header; the active tool's content fills the dock body. This is the seam a later stage plugs the
 * real tools into (a file navigator, the AI assistant, a log viewer, …); for now the docks carry simple
 * placeholder tools built by {@link placeholderTool}.
 */

export interface PanelTool {
  /** Stable id; also the persisted "active mode" value for this dock (see dock-state.ts). */
  readonly id: string;
  /** Short human label — the mode's accessible name and the dock header title. */
  readonly label: string;
  /** Inline SVG markup for the dock's mode rail (see workspace/icons.ts). */
  readonly icon: string;
  /** Build this tool's content into `body`. Called once, when the dock mounts — the tool's element is then
   *  shown/hidden as the mode switches, never rebuilt, so it keeps its own scroll/state. */
  mount(body: HTMLElement): void;
  /** Called whenever this tool becomes visible in an expanded dock. */
  onShow?(): void;
  /** Called before this tool is hidden by collapse or a mode switch. */
  onHide?(): void;
}

/**
 * A minimal placeholder tool: a muted hint line. Stands in until a later stage replaces it with the real
 * tool of the same id, so the dock chrome, mode switching, and persistence can be built and reviewed now
 * against something visible. The mode's name is shown by the dock header, so the body carries only the hint.
 */
export function placeholderTool(id: string, label: string, icon: string, hint: string): PanelTool {
  return {
    id,
    label,
    icon,
    mount(body: HTMLElement): void {
      const wrap = document.createElement("div");
      wrap.className = "dock-placeholder";

      const message = document.createElement("p");
      message.className = "dock-placeholder-hint";
      message.textContent = hint;

      wrap.appendChild(message);
      body.appendChild(wrap);
    },
  };
}
