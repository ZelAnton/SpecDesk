/**
 * The document-outline tool (design concept §9): the open document's Markdown headings as a navigable
 * tree in the right rail. Clicking a heading scrolls the editor to it (via the owner's onNavigate). The
 * parser is pure (fenced code blocks excluded, ATX headings only) so it is unit-tested directly; the tool
 * renders and re-renders from {@link Outline.setItems} as the document changes.
 */

import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface OutlineItem {
  /** Heading level 1–6. */
  readonly level: number;
  /** Heading text (trailing ATX `#`s stripped, trimmed; may be empty). */
  readonly text: string;
  /** 0-based source line the heading is on. */
  readonly line: number;
}

/**
 * Parse ATX Markdown headings (`#`–`######` followed by a space) into an outline, skipping any inside a
 * fenced code block (``` or ~~~) so a `#` comment in a code sample is never mistaken for a heading.
 */
export function parseOutline(text: string): OutlineItem[] {
  const items: OutlineItem[] = [];
  const lines = text.split("\n");
  // Skip a leading YAML front-matter block (`---` … `---`) so a `#` comment inside it isn't read as an H1.
  let start = 0;
  if ((lines[0] ?? "").trim() === "---") {
    for (let j = 1; j < lines.length; j += 1) {
      if ((lines[j] ?? "").trim() === "---") {
        start = j + 1;
        break;
      }
    }
  }
  let fence: string | null = null; // the run of ` or ~ that opened the current fence, or null
  for (let i = start; i < lines.length; i += 1) {
    const line = lines[i] ?? "";
    const fenceMatch = /^ {0,3}(`{3,}|~{3,})/.exec(line);
    if (fenceMatch !== null) {
      const marker = fenceMatch[1] ?? "";
      if (fence === null) {
        fence = marker[0] ?? "";
      } else if (fence === (marker[0] ?? "")) {
        // A closing fence must use the same character (length is not re-checked — close enough here).
        fence = null;
      }
      continue;
    }
    if (fence !== null) {
      continue;
    }
    const heading = /^ {0,3}(#{1,6})(?:\s+(.*?))?\s*$/.exec(line);
    if (heading !== null) {
      const level = (heading[1] ?? "").length;
      const raw = heading[2] ?? "";
      items.push({ level, text: raw.replace(/\s+#+$/, "").trim(), line: i });
    }
  }
  return items;
}

/** Value-equality over two outline lists (level, text and line) — used to skip a no-op re-render. */
function sameItems(a: readonly OutlineItem[], b: readonly OutlineItem[]): boolean {
  if (a.length !== b.length) {
    return false;
  }
  return a.every((item, i) => {
    const other = b[i];
    return (
      other !== undefined &&
      item.level === other.level &&
      item.text === other.text &&
      item.line === other.line
    );
  });
}

export class Outline implements PanelTool {
  readonly id = "outline";
  readonly label = "Outline";
  readonly icon = icon("outline");
  private listEl: HTMLElement | null = null;
  private emptyEl: HTMLElement | null = null;
  private items: readonly OutlineItem[] = [];

  /** @param onNavigate called with the 0-based source line to scroll to when a heading is clicked. */
  constructor(private readonly onNavigate: (line: number) => void) {}

  mount(body: HTMLElement): void {
    const empty = document.createElement("p");
    empty.className = "outline-empty";
    empty.textContent = "The document's headings will appear here.";

    const list = document.createElement("nav");
    list.className = "outline-list";
    list.setAttribute("aria-label", "Document outline");

    body.append(empty, list);
    this.emptyEl = empty;
    this.listEl = list;
    this.render();
  }

  /** Replace the outline with `items` (called as the document changes). */
  setItems(items: readonly OutlineItem[]): void {
    // The document text changes on every keystroke, but the heading set usually does not — skip the DOM
    // rebuild (replaceChildren + a fresh <li>/listener per heading) when nothing changed, so typing inside a
    // paragraph costs nothing here.
    if (sameItems(this.items, items)) {
      return;
    }
    this.items = items;
    this.render();
  }

  private render(): void {
    if (this.listEl === null || this.emptyEl === null) {
      return;
    }
    const hasItems = this.items.length > 0;
    this.emptyEl.hidden = hasItems;
    this.listEl.hidden = !hasItems;
    this.listEl.replaceChildren();
    if (!hasItems) {
      return;
    }
    // Nested <ul>/<li> so the heading hierarchy is programmatic (not indent-and-bold alone) — a screen
    // reader announces the nesting. Each item opens a child list for its (possibly absent) sub-headings; the
    // empty ones are dropped afterward. Nesting is by RELATIVE order (an h3 after an h1 nests one level), so
    // a skipped level doesn't leave a gap.
    const root = document.createElement("ul");
    root.className = "outline-tree";
    const stack: { level: number; list: HTMLUListElement }[] = [{ level: 0, list: root }];
    for (const item of this.items) {
      while (stack.length > 1 && (stack.at(-1)?.level ?? 0) >= item.level) {
        stack.pop();
      }
      const parent = stack.at(-1)?.list ?? root;
      const li = document.createElement("li");
      const button = document.createElement("button");
      button.type = "button";
      button.className = "outline-item";
      button.dataset.level = String(item.level);
      button.textContent = item.text.length > 0 ? item.text : "(untitled)";
      // The rail truncates long headings with an ellipsis; a title lets a mouse user read the full text.
      if (item.text.length > 0) {
        button.title = item.text;
      }
      button.addEventListener("click", () => this.onNavigate(item.line));
      li.appendChild(button);
      parent.appendChild(li);
      const childList = document.createElement("ul");
      childList.className = "outline-tree";
      li.appendChild(childList);
      stack.push({ level: item.level, list: childList });
    }
    for (const list of Array.from(root.querySelectorAll("ul"))) {
      if (list.children.length === 0) {
        list.remove();
      }
    }
    this.listEl.appendChild(root);
  }
}
