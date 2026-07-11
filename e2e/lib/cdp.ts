import { type Browser, chromium, type Page } from "@playwright/test";

export interface AttachedApp {
  browser: Browser;
  page: Page;
}

/**
 * Poll the WebView2 CDP endpoint until it answers, connect over it, and return the SpecDesk page — the
 * one whose URL is `…/wwwroot/index.html` (WebView2 starts a target at `about:blank`, then Photino
 * navigates it to the shell). connectOverCDP is the version-tolerant path Microsoft documents for
 * driving WebView2 with Playwright.
 */
export async function attachToApp(port: number, timeoutMs = 30_000): Promise<AttachedApp> {
  const endpoint = `http://127.0.0.1:${port}`;

  // 1) Wait for the debugging endpoint to come up (its own budget — the host + WebView2 take a moment).
  const endpointDeadline = Date.now() + timeoutMs;
  let endpointUp = false;
  while (Date.now() < endpointDeadline) {
    try {
      const res = await fetch(`${endpoint}/json/version`);
      if (res.ok) {
        endpointUp = true;
        break;
      }
    } catch {
      // Endpoint not listening yet — keep polling until the deadline.
    }
    await delay(200);
  }
  if (!endpointUp) {
    // A clear cause (rather than connectOverCDP's generic connect error) — the host never exposed the
    // debug port: it may have failed to launch, or a guard refused a stale build/working copy.
    throw new Error(
      `WebView2 CDP debug port never came up on ${endpoint} within ${timeoutMs}ms (host failed to launch or a guard refused it)`,
    );
  }

  const browser = await chromium.connectOverCDP(endpoint);

  // 2) Wait for the shell page to navigate from about:blank to wwwroot/index.html (its own budget, so a
  //    slow endpoint in phase 1 doesn't starve the page-appearance wait here).
  const pageDeadline = Date.now() + timeoutMs;
  while (Date.now() < pageDeadline) {
    const page = findSpecPage(browser);
    if (page) {
      await page.waitForLoadState("domcontentloaded").catch(() => {
        // A best-effort settle — the assertions below wait for the concrete UI anyway.
      });
      return { browser, page };
    }
    await delay(200);
  }

  await browser.close();
  throw new Error(`SpecDesk page (wwwroot/index.html) did not appear over CDP on ${endpoint} within ${timeoutMs}ms`);
}

function findSpecPage(browser: Browser): Page | undefined {
  for (const context of browser.contexts()) {
    for (const page of context.pages()) {
      if (page.url().includes("wwwroot/index.html")) {
        return page;
      }
    }
  }
  return undefined;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
