import type { Page } from "@playwright/test";

// Selectors for the Split panes' real DOM (same seams the jsdom delivery harness uses).
const FORMATTED_PANE = "#formatted"; // the formatted pane's scroll container
const FORMATTED_CONTENT = "#formatted .ProseMirror";
const CODE_SCROLLER = "#editor .cm-scroller";
const SPACER = "#editor .cm-sync-spacer";
const CODE_LINE = "#editor .cm-line";

/** A leaf to locate in the formatted pane, by tag + a substring of its text. */
export interface LeafSpec {
  label: string;
  tag: string;
  needle: string;
}

export interface SpacerInfo {
  /** The pixel height height-sync wrote as `style.height`. */
  styleHeight: number;
  /** The height the browser actually laid the spacer out at (real geometry). */
  renderedHeight: number;
}

export interface GeometryDump {
  mode: string | null;
  title: string;
  panes: {
    editor: { scrollTop: number; rect: RectLite } | null;
    formatted: { scrollTop: number; rect: RectLite } | null;
  };
  spacers: Array<SpacerInfo & { top: number }>;
  anchors: Array<{ label: string; formattedTop: number }>;
}

interface RectLite {
  top: number;
  left: number;
  width: number;
  height: number;
}

/** The editors' scroll-settle debounce (SCROLL_SETTLE_MS in editor.ts / formatted.ts): a real scroll
 *  arms it on every event and it fires this long after the LAST one, re-coupling from the final resting
 *  position. A faithful "the author scrolled and paused" gesture is not over until it fires. */
const SCROLL_SETTLE_MS = 120;

/**
 * Resolve once the panes' geometry is STABLE: the spacer set, both scroll positions and the spacer
 * heights are identical across two consecutive animation frames. This is the fixed-point drain the
 * jsdom harness does with `flushFrames`, expressed as a real-browser rAF poll — the settle A-T1's
 * `loadDoc` deliberately does not do (it waits only for mount). Use after a load or a scroll before
 * probing geometry.
 *
 * Requires at least one spacer, so it can't accept a "settled" reading BEFORE the first reconcile has
 * applied spacers (two empty frames at mount would otherwise settle on pre-spacer geometry). Every
 * geometry scenario loads a spacer-producing document, so this holds; a 0-spacer doc would time out
 * (loudly) rather than mis-settle. The signature store is reset per call so each settle is
 * self-contained and can't false-positive against a prior call's stale-but-equal value.
 */
export async function waitForGeometrySettle(page: Page): Promise<void> {
  await page.evaluate(() => {
    delete (window as unknown as { __sd_geoSig?: string }).__sd_geoSig;
  });
  await page.waitForFunction(
    () => {
      const spacers = Array.from(document.querySelectorAll("#editor .cm-sync-spacer"));
      if (spacers.length === 0) {
        return false;
      }
      const editor = document.querySelector("#editor .cm-scroller") as HTMLElement | null;
      const formatted = document.querySelector("#formatted") as HTMLElement | null;
      const signature = JSON.stringify({
        n: spacers.length,
        h: spacers.map((s) => (s as HTMLElement).style.height),
        es: editor ? Math.round(editor.scrollTop) : null,
        fs: formatted ? Math.round(formatted.scrollTop) : null,
      });
      const store = window as unknown as { __sd_geoSig?: string };
      const stable = store.__sd_geoSig === signature;
      store.__sd_geoSig = signature;
      return stable;
    },
    undefined,
    { polling: "raf", timeout: 10_000 },
  );
}

/**
 * After a scroll: drain the timer-based scroll-settle debounce (plus a margin), THEN settle geometry —
 * so a probe never reads a position the late `SplitSync.settle` re-couple is about to move (the "late
 * pane-settle" class the repo fixed in a35346c). The jsdom gate drains the same debounce in `scrollPane`.
 */
export async function waitForScrollSettle(page: Page): Promise<void> {
  await page.waitForTimeout(SCROLL_SETTLE_MS + 60);
  await waitForGeometrySettle(page);
}

/** The `.cm-sync-spacer` widgets height-sync applied, with both their declared and rendered heights. */
export function spacerReport(page: Page): Promise<SpacerInfo[]> {
  return page.evaluate((sel) =>
    Array.from(document.querySelectorAll(sel)).map((el) => ({
      styleHeight: Number.parseFloat((el as HTMLElement).style.height) || 0,
      renderedHeight: el.getBoundingClientRect().height,
    })),
    SPACER,
  );
}

/** The content-relative top (px from the formatted pane's scroll origin) of each named leaf. */
export function formattedAnchorTops(
  page: Page,
  specs: LeafSpec[],
): Promise<Record<string, number>> {
  return page.evaluate(
    ({ specList, paneSel, contentSel }) => {
      const pane = document.querySelector(paneSel) as HTMLElement | null;
      const content = document.querySelector(contentSel);
      if (!pane || !content) {
        return {};
      }
      const paneTop = pane.getBoundingClientRect().top;
      const scrollTop = pane.scrollTop;
      const out: Record<string, number> = {};
      for (const spec of specList) {
        const el = Array.from(content.querySelectorAll(spec.tag)).find((e) =>
          (e.textContent ?? "").includes(spec.needle),
        );
        if (el) {
          out[spec.label] = el.getBoundingClientRect().top - paneTop + scrollTop;
        }
      }
      return out;
    },
    { specList: specs, paneSel: FORMATTED_PANE, contentSel: FORMATTED_CONTENT },
  );
}

/** Scroll the formatted pane so `top` (a content-relative coordinate) sits at its viewport top. */
export async function scrollFormattedTo(page: Page, top: number): Promise<void> {
  await page.evaluate(
    ({ t, sel }) => {
      const pane = document.querySelector(sel) as HTMLElement | null;
      if (pane) {
        pane.scrollTop = t;
        pane.dispatchEvent(new Event("scroll"));
      }
    },
    { t: top, sel: FORMATTED_PANE },
  );
}

/** Scroll the code editor so `top` sits at its viewport top. */
export async function scrollCodeTo(page: Page, top: number): Promise<void> {
  await page.evaluate(
    ({ t, sel }) => {
      const sc = document.querySelector(sel) as HTMLElement | null;
      if (sc) {
        sc.scrollTop = t;
        sc.dispatchEvent(new Event("scroll"));
      }
    },
    { t: top, sel: CODE_SCROLLER },
  );
}

/** A leaf identified in the formatted pane paired with the source line it renders from. */
export interface AlignSpec {
  label: string;
  tag: string;
  needle: string;
  srcLine: string;
}

/**
 * The vertical alignment of a rendered block against its source line, each measured RELATIVE to its
 * own pane's top (so a constant difference in the two panes' screen positions or top insets cancels).
 * height-sync's whole promise is that these two match; the diff is the real-geometry alignment error.
 * Returns null if either the leaf or its source line is not currently in the DOM.
 */
export function measureAlignment(
  page: Page,
  spec: AlignSpec,
): Promise<{ formattedRel: number; codeRel: number } | null> {
  return page.evaluate(
    ({ s, paneSel, contentSel, codeSel, lineSel }) => {
      const pane = document.querySelector(paneSel);
      const content = document.querySelector(contentSel);
      const scroller = document.querySelector(codeSel);
      if (!pane || !content || !scroller) {
        return null;
      }
      const leaf = Array.from(content.querySelectorAll(s.tag)).find((e) =>
        (e.textContent ?? "").includes(s.needle),
      );
      const line = Array.from(document.querySelectorAll(lineSel)).find(
        (l) => (l.textContent ?? "") === s.srcLine,
      );
      if (!leaf || !line) {
        return null;
      }
      return {
        formattedRel: leaf.getBoundingClientRect().top - pane.getBoundingClientRect().top,
        codeRel: line.getBoundingClientRect().top - scroller.getBoundingClientRect().top,
      };
    },
    { s: spec, paneSel: FORMATTED_PANE, contentSel: FORMATTED_CONTENT, codeSel: CODE_SCROLLER, lineSel: CODE_LINE },
  );
}

/** The content-relative top (px from the code editor's scroll origin) of a source line, for scrolling
 *  the code pane to it. Null if the line is not currently rendered (CodeMirror virtualises). */
export function codeLineTop(page: Page, srcLine: string): Promise<number | null> {
  return page.evaluate(
    ({ line, codeSel, lineSel }) => {
      const scroller = document.querySelector(codeSel) as HTMLElement | null;
      const el = Array.from(document.querySelectorAll(lineSel)).find(
        (l) => (l.textContent ?? "") === line,
      );
      if (!scroller || !el) {
        return null;
      }
      return el.getBoundingClientRect().top - scroller.getBoundingClientRect().top + scroller.scrollTop;
    },
    { line: srcLine, codeSel: CODE_SCROLLER, lineSel: CODE_LINE },
  );
}

/** Both panes' current scroll offsets. */
export function scrollTops(page: Page): Promise<{ editor: number; formatted: number }> {
  return page.evaluate(
    ({ codeSel, paneSel }) => ({
      editor: (document.querySelector(codeSel) as HTMLElement | null)?.scrollTop ?? 0,
      formatted: (document.querySelector(paneSel) as HTMLElement | null)?.scrollTop ?? 0,
    }),
    { codeSel: CODE_SCROLLER, paneSel: FORMATTED_PANE },
  );
}

/** The formatted pane's parent vs nested list-item left edges (real rendered geometry). */
export function formattedListIndent(
  page: Page,
): Promise<{ parentLeft: number; nestedLeft: number } | null> {
  return page.evaluate((sel) => {
    const content = document.querySelector(sel);
    if (!content) {
      return null;
    }
    const items = Array.from(content.querySelectorAll("li"));
    const nested = items.find((li) => li.parentElement?.closest("li") != null) ?? null;
    const parent = nested?.parentElement?.closest("li") ?? null;
    if (!nested || !parent) {
      return null;
    }
    return {
      parentLeft: parent.getBoundingClientRect().left,
      nestedLeft: nested.getBoundingClientRect().left,
    };
  }, FORMATTED_CONTENT);
}

/** The code pane's parent vs nested source-line content left edges (the x of the first non-blank glyph). */
export function sourceListIndent(
  page: Page,
  parentLine: string,
  nestedLine: string,
): Promise<{ parentLeft: number; nestedLeft: number } | null> {
  return page.evaluate(
    ({ lineSel, parent, nested }) => {
      const lines = Array.from(document.querySelectorAll(lineSel));
      const firstGlyphLeft = (predicate: (text: string) => boolean): number | null => {
        const line = lines.find((l) => predicate(l.textContent ?? ""));
        if (!line) {
          return null;
        }
        const text = line.textContent ?? "";
        const idx = text.search(/\S/);
        if (idx < 0) {
          return null;
        }
        const walker = document.createTreeWalker(line, NodeFilter.SHOW_TEXT);
        let acc = 0;
        let node = walker.nextNode();
        while (node) {
          const len = node.textContent?.length ?? 0;
          if (acc + len > idx) {
            const range = document.createRange();
            range.setStart(node, idx - acc);
            range.setEnd(node, idx - acc + 1);
            return range.getBoundingClientRect().left;
          }
          acc += len;
          node = walker.nextNode();
        }
        return null;
      };
      const parentLeft = firstGlyphLeft((t) => t === parent);
      const nestedLeft = firstGlyphLeft((t) => t === nested);
      if (parentLeft === null || nestedLeft === null) {
        return null;
      }
      return { parentLeft, nestedLeft };
    },
    { lineSel: CODE_LINE, parent: parentLine, nested: nestedLine },
  );
}

/** A comprehensive geometry snapshot for the failure-artifact bundle. */
export function collectGeometry(page: Page): Promise<GeometryDump> {
  return page.evaluate(
    ({ codeSel, paneSel, contentSel, spacerSel }) => {
      const rectLite = (el: Element): RectLite => {
        const r = el.getBoundingClientRect();
        return { top: r.top, left: r.left, width: r.width, height: r.height };
      };
      const paneOf = (sel: string) => {
        const el = document.querySelector(sel) as HTMLElement | null;
        return el ? { scrollTop: el.scrollTop, rect: rectLite(el) } : null;
      };
      const spacers = Array.from(document.querySelectorAll(spacerSel)).map((el) => {
        const r = el.getBoundingClientRect();
        return {
          styleHeight: Number.parseFloat((el as HTMLElement).style.height) || 0,
          renderedHeight: r.height,
          top: r.top,
        };
      });
      const pane = document.querySelector(paneSel) as HTMLElement | null;
      const content = document.querySelector(contentSel);
      const anchors: Array<{ label: string; formattedTop: number }> = [];
      if (pane && content) {
        const paneTop = pane.getBoundingClientRect().top;
        const scroll = pane.scrollTop;
        // Leaf projection matches the semantic anchors sync-anchors.ts projects (table→each row,
        // list→each item) and harness.ts's formattedLeaves — keep the three in step.
        const leaves: Element[] = [];
        for (const child of Array.from(content.children)) {
          if (child.tagName === "TABLE") {
            leaves.push(...Array.from(child.querySelectorAll("tr")));
          } else if (child.tagName === "UL" || child.tagName === "OL") {
            leaves.push(...Array.from(child.querySelectorAll("li")));
          } else {
            leaves.push(child);
          }
        }
        leaves.forEach((leaf, i) => {
          anchors.push({
            label: `${leaf.tagName.toLowerCase()}#${i}:${(leaf.textContent ?? "").slice(0, 24)}`,
            formattedTop: leaf.getBoundingClientRect().top - paneTop + scroll,
          });
        });
      }
      const mode =
        (document.querySelector("#panes") as HTMLElement | null)?.getAttribute("data-mode") ?? null;
      return {
        mode,
        title: document.title,
        panes: { editor: paneOf(codeSel), formatted: paneOf(paneSel) },
        spacers,
        anchors,
      };
    },
    {
      codeSel: CODE_SCROLLER,
      paneSel: FORMATTED_PANE,
      contentSel: FORMATTED_CONTENT,
      spacerSel: SPACER,
    },
  );
}
