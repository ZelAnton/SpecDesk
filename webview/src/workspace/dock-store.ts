/**
 * The localStorage-backed persistence for the workspace docks — the one place that touches Web Storage, so
 * the pure codec (dock-state.ts) and the DOM controller (dock.ts) stay free of IO. Storage is injectable so
 * tests drive an in-memory double, and every access is guarded: WebView2 can have storage disabled and a
 * quota-exceeded write must never throw into the UI wiring.
 */

import {
  DOCKS_STORAGE_KEY,
  parseDocksState,
  serializeDocksState,
  type WorkspaceDocksState,
} from "./dock-state.js";

export class DockStore {
  /** @param storage the backing store (`window.localStorage`), or `null` when unavailable — then load()
   *  returns defaults and save() is a no-op, so the docks still work for the session, just unpersisted. */
  constructor(private readonly storage: Storage | null) {}

  /** Read the persisted state, degrading to defaults on an absent, unreadable, or corrupt value. */
  load(): WorkspaceDocksState {
    let raw: string | null = null;
    if (this.storage !== null) {
      try {
        raw = this.storage.getItem(DOCKS_STORAGE_KEY);
      } catch {
        // Reading can throw if storage is disabled (some privacy modes); treat as "nothing stored".
        raw = null;
      }
    }
    return parseDocksState(raw);
  }

  /** Persist the state; a blocked or full store is swallowed (layout persistence is best-effort, never a
   *  reason to fail a user action like opening a panel). */
  save(state: WorkspaceDocksState): void {
    if (this.storage === null) {
      return;
    }
    try {
      this.storage.setItem(DOCKS_STORAGE_KEY, serializeDocksState(state));
    } catch {
      // Quota exceeded or storage disabled — persistence is best-effort, so drop it silently.
    }
  }
}

/** The real store bound to `window.localStorage`, or a null store if even accessing it throws (a hardened
 *  WebView2 / sandboxed context can make the property access itself raise). */
export function browserDockStore(): DockStore {
  try {
    return new DockStore(window.localStorage);
  } catch {
    // Accessing window.localStorage can throw (not just its methods) when storage is fully disabled.
    return new DockStore(null);
  }
}
