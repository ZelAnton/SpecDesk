import { afterEach, describe, expect, it, vi } from "vitest";
import { log } from "../../src/util/log.js";
import { ipc } from "../../src/wire/ipc.js";
import { Kinds } from "../../src/wire/protocol.js";

afterEach(() => {
  vi.restoreAllMocks();
});

describe("log", () => {
  it("ships level, message, and JSON-stringified data over ipc", () => {
    const send = vi.spyOn(ipc, "send").mockReturnValue(true);
    log.info("hello", { a: 1 });
    expect(send).toHaveBeenCalledWith(Kinds.log, {
      level: "info",
      message: "hello",
      data: '{"a":1}',
    });
  });

  it("omits data when none is passed", () => {
    const send = vi.spyOn(ipc, "send").mockReturnValue(true);
    log.warn("no data");
    expect(send).toHaveBeenCalledWith(Kinds.log, { level: "warn", message: "no data" });
  });

  it("renders BigInt values instead of throwing", () => {
    const send = vi.spyOn(ipc, "send").mockReturnValue(true);
    log.debug("big", { count: 10n });
    const payload = send.mock.calls[0]?.[1] as { data?: string };
    expect(payload.data).toBe('{"count":"10n"}');
  });

  it("falls back to a placeholder instead of throwing on circular data", () => {
    const send = vi.spyOn(ipc, "send").mockReturnValue(true);
    const cyclic: Record<string, unknown> = {};
    cyclic.self = cyclic;
    expect(() => log.error("cyclic", cyclic)).not.toThrow();
    const payload = send.mock.calls[0]?.[1] as { data?: string };
    expect(payload.data).toBe("[unserializable log data]");
  });

  it("dispatches each level with its own tag", () => {
    const send = vi.spyOn(ipc, "send").mockReturnValue(true);
    log.debug("d");
    log.error("e");
    expect(send.mock.calls[0]?.[1]).toMatchObject({ level: "debug" });
    expect(send.mock.calls[1]?.[1]).toMatchObject({ level: "error" });
  });
});
