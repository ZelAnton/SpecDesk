/**
 * Measured, reusable overflow for a single-row toolbar. The real controls stay in the toolbar so their
 * existing command wiring and fieldset semantics remain authoritative; menu entries are short-lived proxies
 * that mirror each overflowed control's label, enabled/checked state, and click behavior.
 */

export interface ToolbarOverflowOptions {
  /** Controls in visual/command order. Hidden controls are ignored until they become visible again. */
  readonly controls: readonly HTMLButtonElement[];
  readonly label?: string;
}

/** Return the first index moved into overflow. Commands always leave the row from the trailing edge. */
export function firstOverflowIndex(
  widths: readonly number[],
  availableWidth: number,
  gap: number,
): number {
  if (widths.length === 0) return 0;
  let used = 0;
  for (let index = 0; index < widths.length; index += 1) {
    used += widths[index] ?? 0;
    if (index > 0) used += gap;
    if (used > availableWidth) return index;
  }
  return widths.length;
}

function visible(control: HTMLButtonElement): boolean {
  return !control.hidden && control.getAttribute("aria-hidden") !== "true";
}

function labelFor(control: HTMLButtonElement): string {
  return (
    control.getAttribute("aria-label")?.trim() ||
    control.title.trim() ||
    control.textContent?.trim() ||
    "Command"
  );
}

export class ToolbarOverflow {
  private readonly trigger: HTMLButtonElement;
  private readonly menu: HTMLElement;
  private readonly observer: ResizeObserver | null;
  private readonly mutations: MutationObserver;
  private frame = 0;
  private disposed = false;

  constructor(
    private readonly root: HTMLElement,
    private readonly options: ToolbarOverflowOptions,
  ) {
    this.root.classList.add("toolbar-overflow-root");
    this.root.setAttribute("data-toolbar-overflow", "ready");

    this.trigger = document.createElement("button");
    this.trigger.type = "button";
    this.trigger.className = "toolbar-overflow-trigger";
    this.trigger.setAttribute("aria-label", "More toolbar commands");
    this.trigger.setAttribute("aria-haspopup", "menu");
    this.trigger.setAttribute("aria-expanded", "false");
    this.trigger.title = "More toolbar commands";
    this.trigger.innerHTML =
      '<svg viewBox="0 0 16 16" focusable="false" aria-hidden="true"><circle cx="3" cy="8" r="1.25"/><circle cx="8" cy="8" r="1.25"/><circle cx="13" cy="8" r="1.25"/></svg>';
    this.trigger.hidden = true;

    this.menu = document.createElement("div");
    this.menu.className = "toolbar-overflow-menu";
    this.menu.setAttribute("role", "menu");
    this.menu.setAttribute("aria-label", options.label ?? "More toolbar commands");
    this.menu.hidden = true;
    this.root.append(this.trigger, this.menu);

    this.trigger.addEventListener("click", () =>
      this.setMenuOpen(this.menu.hasAttribute("hidden")),
    );
    this.trigger.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        this.setMenuOpen(true, true);
      }
    });
    this.menu.addEventListener("keydown", (event) => this.onMenuKeyDown(event));
    document.addEventListener("pointerdown", this.onDocumentPointerDown, true);
    document.addEventListener("keydown", this.onDocumentKeyDown, true);

    this.mutations = new MutationObserver(() => this.schedule());
    for (const control of options.controls) {
      this.mutations.observe(control, {
        attributes: true,
        childList: true,
        subtree: true,
        attributeFilter: [
          "hidden",
          "disabled",
          "aria-hidden",
          "aria-checked",
          "aria-pressed",
          "aria-label",
          "title",
        ],
      });
    }
    for (const fieldset of new Set(
      options.controls.map((control) => control.closest("fieldset")),
    )) {
      if (fieldset !== null) {
        this.mutations.observe(fieldset, {
          attributes: true,
          attributeFilter: ["disabled", "hidden"],
        });
      }
    }
    this.mutations.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["class", "style", "data-theme"],
    });
    this.observer =
      typeof ResizeObserver === "undefined" ? null : new ResizeObserver(() => this.schedule());
    this.observer?.observe(root);
    window.addEventListener("resize", this.schedule);
    window.visualViewport?.addEventListener("resize", this.schedule);
    document.fonts?.addEventListener("loadingdone", this.schedule);
    void document.fonts?.ready.then(() => this.schedule());
    this.schedule();
  }

  /** Recalculate after lifecycle or mode code changes several controls in the same turn. */
  refresh(): void {
    this.schedule();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frame);
    this.observer?.disconnect();
    this.mutations.disconnect();
    window.removeEventListener("resize", this.schedule);
    window.visualViewport?.removeEventListener("resize", this.schedule);
    document.fonts?.removeEventListener("loadingdone", this.schedule);
    document.removeEventListener("pointerdown", this.onDocumentPointerDown, true);
    document.removeEventListener("keydown", this.onDocumentKeyDown, true);
    for (const control of this.options.controls) control.classList.remove("toolbar-overflowed");
    this.trigger.remove();
    this.menu.remove();
  }

  private readonly schedule = (): void => {
    if (this.disposed || this.frame !== 0) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.measure();
    });
  };

  private measure(): void {
    const controls = this.options.controls.filter(visible);
    for (const control of this.options.controls) control.classList.remove("toolbar-overflowed");
    this.trigger.hidden = true;
    // scrollWidth observes the browser's real layout, including nested segmented controls, padding, gaps,
    // zoom, and loaded fonts. It is therefore more exact than adding nominal button widths ourselves.
    if (this.root.scrollWidth <= this.root.clientWidth + 1) {
      this.trigger.hidden = true;
      this.setMenuOpen(false);
      this.renderMenu([]);
      return;
    }
    this.trigger.hidden = false;
    const overflowed: HTMLButtonElement[] = [];
    for (let index = controls.length - 1; index >= 0; index -= 1) {
      if (this.root.scrollWidth <= this.root.clientWidth + 1) break;
      const control = controls[index];
      if (control === undefined) continue;
      control.classList.add("toolbar-overflowed");
      overflowed.unshift(control);
    }
    this.renderMenu(overflowed);
  }

  private renderMenu(controls: readonly HTMLButtonElement[]): void {
    this.menu.replaceChildren();
    for (const control of controls) {
      const proxy = document.createElement("button");
      proxy.type = "button";
      proxy.className = "toolbar-overflow-item";
      const pressed = control.getAttribute("aria-pressed");
      const checked = control.getAttribute("aria-checked");
      if (checked !== null) {
        proxy.setAttribute("role", "menuitemradio");
        proxy.setAttribute("aria-checked", checked);
      } else if (pressed !== null) {
        proxy.setAttribute("role", "menuitemcheckbox");
        proxy.setAttribute("aria-checked", pressed);
      } else {
        proxy.setAttribute("role", "menuitem");
      }
      proxy.textContent = labelFor(control);
      proxy.disabled = control.matches(":disabled");
      proxy.title = control.title;
      proxy.addEventListener("click", () => {
        control.click();
        this.setMenuOpen(false);
        this.trigger.focus();
        this.schedule();
      });
      this.menu.appendChild(proxy);
    }
  }

  private setMenuOpen(open: boolean, focusFirst = false): void {
    if (open && this.trigger.hidden) return;
    this.menu.hidden = !open;
    this.trigger.setAttribute("aria-expanded", String(open));
    if (open && focusFirst) this.menuItems()[0]?.focus();
  }

  private menuItems(): HTMLButtonElement[] {
    return Array.from(this.menu.querySelectorAll<HTMLButtonElement>("button:not(:disabled)"));
  }

  private onMenuKeyDown(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      event.preventDefault();
      this.setMenuOpen(false);
      this.trigger.focus();
      return;
    }
    if (event.key === "Tab") {
      this.setMenuOpen(false);
      return;
    }
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    const items = this.menuItems();
    if (items.length === 0) return;
    event.preventDefault();
    const current = items.indexOf(document.activeElement as HTMLButtonElement);
    const next =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? items.length - 1
          : current < 0
            ? 0
            : (current + (event.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
    items[next]?.focus();
  }

  private readonly onDocumentPointerDown = (event: PointerEvent): void => {
    if (event.target instanceof Node && !this.root.contains(event.target)) this.setMenuOpen(false);
  };

  private readonly onDocumentKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape" && !this.menu.hidden) {
      event.preventDefault();
      this.setMenuOpen(false);
      this.trigger.focus();
    }
  };
}
