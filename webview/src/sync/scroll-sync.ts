/**
 * The scroll-sync driver lock for Split view. The two editable panes couple by source line; when one
 * pane scrolls it top-aligns the other, whose programmatic echo must NOT bounce back and re-drive the
 * first. A short "driver" window lets the actively-scrolled pane stay authoritative; `suppress()` mutes
 * both around a programmatic scroll (edit mirror / mode switch); `drive()` hands authority to one pane
 * without muting it (a caret-move reveal). Pure timing state (no DOM), so it is unit-tested directly —
 * this is the subtlest part of the Split orchestration index.ts used to hold inline.
 */

export type ScrollPane = "editor" | "formatted";

/** The driver/suppress window length. A claim by another pane within this window of the last is ignored. */
export const SCROLL_SYNC_MS = 120;

export class ScrollSync {
  private driver: ScrollPane | "none" = "none";
  private driverUntil = 0;
  private lastSyncAt = 0;

  /**
   * Try to claim `who` as the scroll driver for the next window. Returns `false` when another pane is
   * already driving (or a suppress window is active) — the caller should ignore its scroll as an echo.
   */
  claim(who: ScrollPane): boolean {
    const now = Date.now();
    if (this.driver !== who && now < this.driverUntil) {
      return false;
    }
    this.driver = who;
    this.driverUntil = now + SCROLL_SYNC_MS;
    return true;
  }

  /** Mute both panes for the next window — a programmatic scroll (edit mirror, mode switch) is coming. */
  suppress(): void {
    this.driver = "none";
    this.driverUntil = Date.now() + SCROLL_SYNC_MS;
  }

  /**
   * Make `who` the authoritative driver for the next window WITHOUT muting it. Used when a deliberate
   * caret move in `who` reveals the synced highlight in the other pane: that other pane's programmatic
   * reveal scroll must not echo back and drive `who`, yet `who`'s own scroll must still sync normally.
   */
  drive(who: ScrollPane): void {
    this.driver = who;
    this.driverUntil = Date.now() + SCROLL_SYNC_MS;
  }

  /** Record that a genuine pane scroll just drove a sync (top-aligned the other pane). */
  markSynced(): void {
    this.lastSyncAt = Date.now();
  }

  /**
   * Whether a scroll-driven sync happened within the last window. While true, the passive pane is being
   * positioned by scroll-sync, so a caret-move reveal must stand down — otherwise the two fight over its
   * scrollTop and it judders (most visible holding an arrow key inside a tall table/list).
   */
  syncedRecently(): boolean {
    return Date.now() - this.lastSyncAt < SCROLL_SYNC_MS;
  }
}
