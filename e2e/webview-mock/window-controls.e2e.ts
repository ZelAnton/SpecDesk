import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("in-content window controls route commands and reflect native maximize state", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");

  await expect(page.getByRole("group", { name: "Window controls" })).toBeVisible();
  await page.locator("#app-title").click();
  let kinds = (await sentFrames(page)).map((frame) => frame.kind);
  expect(kinds.filter((kind) => kind === "window.drag")).toHaveLength(1);
  expect(kinds.filter((kind) => kind === "window.toggleMaximize")).toHaveLength(0);

  // Reload for an independent real double-click sequence: Playwright supplies the browser's trusted
  // mousedown click counts, while the mock host bridge gets a fresh outbound-frame list.
  await page.reload();
  await waitForSent(page, "ready");
  await page.getByRole("button", { name: "Minimize" }).click();
  await page.getByRole("button", { name: "Maximize" }).click();
  await page.locator("#app-title").dblclick();

  kinds = (await sentFrames(page)).map((frame) => frame.kind);
  expect(kinds).toContain("window.minimize");
  expect(kinds.filter((kind) => kind === "window.toggleMaximize")).toHaveLength(2);
  expect(kinds.filter((kind) => kind === "window.drag")).toHaveLength(1);

  await emit(page, { kind: "window.state", payload: { maximized: true } });
  const restore = page.getByRole("button", { name: "Restore" });
  await expect(restore).toHaveAttribute("aria-pressed", "true");
  await emit(page, { kind: "window.state", payload: { maximized: false } });
  await expect(page.getByRole("button", { name: "Maximize" })).toHaveAttribute(
    "aria-pressed",
    "false",
  );

  await page.getByRole("button", { name: "Close" }).click();
  expect((await sentFrames(page)).some((frame) => frame.kind === "window.close")).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("custom-window-controls.png"), fullPage: true });
});
