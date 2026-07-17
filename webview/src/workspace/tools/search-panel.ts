/**
 * The left-rail Search mode (T-078): a bounded host-side search across the active workspace's Markdown
 * files (docs/design/09-ipc-protocol.md, search.request/search.results) — distinct from the toolbar's
 * in-document search (webview/src/index.ts), which only ever looks at the open document. A submitted query
 * asks the host once (correlated by envelope id, the same request/reply shape as the review-list panels);
 * results are flat file:line rows with a text snippet. Clicking a row asks the integrator to open that
 * document at that line (see SearchCallbacks.onOpenResult).
 */

import type { SearchResultPayload, SearchResultsPayload } from "../../wire/protocol.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface SearchCallbacks {
  /** Ask the host to search the active workspace's Markdown files for `query`. */
  request(query: string): Promise<SearchResultsPayload>;
  /** Open one result: the absolute file path and its 0-based line (the match's 1-based line, converted). */
  onOpenResult(path: string, line: number): void;
}

/** The left-rail Search mode: an input, a status line, and a flat list of file:line snippet results. */
export class SearchPanel implements PanelTool {
  readonly id = "search";
  readonly label = "Search";
  readonly icon = icon("search");

  private root: HTMLElement | null = null;
  private input: HTMLInputElement | null = null;
  private status: HTMLElement | null = null;
  private list: HTMLElement | null = null;
  private generation = 0;

  constructor(private readonly callbacks: SearchCallbacks) {}

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "search-panel";

    const form = document.createElement("form");
    form.className = "search-panel-form";
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      void this.runSearch();
    });

    const input = document.createElement("input");
    input.type = "search";
    input.className = "search-panel-input";
    input.placeholder = "Search specs in this workspace";
    input.setAttribute("aria-label", "Search specs in this workspace");
    input.autocomplete = "off";
    form.appendChild(input);

    const status = document.createElement("p");
    status.className = "search-panel-status";
    status.setAttribute("role", "status");

    const list = document.createElement("ul");
    list.className = "search-panel-results";
    list.setAttribute("aria-label", "Search results");

    root.append(form, status, list);
    body.appendChild(root);
    this.root = root;
    this.input = input;
    this.status = status;
    this.list = list;
    this.setState("idle", "Search the Markdown files in the current workspace.");
  }

  focusPrimary(): void {
    this.input?.focus();
  }

  private async runSearch(): Promise<void> {
    const query = (this.input?.value ?? "").trim();
    if (query.length === 0) {
      this.generation++;
      this.list?.replaceChildren();
      this.setState("idle", "Enter text to search the current workspace.");
      return;
    }
    const request = ++this.generation;
    this.setState("loading", `Searching for "${query}"…`);
    this.list?.replaceChildren();
    try {
      const payload = await this.callbacks.request(query);
      if (request !== this.generation) return;
      this.render(payload);
    } catch {
      if (request === this.generation) {
        this.setState("error", "Could not search the workspace. Try again.");
      }
    }
  }

  private render(payload: SearchResultsPayload): void {
    this.list?.replaceChildren();
    if (payload.results.length === 0) {
      this.setState("empty", `No matches for "${payload.query}".`);
      return;
    }
    const count = `${payload.results.length} match${payload.results.length === 1 ? "" : "es"}`;
    const suffix = payload.truncated ? " — showing the first matches" : "";
    this.setState("ready", `${count} for "${payload.query}"${suffix}`);
    for (const result of payload.results) {
      this.list?.appendChild(this.row(result));
    }
  }

  private row(result: SearchResultPayload): HTMLLIElement {
    const li = document.createElement("li");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "search-panel-result";
    const file = document.createElement("span");
    file.className = "search-panel-result-file";
    file.textContent = `${fileName(result.path)}:${result.line}`;
    file.title = result.path;
    const snippet = document.createElement("span");
    snippet.className = "search-panel-result-snippet";
    snippet.textContent = result.snippet;
    button.append(file, snippet);
    // The host reports the 1-based file line; the editor's scroll-to-line coordinate is 0-based.
    button.addEventListener("click", () =>
      this.callbacks.onOpenResult(result.path, Math.max(0, result.line - 1)),
    );
    li.appendChild(button);
    return li;
  }

  private setState(state: string, message: string): void {
    if (this.root !== null) {
      this.root.dataset.state = state;
    }
    if (this.status !== null) {
      this.status.textContent = message;
      this.status.hidden = message.length === 0;
    }
  }
}

function fileName(path: string): string {
  const trimmed = path.replace(/[/\\]+$/, "");
  const segments = trimmed.split(/[/\\]/);
  return segments[segments.length - 1] || trimmed;
}
