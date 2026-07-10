import { readFileSync } from "node:fs";
import { resolve, sep } from "node:path";
import type { BrowserContext } from "@playwright/test";

const libDir = import.meta.dirname;
const wwwrootDir = resolve(libDir, "..", "..", "src", "SpecDesk.Host", "wwwroot");

/** The origin the bundle is served from — `localhost` so the page runs in a SECURE CONTEXT (matching
 *  production WebView2 and keeping secure-context-gated APIs available to later scenarios), served
 *  entirely from disk via `context.route` (not a real server, and not `file://`, which blocks the
 *  `<script type="module">` the bundle loads with). */
export const BASE_URL = "http://localhost/";

const CONTENT_TYPES: Record<string, string> = {
  html: "text/html; charset=utf-8",
  js: "text/javascript; charset=utf-8",
  css: "text/css; charset=utf-8",
};

/**
 * Fulfil every request to {@link BASE_URL} from the built `wwwroot/` on disk. No real server: the
 * bundle, its `index.html` and `styles.css` are read straight off disk, so the page runs the exact
 * artifact global-setup built and verified.
 */
export async function serveBundle(context: BrowserContext): Promise<void> {
  await context.route(`${BASE_URL}**`, async (route) => {
    // A fulfill can reject if the page/context is already tearing down when a late request arrives;
    // swallow that so it never surfaces as an unhandled rejection (which Playwright treats as a
    // worker error and can misattribute to another test). The request simply has nowhere to land.
    const respond = (options: Parameters<typeof route.fulfill>[0]): Promise<void> =>
      route.fulfill(options).catch(() => {});

    const path = new URL(route.request().url()).pathname;
    const rel = path === "/" ? "index.html" : path.replace(/^\/+/, "");
    const target = resolve(wwwrootDir, rel);
    // Contain the read to wwwroot — a `..` in the path must never escape the served tree.
    if (target !== wwwrootDir && !target.startsWith(wwwrootDir + sep)) {
      await respond({ status: 403, body: "forbidden" });
      return;
    }

    let body: Buffer;
    try {
      body = readFileSync(target);
    } catch {
      // The only expected read error is ENOENT (a request for a file not in the bundle, e.g. the
      // auto /favicon.ico); serve a 404 so the page surfaces the miss rather than the route hanging.
      await respond({ status: 404, body: `not found: ${rel}` });
      return;
    }

    const ext = rel.slice(rel.lastIndexOf(".") + 1);
    await respond({
      status: 200,
      contentType: CONTENT_TYPES[ext] ?? "application/octet-stream",
      body,
    });
  });
}
