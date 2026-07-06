import { describe, expect, it } from "vitest";
import { urlAtColumn } from "../../src/util/links.js";

describe("urlAtColumn", () => {
  it("finds a bare URL the column falls within", () => {
    const line = "see https://example.com now";
    expect(urlAtColumn(line, 10)).toBe("https://example.com");
  });

  it("finds the URL inside an inline markdown link, excluding the closing paren", () => {
    expect(urlAtColumn("[docs](https://example.com/path)", 15)).toBe("https://example.com/path");
    expect(urlAtColumn("[d](https://x.com)", 6)).toBe("https://x.com");
  });

  it("finds the URL inside an autolink, excluding the angle brackets", () => {
    expect(urlAtColumn("<https://example.com>", 5)).toBe("https://example.com");
  });

  it("trims trailing sentence punctuation from a bare URL", () => {
    expect(urlAtColumn("visit https://x.com.", 10)).toBe("https://x.com");
  });

  it("returns null when the column is not on the URL", () => {
    const line = "see https://example.com now";
    expect(urlAtColumn(line, 0)).toBeNull();
    expect(urlAtColumn(line, 25)).toBeNull();
    // The position right after the URL (the trailing space / line-end clamp) is not on the URL.
    expect(urlAtColumn(line, 23)).toBeNull();
    expect(urlAtColumn("https://x.com", 13)).toBeNull();
  });

  it("returns null when the line has no URL", () => {
    expect(urlAtColumn("plain text without links", 4)).toBeNull();
  });
});
