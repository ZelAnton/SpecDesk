import { expect, test } from "../lib/fixtures";
import { openDockTool } from "../lib/dock";
import { emit, waitForSent } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";

test("Copilot composer keeps prompt, actions, and GitHub state in one card", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "octo" },
  });

  const rightDock = page.locator("#right-dock");
  await openDockTool(page, "right", "Assistant");

  const surface = rightDock.locator(".chat-composer-surface");
  await expect(surface).toBeVisible();
  await expect(surface.locator(".chat-input")).toHaveAttribute(
    "placeholder",
    "Describe what you want to work on…",
  );
  await expect(surface.locator(".chat-composer-agent")).toContainText("Copilot");
  await expect(surface.locator('[aria-label="Model selection: automatic"]')).toHaveText(
    "Automatic",
  );
  await expect(surface.locator('[aria-label="Add context"]')).toBeVisible();
  await expect(surface.locator('[aria-label="Send message"]')).toBeVisible();
  await expect(rightDock.locator(".chat-connection-text")).toHaveText(
    "Connected to GitHub as octo",
  );

  const boxes = await surface.evaluate((card) => {
    const prompt = card.querySelector<HTMLElement>(".chat-input")?.getBoundingClientRect();
    const actions = card
      .querySelector<HTMLElement>(".chat-composer-actions")
      ?.getBoundingClientRect();
    const bounds = card.getBoundingClientRect();
    return {
      height: bounds.height,
      cardBottom: bounds.bottom,
      promptBottom: prompt?.bottom ?? 0,
      actionsTop: actions?.top ?? 0,
      actionsBottom: actions?.bottom ?? 0,
    };
  });
  expect(boxes.height).toBeGreaterThan(100);
  expect(boxes.actionsTop).toBeGreaterThanOrEqual(boxes.promptBottom - 1);
  expect(boxes.actionsBottom).toBeLessThanOrEqual(boxes.cardBottom);

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: false },
  });
  await expect(surface.locator(".chat-input")).toBeDisabled();
  await expect(rightDock.locator(".chat-connection-text")).toHaveText(
    "Connect to GitHub to use Copilot",
  );
});
