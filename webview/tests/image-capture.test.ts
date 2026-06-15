import { describe, expect, it } from "vitest";
import { stripDataUrlPrefix } from "../src/image-capture.js";

describe("stripDataUrlPrefix", () => {
  it("removes the data URL prefix, leaving the base64 payload", () => {
    expect(stripDataUrlPrefix("data:image/png;base64,AAABBBCCC")).toBe("AAABBBCCC");
  });

  it("returns the input unchanged when there is no comma", () => {
    expect(stripDataUrlPrefix("AAABBBCCC")).toBe("AAABBBCCC");
  });
});
