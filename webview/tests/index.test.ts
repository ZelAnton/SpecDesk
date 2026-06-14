import { describe, expect, it } from "vitest";
import { postReady, sendToHost } from "../src/index.js";

describe("ipc client", () => {
  it("sendToHost returns false when no host bridge is present", () => {
    expect(sendToHost("ping", { value: 1 })).toBe(false);
  });

  it("postReady returns false when no host bridge is present", () => {
    expect(postReady()).toBe(false);
  });
});
