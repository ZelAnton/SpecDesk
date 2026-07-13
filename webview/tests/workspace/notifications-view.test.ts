// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { buildNotificationsView } from "../../src/workspace/tools/notifications-view.js";

describe("notifications central view", () => {
  it("renders an accessible stub list without inventing live notification data", () => {
    const host = document.createElement("div");
    buildNotificationsView(host);

    expect(host.querySelector("h1")?.textContent).toBe("Notifications");
    expect(host.querySelector("ul")?.getAttribute("aria-label")).toBe("Notifications");
    expect(host.querySelectorAll("li")).toHaveLength(1);
    expect(host.textContent).toContain("Review requests and mentions will appear here.");
  });
});
