import { type ActivityEntry, activityStream } from "../activity-stream.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export class ActivityLogPanel implements PanelTool {
  readonly id = "log";
  readonly label = "Log";
  readonly icon = icon("log");
  private root: HTMLElement | null = null;

  mount(body: HTMLElement): void {
    this.root = document.createElement("ol");
    this.root.className = "activity-log";
    this.root.setAttribute("aria-label", "Application activity");
    body.appendChild(this.root);
    activityStream.subscribe((entries) => this.render(entries));
  }

  private render(entries: readonly ActivityEntry[]): void {
    if (this.root === null) return;
    this.root.replaceChildren();
    if (entries.length === 0) {
      const empty = document.createElement("li");
      empty.className = "activity-log-empty";
      empty.textContent = "Actions and GitHub activity will appear here.";
      this.root.appendChild(empty);
      return;
    }
    for (const entry of entries) {
      const row = document.createElement("li");
      row.className = "activity-log-entry";
      if (entry.outcome !== undefined) row.dataset.outcome = entry.outcome;
      const time = document.createElement("time");
      time.dateTime = entry.when.toISOString();
      time.textContent = entry.when.toLocaleTimeString([], {
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
      });
      const category = document.createElement("strong");
      category.textContent = entry.category;
      const text = document.createElement("span");
      text.textContent = entry.message;
      row.append(time, category, text);
      this.root.appendChild(row);
    }
  }
}
