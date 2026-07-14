// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { ActivityStream } from "../../src/workspace/activity-stream.js";

describe("ActivityStream", () => {
  it("keeps a newest-first bounded feed and publishes summaries", () => {
    const stream = new ActivityStream();
    let latest: readonly { message: string }[] = [];
    stream.subscribe((entries) => {
      latest = entries;
    });

    for (let index = 0; index < 251; index++) {
      stream.add("Action", `Action ${index}`);
    }

    expect(latest).toHaveLength(250);
    expect(latest[0]?.message).toBe("Action 250");
    expect(latest.at(-1)?.message).toBe("Action 1");
  });

  it("clears all prior summaries at an account boundary", () => {
    const stream = new ActivityStream();
    let latest: readonly { message: string }[] = [];
    stream.subscribe((entries) => {
      latest = entries;
    });
    stream.add("GitHub", "Opened private/repo review #42");

    stream.clear();

    expect(latest).toEqual([]);
  });
});
