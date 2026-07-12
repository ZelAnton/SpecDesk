// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { buildHomeView } from "../../src/workspace/tools/home-view.js";

describe("buildHomeView", () => {
  it("builds the Start screen with a title, prompt, recents, and a working Open button", () => {
    const host = document.createElement("div");
    const onOpen = vi.fn();
    buildHomeView(host, onOpen);

    expect(host.querySelector(".home-title")?.textContent).toBe("SpecDesk");
    expect(host.querySelector(".home-prompt")).not.toBeNull();
    expect(host.querySelector(".home-recents-label")?.textContent).toBe("Recent");

    const open = host.querySelector<HTMLButtonElement>(".home-open");
    expect(open?.textContent).toBe("Open a spec");
    open?.click();
    expect(onOpen).toHaveBeenCalledTimes(1);
  });
});
