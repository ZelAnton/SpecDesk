/**
 * SpecDesk webview IPC client.
 *
 * The single JSON envelope (see docs/design/09-ipc-protocol.md) is exchanged with the .NET host
 * over Photino's `window.external` bridge: `sendMessage` for webview->native, `receiveMessage`
 * to register the native->webview callback. All Markdown/git/AI logic stays native; this client
 * only ships intents and dispatches results.
 */

import { trace } from "../util/trace.js";
import { isNumber, isRecord, isString } from "./decoders.js";
import { Kinds } from "./protocol.js";

// The diagnostics channels are excluded from `ipc.send` tracing so they don't trace themselves: `log`
// frames (the trace's own error-forward path ships these) and the future `trace.dump` frame (B3).
const TRACE_DUMP_KIND = "trace.dump";

export interface IpcMessage {
  kind: string;
  id?: string;
  version?: number;
  payload?: unknown;
}

/** Photino injects these on `window.external` inside the host shell. */
interface PhotinoExternal {
  sendMessage?: (message: string) => void;
  receiveMessage?: (callback: (message: string) => void) => void;
}

/**
 * The Photino-injected `window.external` bridge, read via Reflect so the non-standard global isn't a
 * cast on `globalThis`. Validated to be an object; its (optional) methods are typeof-guarded at every
 * call site, so the one boundary assertion here is the host bridge, isolated and absent outside the shell.
 */
function photinoExternal(): PhotinoExternal | undefined {
  const external: unknown = Reflect.get(globalThis, "external");
  return isRecord(external) ? (external as PhotinoExternal) : undefined;
}

/** Narrow a parsed JSON frame to a well-formed envelope, or null (a bare value / contract drift). */
function parseEnvelope(value: unknown): IpcMessage | null {
  if (!isRecord(value) || !isString(value.kind)) {
    return null;
  }
  if (value.id !== undefined && !isString(value.id)) {
    return null;
  }
  if (value.version !== undefined && !isNumber(value.version)) {
    return null;
  }
  const message: IpcMessage = { kind: value.kind, payload: value.payload };
  if (value.id !== undefined) {
    message.id = value.id;
  }
  if (value.version !== undefined) {
    message.version = value.version;
  }
  return message;
}

let idCounter = 0;

/** Correlation id for a request/reply pair. Avoids `crypto.randomUUID` (needs a secure context). */
function nextId(): string {
  idCounter += 1;
  return `r-${idCounter}-${Math.random().toString(36).slice(2, 8)}`;
}

/**
 * Bidirectional client over the Photino bridge: fire-and-forget {@link IpcClient.send}, a
 * correlated {@link IpcClient.request} (reply matched by `id`), and {@link IpcClient.on} for
 * unsolicited host events.
 */
export class IpcClient {
  private readonly external: PhotinoExternal | undefined;
  private readonly pending = new Map<string, (reply: IpcMessage) => void>();
  private readonly handlers = new Map<string, (message: IpcMessage) => void>();
  private listening = false;

  constructor(external: PhotinoExternal | undefined = photinoExternal()) {
    this.external = external;
  }

  /** Begin receiving messages from the host. Idempotent; a no-op without a bridge. */
  start(): void {
    if (this.listening || !this.external?.receiveMessage) {
      return;
    }
    this.external.receiveMessage((raw) => {
      this.dispatch(raw);
    });
    this.listening = true;
  }

  private dispatch(raw: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      // Ignore malformed frames rather than throwing inside the bridge callback.
      return;
    }
    // Drop anything that is not a well-formed envelope (a bare null/number/array, or a missing kind)
    // so a stray frame never throws inside the host callback.
    const message = parseEnvelope(parsed);
    if (message === null) {
      return;
    }
    // Byte count only — never the payload (an `editor.changed` frame carries the whole document).
    trace("ipc", "ipc.recv", {
      kind: message.kind,
      id: message.id,
      version: message.version,
      bytes: raw.length,
    });
    if (message.id !== undefined) {
      // id-bearing frames are correlated replies, routed to whoever is waiting on that id (or
      // dropped if unawaited). `pending` entries are NOT removed here: request() below is a
      // one-shot correlation and deletes its own entry once resolved, while subscribe() is a
      // multi-frame correlation (PoC-9's chat.delta/chat.done streaming) whose entry stays
      // registered across frames until the subscriber itself calls the returned unsubscribe —
      // typically on a terminal frame kind such as chat.done. This lets both usages share one
      // `pending` map/dispatch path without dispatch needing to know which frame is "the last".
      this.pending.get(message.id)?.(message);
      return;
    }
    this.handlers.get(message.kind)?.(message);
  }

  /**
   * Fire-and-forget message to the host. Pass `opts.version` to stamp the monotonic editor
   * counter onto the envelope (used by `editor.changed`). Returns false when no bridge is present.
   */
  send(kind: string, payload?: unknown, opts?: { version?: number }): boolean {
    if (!this.external?.sendMessage) {
      return false;
    }
    const message: IpcMessage = { kind, payload };
    if (opts?.version !== undefined) {
      message.version = opts.version;
    }
    if (kind !== Kinds.log && kind !== TRACE_DUMP_KIND) {
      trace("ipc", "ipc.send", { kind, version: message.version });
    }
    this.external.sendMessage(JSON.stringify(message));
    return true;
  }

  /**
   * Send a request and resolve with the host's reply correlated by `id`. Rejects after
   * `timeoutMs` (default 30s) so a kind the host never answers cannot hang the caller or leak
   * the pending entry; pass `0` to wait indefinitely.
   */
  request(kind: string, payload?: unknown, timeoutMs = 30_000): Promise<IpcMessage> {
    this.start();
    const send = this.external?.sendMessage;
    if (!send) {
      return Promise.reject(new Error("No host bridge available"));
    }
    const id = nextId();
    const message: IpcMessage = { kind, id, payload };
    if (kind !== Kinds.log && kind !== TRACE_DUMP_KIND) {
      trace("ipc", "ipc.send", { kind, id });
    }
    return new Promise<IpcMessage>((resolve, reject) => {
      const timer =
        timeoutMs > 0
          ? setTimeout(() => {
              this.pending.delete(id);
              reject(new Error(`IPC request "${kind}" (id=${id}) timed out after ${timeoutMs}ms`));
            }, timeoutMs)
          : undefined;
      this.pending.set(id, (reply) => {
        // One-shot: drop the entry before resolving, since dispatch() no longer does so itself
        // (see the comment there on why request() and subscribe() share `pending` differently).
        this.pending.delete(id);
        if (timer !== undefined) {
          clearTimeout(timer);
        }
        resolve(reply);
      });
      send(JSON.stringify(message));
    });
  }

  /**
   * Correlate every frame carrying `id` to `handler`, for streaming replies (PoC-9's
   * chat.delta/chat.done) where a single request produces more than one reply. Unlike
   * {@link IpcClient.request}, the entry is not removed automatically — call the returned
   * `unsubscribe()` once the stream is done (e.g. when `handler` sees a terminal kind such as
   * `chat.done`) to stop receiving frames for that id and free the entry.
   */
  subscribe(id: string, handler: (message: IpcMessage) => void): () => void {
    this.start();
    this.pending.set(id, handler);
    return () => {
      this.pending.delete(id);
    };
  }

  /**
   * Register a handler for unsolicited host events of a given kind. Throws if a handler is
   * already registered for that kind — re-registering the same kind previously replaced the
   * prior handler silently, which is never the intent of any current caller (each kind is
   * registered exactly once, from a single init path).
   */
  on(kind: string, handler: (message: IpcMessage) => void): void {
    if (this.handlers.has(kind)) {
      throw new Error(`IPC handler for kind "${kind}" is already registered`);
    }
    this.handlers.set(kind, handler);
    this.start();
  }
}

/** Shared default client bound to the live host bridge. */
export const ipc = new IpcClient();

/** Announce to the host that the webview has finished loading. */
export function postReady(): boolean {
  return ipc.send(Kinds.ready, null);
}
