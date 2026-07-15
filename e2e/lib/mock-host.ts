import type { BrowserContext, Page } from "@playwright/test";

/** A native→webview frame the host would send, or a webview→native frame the app sent out. */
export interface HostFrame {
  kind: string;
  id?: string;
  version?: number;
  payload?: unknown;
}

/**
 * Install the mock Photino host bridge as `globalThis.external`, BEFORE any page script runs, so the
 * bundle's `ipc` client binds to it exactly as it binds to the real host. Sent frames accumulate on
 * `window.__sd_sent`; the receive callback is stashed as `window.__sd_deliver` so {@link emit} can
 * push native→webview frames in. Defined (not assigned) with `Object.defineProperty` — engine-agnostic
 * and matching the vitest harnesses — so the bundle's `Reflect.get(globalThis, "external")` sees the
 * mock rather than Chromium's legacy built-in.
 */
export async function installMockHost(context: BrowserContext): Promise<void> {
  await context.addInitScript(() => {
    const sent: unknown[] = [];
    let receive: ((raw: string) => void) | undefined;
    const w = window as unknown as {
      __sd_sent: unknown[];
      __sd_deliver: (raw: string) => void;
    };
    w.__sd_sent = sent;
    w.__sd_deliver = (raw: string) => receive?.(raw);
    Object.defineProperty(globalThis, "external", {
      value: {
        sendMessage: (raw: string) => {
          sent.push(JSON.parse(raw));
        },
        receiveMessage: (cb: (raw: string) => void) => {
          receive = cb;
        },
      },
      configurable: true,
    });
  });
}

/** The webview→native frames the app has sent so far. */
export function sentFrames(page: Page): Promise<HostFrame[]> {
  return page.evaluate(() => (window as unknown as { __sd_sent: HostFrame[] }).__sd_sent);
}

/** Push one native→webview frame into the wired `ipc` client (as the host would). */
export async function emit(page: Page, frame: HostFrame): Promise<void> {
  await page.evaluate((f) => {
    (window as unknown as { __sd_deliver: (raw: string) => void }).__sd_deliver(JSON.stringify(f));
  }, frame);
}

/** Resolve once the app has sent a frame of `kind` (e.g. `"ready"`). */
export async function waitForSent(page: Page, kind: string): Promise<void> {
  await page.waitForFunction(
    (k) =>
      (window as unknown as { __sd_sent: { kind: string }[] }).__sd_sent.some((f) => f.kind === k),
    kind,
  );
}

export interface LoadDocOptions {
  path: string;
  text: string;
  docDir?: string;
  /** Most scenarios exercise the loaded document. Set false only when proving the startup Start screen. */
  reveal?: boolean;
}

/**
 * Feed a `doc.loaded` frame (the host's "here is the document") and wait for both editors to MOUNT —
 * the real mount signal (not a synthetic frame flush). Note this is NOT a geometry-settle: height-sync
 * applies spacers on a later rAF, so a geometry probe wants a separate wait (added with the geometry
 * scenarios), not this.
 */
export async function loadDoc(page: Page, doc: LoadDocOptions): Promise<void> {
  await emit(page, {
    kind: "doc.loaded",
    payload: { path: doc.path, text: doc.text, docDir: doc.docDir ?? "", readOnly: false },
  });
  await page.waitForFunction(
    () =>
      document.querySelector("#editor .cm-editor") !== null &&
      document.querySelector("#formatted .ProseMirror") !== null,
  );
  if (doc.reveal !== false) {
    await page
      .locator('#left-dock .dock-rail-btn[aria-label="Outline"]')
      .evaluate((element: HTMLElement) => element.click());
  }
}
