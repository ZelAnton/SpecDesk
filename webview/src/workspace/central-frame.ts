/**
 * The central-frame host: the swappable content region between the collapsible rails (design concept §9).
 * The editor view (`#editor-view` — the formatting toolbar over the panes) is the primary view; the
 * left-rail navigation substitutes the centre with another registered view (e.g. the Start screen) by
 * calling {@link CentralFrame.show}. Only one view is visible at a time; the rest stay in the DOM but
 * hidden, so each keeps its own scroll/caret/state — the same "hide, never destroy" policy the view-mode
 * panes use.
 *
 * Visibility is driven by a class, not an inline style: the active view carries `central-view--active`, and
 * the stylesheet hides every `.central-view` without it. The hide rule is written at `#central-frame` (id) +
 * two classes so its specificity (1,2,0) exceeds an active view's own id `display: flex` rule (e.g.
 * `#editor-view`, 1,0,0) — an id-level rule a single class could not override. That hide rule uses a CHILD
 * combinator, so each view element must be a direct child of the host frame (see {@link CentralView.el}).
 * The host element also carries the active view's id as `data-view`, a stable hook for CSS and for "is the
 * editor showing?" checks elsewhere.
 */

/** The base class every central view element carries; the stylesheet hides those lacking the active flag. */
export const CENTRAL_VIEW_CLASS = "central-view";
/** The flag on the single visible view element (see the stylesheet's hide rule and the class comment). */
export const CENTRAL_VIEW_ACTIVE_CLASS = "central-view--active";
/** The id of the primary view — the editor view (#editor-view). Kept here so markup, CSS, and wiring agree. */
export const CENTRAL_VIEW_EDITOR = "editor";

export interface CentralView {
  /** Stable identifier; also written to the host's `data-view` attribute while this view is shown. */
  readonly id: string;
  /** This view's root element — a DIRECT child of the host frame (e.g. #editor-view, #home-view). Shown when
   *  active, hidden otherwise; the stylesheet's child-combinator hide rule only reaches direct children. */
  readonly el: HTMLElement;
  /** Ran after this view becomes the active one (its element revealed, geometry now measurable). */
  readonly onShow?: () => void;
  /** Ran after this view stops being the active one (its element hidden). */
  readonly onHide?: () => void;
}

export class CentralFrame {
  private readonly views = new Map<string, CentralView>();
  private activeId: string | null;

  /**
   * @param host the element wrapping every view; its `data-view` seeds and then tracks the active id.
   * @param onChange notified with the new view id after each actual switch (not a no-op re-show) — the
   *   owner uses it to reflect the active destination in the navigator and to gate editor-only chrome.
   */
  constructor(
    private readonly host: HTMLElement,
    private readonly onChange?: (id: string) => void,
  ) {
    // Adopt whatever the markup declares as the starting view (see index.html) so a freshly registered
    // view that is already active is not toggled off and back on — the initial paint stays flash-free.
    // Precondition: `data-view` must name a view registered at startup. If it names none, every register()
    // clears its element's active flag (id !== activeId) and the CSS then hides all views until a later
    // show() — so the markup's data-view and the wiring's registrations are two co-located sources that
    // must agree.
    this.activeId = host.dataset.view ?? null;
  }

  /**
   * Register a view. Its element gains the base class and is flagged active only if it is the id the frame
   * currently shows — so registering the alternate views at startup hides them behind the editor without a
   * flash, and registering the active view (the editor) leaves it visible. Registering an id twice replaces
   * the record; the latest element wins.
   */
  register(view: CentralView): void {
    // Replacing an id with a DIFFERENT element strips both classes off the superseded one, so it stops
    // being a (possibly still-active) view — otherwise re-registering the active view's element would
    // leave two elements flagged active and the CSS would show both at once.
    const existing = this.views.get(view.id);
    if (existing !== undefined && existing.el !== view.el) {
      existing.el.classList.remove(CENTRAL_VIEW_CLASS, CENTRAL_VIEW_ACTIVE_CLASS);
    }
    this.views.set(view.id, view);
    view.el.classList.add(CENTRAL_VIEW_CLASS);
    view.el.classList.toggle(CENTRAL_VIEW_ACTIVE_CLASS, view.id === this.activeId);
  }

  /** Whether a view with this id has been registered. */
  has(id: string): boolean {
    return this.views.has(id);
  }

  /** The id of the currently shown view, or `null` before the first view is shown. */
  active(): string | null {
    return this.activeId;
  }

  /**
   * Show the view with `id`, hiding whichever was shown before and running the two views' lifecycle hooks
   * (previous {@link CentralView.onHide}, then new {@link CentralView.onShow}) after the swap, so `onShow`
   * runs with its element already visible and measurable. A no-op — no class changes, no hooks — when `id`
   * is already active, so a redundant call can't re-fire side effects. An unregistered id is ignored (a
   * navigation destination wired before its view exists must not crash the frame).
   */
  show(id: string): void {
    if (id === this.activeId) {
      return;
    }
    const next = this.views.get(id);
    if (next === undefined) {
      return;
    }
    const previous = this.activeId !== null ? this.views.get(this.activeId) : undefined;
    previous?.el.classList.remove(CENTRAL_VIEW_ACTIVE_CLASS);
    next.el.classList.add(CENTRAL_VIEW_ACTIVE_CLASS);
    this.host.dataset.view = id;
    this.activeId = id;
    previous?.onHide?.();
    next.onShow?.();
    // After the swap and the views' own hooks, notify the owner so it can reflect the new active view
    // (navigator highlight, editor-only chrome gating). Fires only on a real switch — the early return
    // above means a redundant show can't re-fire it.
    this.onChange?.(id);
  }
}
