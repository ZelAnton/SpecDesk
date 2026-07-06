import { afterEach, describe, expect, it, vi } from "vitest";
import { IpcClient, type IpcMessage } from "../../src/wire/ipc.js";

/** A fake `window.external` — the "mock IPC host" the design calls for. */
function mockBridge() {
  const sent: string[] = [];
  let callback: ((message: string) => void) | undefined;
  return {
    sent,
    sendMessage: (message: string) => {
      sent.push(message);
    },
    receiveMessage: (cb: (message: string) => void) => {
      callback = cb;
    },
    /** Simulate a native->webview frame. */
    emit: (message: IpcMessage) => callback?.(JSON.stringify(message)),
    /** Simulate a raw (possibly malformed) native->webview frame. */
    emitRaw: (raw: string) => callback?.(raw),
  };
}

afterEach(() => {
  vi.useRealTimers();
});

describe("IpcClient", () => {
  it("request resolves on a reply with the matching id", async () => {
    const bridge = mockBridge();
    const client = new IpcClient(bridge);

    const promise = client.request("echo", { text: "hi" });

    expect(bridge.sent).toHaveLength(1);
    const sent = JSON.parse(bridge.sent[0] ?? "") as IpcMessage;
    expect(sent.kind).toBe("echo");
    const id = sent.id;
    if (id === undefined) {
      throw new Error("request did not include an id");
    }

    bridge.emit({ kind: "echo.reply", id, payload: { text: "hi" } });

    const reply = await promise;
    expect(reply.kind).toBe("echo.reply");
    expect(reply.payload).toEqual({ text: "hi" });
  });

  it("drops an id-bearing reply that matches no pending request", () => {
    const bridge = mockBridge();
    const client = new IpcClient(bridge);
    const handler = vi.fn();
    client.on("echo.reply", handler);

    bridge.emit({ kind: "echo.reply", id: "unknown", payload: null });

    expect(handler).not.toHaveBeenCalled();
  });

  it("on dispatches unsolicited events by kind", () => {
    const bridge = mockBridge();
    const client = new IpcClient(bridge);
    const handler = vi.fn();
    client.on("status", handler);

    bridge.emit({ kind: "status", payload: { state: "Saved" } });

    expect(handler).toHaveBeenCalledOnce();
  });

  it("on throws instead of silently replacing a handler already registered for the kind", () => {
    const bridge = mockBridge();
    const client = new IpcClient(bridge);
    const first = vi.fn();
    const second = vi.fn();
    client.on("status", first);

    expect(() => client.on("status", second)).toThrow(/already registered/);

    // The original handler is still the one dispatched to — not silently replaced.
    bridge.emit({ kind: "status", payload: { state: "Saved" } });
    expect(first).toHaveBeenCalledOnce();
    expect(second).not.toHaveBeenCalled();
  });

  it("send returns false and request rejects when no bridge is present", async () => {
    const client = new IpcClient(undefined);
    expect(client.send("ping", {})).toBe(false);
    await expect(client.request("echo", {})).rejects.toThrow();
  });

  it("ignores malformed and non-object frames without throwing", () => {
    const bridge = mockBridge();
    const client = new IpcClient(bridge);
    const handler = vi.fn();
    client.on("status", handler);

    // None of these are well-formed envelopes; dispatch must swallow them.
    expect(() => bridge.emitRaw("{not json")).not.toThrow();
    expect(() => bridge.emitRaw("null")).not.toThrow();
    expect(() => bridge.emitRaw("42")).not.toThrow();
    expect(() => bridge.emitRaw("[1,2,3]")).not.toThrow();
    expect(() => bridge.emitRaw('{"id":"x"}')).not.toThrow();
    expect(handler).not.toHaveBeenCalled();
  });

  it("subscribe routes every frame for an id to the handler until unsubscribed", () => {
    const bridge = mockBridge();
    const client = new IpcClient(bridge);
    const received: IpcMessage[] = [];
    const id = "stream-1";

    const unsubscribe = client.subscribe(id, (message) => {
      received.push(message);
      if (message.kind === "chat.done") {
        unsubscribe();
      }
    });

    bridge.emit({ kind: "chat.delta", id, payload: { text: "Hel" } });
    bridge.emit({ kind: "chat.delta", id, payload: { text: "lo" } });
    bridge.emit({ kind: "chat.done", id, payload: null });
    // A frame arriving after the terminal one is a no-op — the entry was released above.
    bridge.emit({ kind: "chat.delta", id, payload: { text: "late" } });

    expect(received.map((message) => message.kind)).toEqual([
      "chat.delta",
      "chat.delta",
      "chat.done",
    ]);
  });

  it("request rejects and drops the pending entry after the timeout", async () => {
    vi.useFakeTimers();
    const bridge = mockBridge();
    const client = new IpcClient(bridge);

    const promise = client.request("never-answered", {}, 1000);
    const assertion = expect(promise).rejects.toThrow(/timed out/);
    await vi.advanceTimersByTimeAsync(1000);
    await assertion;

    // A late reply for the timed-out id is now a no-op (entry already dropped).
    const sent = JSON.parse(bridge.sent[0] ?? "") as IpcMessage;
    const id = sent.id ?? "";
    expect(() => bridge.emit({ kind: "late.reply", id, payload: null })).not.toThrow();
  });
});
