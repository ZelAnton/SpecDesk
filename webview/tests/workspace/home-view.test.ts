// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { buildHomeView } from "../../src/workspace/tools/home-view.js";

describe("buildHomeView", () => {
  it("builds the Start screen with a title, prompt, recents, and open-file / open-folder actions", () => {
    const host = document.createElement("div");
    const onOpenFile = vi.fn();
    const onOpenFolder = vi.fn();
    buildHomeView(host, { onOpenFile, onOpenFolder });

    expect(host.querySelector(".home-title")?.textContent).toBe("SpecDesk");
    expect(host.querySelector(".home-prompt")).not.toBeNull();
    expect(host.querySelector(".home-recents-label")?.textContent).toBe("Recent");

    const buttons = host.querySelectorAll<HTMLButtonElement>(".home-open");
    expect(Array.from(buttons).map((b) => b.textContent)).toEqual(["Open a file", "Open a folder"]);

    buttons[0]?.click();
    expect(onOpenFile).toHaveBeenCalledTimes(1);
    buttons[1]?.click();
    expect(onOpenFolder).toHaveBeenCalledTimes(1);
  });
});
