import { describe, expect, it } from "vitest";
import { isSplit, paneVisibility, type ViewMode } from "../../src/chrome/view-mode.js";

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

describe("isSplit", () => {
  it("is true only for split", () => {
    const modes: ViewMode[] = ["code", "split", "formatted"];
    expect(modes.filter(isSplit)).toEqual(["split"]);
  });
});
