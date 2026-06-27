/**
 * The open/close state machine shared by the inline prompt bars (see dialogs.ts). It guards the
 * suggestion-request window that a plain `!hidden` check cannot: a synchronous `opening` latch stops a
 * second open() from stacking a request while the first is still awaiting, and a generation token lets
 * a reply that lands after a close() (or a superseding open()) recognise it was invalidated, so it
 * never re-reveals a stale bar. Owning this once means the two bars cannot drift apart on the subtle
 * part; the bar-specific reveal (prefill, single-line reset, focus) is the caller's callback.
 */
export class PromptBar {
  private opening = false;
  private openToken = 0;

  constructor(private readonly bar: HTMLElement | null) {}

  /** Whether the bar is currently revealed. */
  get isOpen(): boolean {
    return this.bar !== null && !this.bar.hidden;
  }

  /**
   * Open the bar: fetch the suggestion, then — unless a close() or another open() superseded this one
   * while the request was in flight — run `reveal` with the suggested value. `reveal` does the visible
   * work (prefill the fields, unhide the bar, focus). A no-op if the bar is already opening or open, so
   * repeated triggers never stack requests.
   */
  async open(suggest: () => Promise<string>, reveal: (suggested: string) => void): Promise<void> {
    if (this.opening || this.isOpen) {
      return;
    }
    this.opening = true;
    const token = ++this.openToken;
    try {
      const suggested = await suggest();
      if (token !== this.openToken) {
        return; // closed or superseded while the request was in flight
      }
      reveal(suggested);
    } finally {
      this.opening = false;
    }
  }

  /** Hide the bar and invalidate any in-flight open() so its late reply won't re-reveal a stale bar. */
  close(): void {
    this.openToken++;
    if (this.bar) {
      this.bar.hidden = true;
    }
  }
}
