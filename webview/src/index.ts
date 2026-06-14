/**
 * SpecDesk webview — IPC client placeholder.
 *
 * The bundle (built with esbuild) runs inside the WebView2 surface hosted by
 * SpecDesk.Host. Messages are exchanged with the .NET host over Photino's
 * `window.external.sendMessage` channel. This is a scaffold: real message
 * kinds and payload shapes land with the IPC protocol implementation.
 */

interface HostBridge {
  sendMessage?: (message: string) => void;
}

/** Photino injects `window.external.sendMessage`; it is absent outside the host. */
function hostBridge(): HostBridge | undefined {
  return (globalThis as { external?: HostBridge }).external;
}

/**
 * Send a typed message to the .NET host. No-op (returns false) when not running
 * inside the host shell, so the same bundle can load in a plain browser for dev.
 */
export function sendToHost(kind: string, payload: unknown): boolean {
  const bridge = hostBridge();
  if (!bridge?.sendMessage) {
    return false;
  }
  bridge.sendMessage(JSON.stringify({ kind, payload }));
  return true;
}

/** Announce to the host that the webview has finished loading. */
export function postReady(): boolean {
  return sendToHost("ready", null);
}
