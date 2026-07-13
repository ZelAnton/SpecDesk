import { expect, type Locator, type Page } from "@playwright/test";

export type DockEdge = "left" | "right" | "bottom";

/** Select a dock tool and leave its panel expanded without collapsing an already-open active tool. */
export async function openDockTool(
  page: Page,
  edge: DockEdge,
  label: string,
): Promise<Locator> {
  const mode = page.locator(`#${edge}-dock .dock-rail-btn[aria-label="${label}"]`);
  await expect(mode).toBeVisible();
  if ((await mode.getAttribute("aria-expanded")) !== "true") {
    await mode.click();
  }
  await expect(mode).toHaveAttribute("aria-expanded", "true");
  return mode;
}
