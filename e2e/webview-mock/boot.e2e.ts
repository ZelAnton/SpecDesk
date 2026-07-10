import { expect, test } from "@playwright/test";
import { installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

// A tiny document — the boot smoke only proves both editors mount and the app handshakes; the
// real-geometry scenarios (spacers, alignment, indentation) come in a later stage with a richer fixture.
const BOOT_DOC = ["# Boot smoke", "", "A short paragraph.", "", "- one", "- two", ""].join("\n");

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("boots both editors from a doc.loaded frame and handshakes ready", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);

  // The app announces itself to the host on load.
  await waitForSent(page, "ready");

  await loadDoc(page, { path: "boot.md", text: BOOT_DOC });

  // Both live editors mounted from one doc.loaded — the real CodeMirror source pane and the real
  // ProseMirror formatted pane, in real Chromium.
  await expect(page.locator("#editor .cm-editor")).toHaveCount(1);
  await expect(page.locator("#formatted .ProseMirror")).toHaveCount(1);
  // The formatted pane actually rendered the document (it parses Markdown itself, no native preview).
  await expect(page.locator("#formatted .ProseMirror h1")).toHaveText("Boot smoke");

  const frames = await sentFrames(page);
  expect(frames.some((f) => f.kind === "ready")).toBe(true);

  // Always leave a screenshot the agent can Read — success path included.
  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
