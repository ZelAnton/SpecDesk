import { describe, expect, it } from "vitest";
import { resolveImageSrc } from "../src/pm-markdown.js";

// resolveImageSrc mirrors the native renderer's rewriteImageUrl (SpecDesk.Markdown/Renderer.fs): the
// formatted view must produce the same app://repo/… URLs the preview does, or images won't load.
describe("resolveImageSrc", () => {
  it("resolves a relative link against the document directory", () => {
    expect(resolveImageSrc("specs/api", "images/diagram.png")).toBe(
      "app://repo/specs/api/images/diagram.png",
    );
  });

  it("resolves a relative link at the repo root (empty docDir)", () => {
    expect(resolveImageSrc("", "images/diagram.png")).toBe("app://repo/images/diagram.png");
  });

  it("collapses . and .. segments", () => {
    expect(resolveImageSrc("specs/api", "../assets/./logo.png")).toBe(
      "app://repo/specs/assets/logo.png",
    );
  });

  it("leaves absolute URLs untouched", () => {
    expect(resolveImageSrc("specs", "https://example.com/x.png")).toBe("https://example.com/x.png");
    expect(resolveImageSrc("specs", "data:image/png;base64,AAAA")).toBe(
      "data:image/png;base64,AAAA",
    );
  });

  it("leaves root-anchored, anchor, and empty URLs untouched", () => {
    expect(resolveImageSrc("specs", "/abs/x.png")).toBe("/abs/x.png");
    expect(resolveImageSrc("specs", "#frag")).toBe("#frag");
    expect(resolveImageSrc("specs", "")).toBe("");
  });
});
