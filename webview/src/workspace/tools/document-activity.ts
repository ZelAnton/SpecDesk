import type { DocumentActivityPayload } from "../../wire/protocol.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export type DocumentActivityKind = "versions" | "comments" | "history";

export class DocumentActivityPanel implements PanelTool {
  readonly icon: string;
  private body: HTMLElement | null = null;
  private payload: DocumentActivityPayload | null = null;
  private requestGeneration = 0;

  constructor(
    readonly id: DocumentActivityKind,
    readonly label: string,
    private readonly requestActivity: () => Promise<DocumentActivityPayload>,
  ) {
    this.icon = icon(id === "comments" ? "comment" : id);
  }

  mount(body: HTMLElement): void {
    this.body = body;
    void this.refresh();
  }

  async refresh(): Promise<void> {
    const generation = ++this.requestGeneration;
    const payload = await this.requestActivity();
    if (generation !== this.requestGeneration) return;
    this.payload = payload;
    this.render();
  }

  private render(): void {
    if (!this.body || !this.payload) return;
    this.body.replaceChildren();
    const root = document.createElement("div");
    root.className = "document-activity";
    const documentName = document.createElement("p");
    documentName.className = "document-activity-name";
    documentName.textContent = this.payload.document ?? "No document selected";
    root.appendChild(documentName);

    const items = this.payload[this.id];
    if (items.length === 0) {
      const empty = document.createElement("p");
      empty.className = "document-activity-empty";
      empty.textContent = this.emptyText();
      root.appendChild(empty);
    } else {
      const list = document.createElement("ol");
      list.className = "document-activity-list";
      for (const item of items) {
        const row = document.createElement("li");
        row.className = "document-activity-item";
        const title = document.createElement("strong");
        title.textContent = "label" in item ? item.label : "note" in item ? item.note : item.body;
        const meta = document.createElement("span");
        const note = this.id === "history" && "note" in item ? ` · ${item.note}` : "";
        meta.textContent = `${item.author}${note} · ${new Date(item.when).toLocaleString()}`;
        row.append(title, meta);
        list.appendChild(row);
      }
      root.appendChild(list);
    }
    this.body.appendChild(root);
  }

  private emptyText(): string {
    if (!this.payload?.document) return "Open a document to see this information.";
    if (this.id === "versions") {
      return this.payload.historyState === "loaded"
        ? "No saved versions for this document yet."
        : (this.payload.historyMessage ?? "Could not load saved history.");
    }
    if (this.id === "comments") {
      return this.payload.commentsState === "loaded"
        ? "No comments on this document."
        : (this.payload.commentsMessage ?? "Could not load comments.");
    }
    return this.payload.historyState === "loaded"
      ? "No saved changes for this document yet."
      : (this.payload.historyMessage ?? "Could not load saved history.");
  }
}
