/**
 * SpecDesk webview entrypoint (PoC-0). Wires the echo demo: send the input text to the native
 * host as an `echo` request and render the correlated reply. The real editor/preview UI lands
 * in later PoCs.
 */

import { ipc, postReady } from "./ipc.js";

function wire(): void {
  const input = document.querySelector<HTMLInputElement>("#echo-input");
  const button = document.querySelector<HTMLButtonElement>("#echo-btn");
  const output = document.querySelector<HTMLDivElement>("#output");
  if (!input || !button || !output) {
    return;
  }

  ipc.start();

  button.addEventListener("click", () => {
    void (async () => {
      try {
        const reply = await ipc.request("echo", { text: input.value });
        const payload = reply.payload as { text?: string } | undefined;
        output.textContent = `Reply (id=${reply.id ?? "?"}): ${payload?.text ?? ""}`;
      } catch (error) {
        output.textContent = `Error: ${String(error)}`;
      }
    })();
  });

  postReady();
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", wire);
} else {
  wire();
}
