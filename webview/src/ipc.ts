/**
 * SpecDesk webview IPC client.
 *
 * The single JSON envelope (see docs/design/09-ipc-protocol.md) is exchanged with the .NET host
 * over Photino's `window.external` bridge: `sendMessage` for webview->native, `receiveMessage`
 * to register the native->webview callback. All Markdown/git/AI logic stays native; this client
 * only ships intents and dispatches results.
 */

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

function photinoExternal(): PhotinoExternal | undefined {
  return (globalThis as { external?: PhotinoExternal }).external;
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
    // Drop anything that is not a well-formed envelope (e.g. a bare null, number, or array)
    // so a stray frame never throws inside the host callback.
    if (typeof parsed !== "object" || parsed === null) {
      return;
    }
    const message = parsed as IpcMessage;
    if (typeof message.kind !== "string") {
      return;
    }
    if (message.id !== undefined) {
      // id-bearing frames are correlated replies; resolve the waiter, or drop if unawaited.
      const resolve = this.pending.get(message.id);
      if (resolve) {
        this.pending.delete(message.id);
        resolve(message);
      }
      return;
    }
    this.handlers.get(message.kind)?.(message);
  }

  /** Fire-and-forget message to the host. Returns false when no bridge is present. */
  send(kind: string, payload?: unknown): boolean {
    if (!this.external?.sendMessage) {
      return false;
    }
    const message: IpcMessage = { kind, payload };
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
    return new Promise<IpcMessage>((resolve, reject) => {
      const timer =
        timeoutMs > 0
          ? setTimeout(() => {
              this.pending.delete(id);
              reject(new Error(`IPC request "${kind}" (id=${id}) timed out after ${timeoutMs}ms`));
            }, timeoutMs)
          : undefined;
      this.pending.set(id, (reply) => {
        if (timer !== undefined) {
          clearTimeout(timer);
        }
        resolve(reply);
      });
      send(JSON.stringify(message));
    });
  }

  /** Register a handler for unsolicited host events of a given kind. */
  on(kind: string, handler: (message: IpcMessage) => void): void {
    this.handlers.set(kind, handler);
    this.start();
  }
}

/** Shared default client bound to the live host bridge. */
export const ipc = new IpcClient();

/** Convenience: fire-and-forget send via the default client. */
export function sendToHost(kind: string, payload: unknown): boolean {
  return ipc.send(kind, payload);
}

/** Announce to the host that the webview has finished loading. */
export function postReady(): boolean {
  return sendToHost("ready", null);
}
