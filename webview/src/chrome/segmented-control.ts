export interface SegmentedOption<T extends string> {
  el: HTMLButtonElement;
  value: T;
}

/**
 * An ARIA radiogroup over a row of buttons — the toolbar's view switch (design concept §7/§11: the
 * segmented control is a `role=radiogroup`, not independent toggles). Single selection with one tab
 * stop and arrow-key navigation; `aria-checked` and a roving `tabindex` track the choice. A click or
 * an arrow-key activation calls `onSelect`; the owner calls {@link setSelected} to reflect the
 * authoritative selection (so the control mirrors the real state, e.g. after a same-value no-op).
 */
export class SegmentedControl<T extends string> {
  private readonly options: readonly SegmentedOption<T>[];
  private readonly onSelect: (value: T) => void;

  constructor(options: readonly SegmentedOption<T>[], onSelect: (value: T) => void) {
    this.options = options;
    this.onSelect = onSelect;
    for (const option of options) {
      option.el.addEventListener("click", () => onSelect(option.value));
      option.el.addEventListener("keydown", (event) => this.onKeydown(event, option));
    }
  }

  /** Reflect the authoritative selection: `aria-checked` on each radio and the single tab stop. */
  setSelected(value: T): void {
    for (const option of this.options) {
      const checked = option.value === value;
      option.el.setAttribute("aria-checked", String(checked));
      option.el.tabIndex = checked ? 0 : -1;
    }
  }

  /** Enable or disable the whole group — used when the control doesn't apply (e.g. the view-mode switch
   *  while a non-editor central view is shown). Disabled radios drop out of the tab order and can't be
   *  clicked; the `aria-checked` selection is untouched, so re-enabling restores it exactly. */
  setDisabled(disabled: boolean): void {
    for (const option of this.options) {
      option.el.disabled = disabled;
    }
  }

  // Left/Up move to the previous radio, Right/Down to the next (wrapping); the moved-to radio takes
  // focus and is selected, the WAI-ARIA radiogroup keyboard pattern.
  private onKeydown(event: KeyboardEvent, current: SegmentedOption<T>): void {
    const forward = event.key === "ArrowRight" || event.key === "ArrowDown";
    const backward = event.key === "ArrowLeft" || event.key === "ArrowUp";
    if (!forward && !backward) {
      return;
    }
    event.preventDefault();
    const count = this.options.length;
    const index = this.options.indexOf(current);
    const next = this.options[(index + (forward ? 1 : -1) + count) % count];
    if (next !== undefined) {
      next.el.focus();
      this.onSelect(next.value);
    }
  }
}
