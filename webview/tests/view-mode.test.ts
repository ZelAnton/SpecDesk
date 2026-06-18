import { describe, expect, it } from "vitest";
import { isSplit, paneVisibility, scrollAuthority, type ViewMode } from "../src/view-mode.js";

describe("paneVisibility", () => {
  it("shows only the editor in code mode", () => {
    expect(paneVisibility("code")).toEqual({ editor: true, preview: false });
  });

  it("shows both panes in split mode", () => {
    expect(paneVisibility("split")).toEqual({ editor: true, preview: true });
  });

  it("shows only the preview in formatted mode", () => {
    expect(paneVisibility("formatted")).toEqual({ editor: false, preview: true });
  });
});

describe("scrollAuthority", () => {
  it("reads from the editor whenever it is shown", () => {
    expect(scrollAuthority("code")).toBe("editor");
    expect(scrollAuthority("split")).toBe("editor");
  });

  it("reads from the preview in formatted mode", () => {
    expect(scrollAuthority("formatted")).toBe("preview");
  });
});

describe("isSplit", () => {
  it("is true only for split", () => {
    const modes: ViewMode[] = ["code", "split", "formatted"];
    expect(modes.filter(isSplit)).toEqual(["split"]);
  });
});
