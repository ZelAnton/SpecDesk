import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { type FullApp, launchAndAttach, stopAndDump } from "../lib/full-app";

// Layer 2 completion coverage for tasks 20-23. This deliberately uses the real Host/WebView2 bridge but
// never fabricates a successful GitHub/Copilot exchange: the disposable profile is signed out, so the safe
// automated boundary is SDK packaging + host account wiring. Live Copilot success remains a manual check
// with the author's own entitled account.
test.describe.configure({ mode: "serial", timeout: 60_000 });

let ctx: FullApp;

test.beforeAll(async () => {
  ctx = await launchAndAttach();
});

test.afterAll(async ({}, testInfo) => {
  await stopAndDump(ctx, testInfo);
});

const visibleRightTools = (page: Page): Promise<string[]> =>
  page
    .locator("#right-dock .dock-rail-btn:visible")
    .evaluateAll((buttons) => buttons.map((button) => button.getAttribute("aria-label") ?? ""));

async function openFile(page: Page, name: string): Promise<void> {
  const filesMode = page.locator('#left-dock .dock-rail-btn[aria-label="Folders"]');
  if ((await filesMode.getAttribute("aria-expanded")) !== "true") {
    await filesMode.click();
  }
  const file = page.locator("#left-dock .file-tree-file", { hasText: name });
  await expect(file).toBeVisible();
  await file.click();
}

async function openAssistant(page: Page): Promise<void> {
  const assistantMode = page.locator('#right-dock .dock-rail-btn[aria-label="Assistant"]');
  if ((await assistantMode.getAttribute("aria-expanded")) !== "true") {
    await assistantMode.click();
  }
  await expect(page.locator("#right-dock .assistant-chat")).toBeVisible();
}

function git(repo: string, ...args: string[]): void {
  execFileSync("git", args, { cwd: repo, stdio: "pipe" });
}

test("real Host packages Copilot 1.0.6 and exposes the honest signed-out chat boundary", async ({}, testInfo) => {
  const { page } = ctx;
  await expect(page).toHaveTitle("SpecDesk");

  // Strongest safe automated SDK assertion without credentials: the freshly-built Host's runtime graph
  // contains the approved SDK version. This proves deployment wiring, not a live Copilot entitlement.
  const deps = readFileSync(
    resolve(
      import.meta.dirname,
      "..",
      "..",
      "src",
      "SpecDesk.Host",
      "bin",
      "Debug",
      "net10.0",
      "SpecDesk.Host.deps.json",
    ),
    "utf8",
  );
  expect(deps).toContain('"GitHub.Copilot.SDK/1.0.6"');

  // The real native account frame reaches the real WebView2. A fresh disposable profile must not claim a
  // connection: the toolbar offers Connect and the Copilot composer is disabled with a truthful reason.
  await expect(page.locator("#github-auth-btn")).toBeVisible();
  await expect(page.locator("#github-auth-btn")).toHaveText("Sign in");
  await expect.poll(() => visibleRightTools(page)).toEqual([
    "Assistant",
    "Outline",
    "Versions",
    "History",
  ]);
  await openAssistant(page);
  await expect(page.locator("#right-dock .dock-title")).toHaveText("Assistant");
  await expect(page.locator("#right-dock .chat-input")).toBeDisabled();
  await expect(page.locator("#right-dock .chat-connection-text")).toHaveText(
    "Connect to GitHub to use Copilot",
  );

  const pixels = await page.locator("#right-dock .assistant-chat").screenshot({
    path: testInfo.outputPath("signed-out-copilot.png"),
  });
  expect(pixels.subarray(1, 4).toString("ascii")).toBe("PNG");
  expect(pixels.byteLength).toBeGreaterThan(1_000);
});

test("right tools keep Chat first and follow real named, detached, and file-type context", async ({}, testInfo) => {
  const { page, app } = ctx;

  await openFile(page, "welcome.md");
  await expect.poll(() => visibleRightTools(page)).toEqual([
    "Assistant",
    "Outline",
    "Versions",
    "History",
  ]);
  await expect(page.locator('#right-dock .dock-rail-btn[aria-label="Comments"]')).toBeHidden();
  await page.screenshot({
    path: testInfo.outputPath("context-named-markdown.png"),
    fullPage: true,
  });

  git(app.repo, "checkout", "--detach", "HEAD");
  await openFile(page, ".spectool.toml");
  await expect(page.locator("#current-repository")).toHaveText("sample-repo");
  await expect(page.locator("#current-branch")).toHaveText("Unnamed version");
  await expect(page.locator("#current-path")).toHaveText(".spectool.toml");
  await expect.poll(() => visibleRightTools(page)).toEqual(["Assistant", "Versions"]);
  await page.screenshot({
    path: testInfo.outputPath("context-detached-nonmarkdown.png"),
    fullPage: true,
  });

  git(app.repo, "checkout", "main");
  await openFile(page, "welcome.md");
  await expect.poll(() => visibleRightTools(page)).toEqual([
    "Assistant",
    "Outline",
    "Versions",
    "History",
  ]);
  await page.screenshot({
    path: testInfo.outputPath("context-restored-markdown.png"),
    fullPage: true,
  });
});

test("the real WebView2 renders the VS Code-style composer hierarchy, focus treatment, and multiline keyboard", async ({}, testInfo) => {
  const { page } = ctx;
  await openAssistant(page);

  const surface = page.locator("#right-dock .chat-composer-surface");
  const input = surface.locator(".chat-input");
  await expect(surface).toBeVisible();
  await expect(surface.locator(".chat-composer-agent")).toContainText("Copilot");
  await expect(surface.locator('[aria-label="Model selection: automatic"]')).toHaveText("Automatic");
  await expect(surface.locator('[aria-label="Add context"]')).toBeVisible();
  await expect(surface.locator('[aria-label="Send message"]')).toBeVisible();
  await expect(surface.locator('[aria-label="Send message"]')).toHaveAttribute(
    "aria-keyshortcuts",
    "Control+Enter Meta+Enter",
  );

  const geometry = await surface.evaluate((card) => {
    const bounds = card.getBoundingClientRect();
    const prompt = card.querySelector<HTMLElement>(".chat-input")?.getBoundingClientRect();
    const actions = card.querySelector<HTMLElement>(".chat-composer-actions")?.getBoundingClientRect();
    return {
      width: bounds.width,
      height: bounds.height,
      promptBottom: prompt?.bottom ?? 0,
      actionsTop: actions?.top ?? 0,
      actionsBottom: actions?.bottom ?? 0,
      cardBottom: bounds.bottom,
    };
  });
  expect(geometry.width).toBeGreaterThan(240);
  expect(geometry.height).toBeGreaterThan(100);
  expect(geometry.actionsTop).toBeGreaterThanOrEqual(geometry.promptBottom - 1);
  expect(geometry.actionsBottom).toBeLessThanOrEqual(geometry.cardBottom);

  // Real account leaves input disabled. Enable only this DOM control to exercise WebView2 keyboard/focus;
  // AssistantChat still knows it is signed out, so Ctrl+Enter cannot start Copilot work.
  await input.evaluate((element: HTMLTextAreaElement) => {
    element.disabled = false;
  });
  await input.focus();
  await expect(input).toBeFocused();
  await expect
    .poll(() =>
      surface.evaluate((element) => {
        const style = getComputedStyle(element);
        return { style: style.outlineStyle, width: style.outlineWidth };
      }),
    )
    .toEqual({ style: "solid", width: "2px" });
  await input.fill("First line");
  await input.press("Enter");
  await expect(input).toHaveValue("First line\n");
  await expect(page.locator("#right-dock .chat-msg--user")).toHaveCount(0);
  await input.fill("   ");
  await input.press("Control+Enter");
  await expect(page.locator("#right-dock .chat-msg--user")).toHaveCount(0);

  const pixels = await surface.screenshot({ path: testInfo.outputPath("composer-focused.png") });
  expect(pixels.subarray(1, 4).toString("ascii")).toBe("PNG");
  expect(pixels.byteLength).toBeGreaterThan(1_000);
  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
