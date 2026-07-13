import { expect, test } from "@playwright/test";
import { installMockHost, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

interface Rgb {
  r: number;
  g: number;
  b: number;
}

function parseRgb(value: string): Rgb {
  const channels = value.match(/\d+(?:\.\d+)?/g)?.slice(0, 3).map(Number);
  const [r, g, b] = channels ?? [];
  if (r === undefined || g === undefined || b === undefined) {
    throw new Error(`Expected an rgb colour, got ${value}`);
  }
  return { r, g, b };
}

function luminance({ r, g, b }: Rgb): number {
  const linear = (channel: number): number => {
    const value = channel / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  return linear(r) * 0.2126 + linear(g) * 0.7152 + linear(b) * 0.0722;
}

function contrast(a: Rgb, b: Rgb): number {
  const aLuminance = luminance(a);
  const bLuminance = luminance(b);
  const lighter = Math.max(aLuminance, bLuminance);
  const darker = Math.min(aLuminance, bLuminance);
  return (lighter + 0.05) / (darker + 0.05);
}

async function panelColours(page: import("@playwright/test").Page): Promise<{
  panel: string;
  header: string;
  rail: string;
  normalBackground: string;
  normalText: string;
  activeBackground: string;
  activeText: string;
  titleText: string;
}> {
  return page.locator("#left-dock").evaluate((dock) => {
    const style = (selector: string): CSSStyleDeclaration =>
      getComputedStyle(dock.querySelector<HTMLElement>(selector) as HTMLElement);
    const normal = style('.dock-rail-btn[aria-checked="false"]');
    const active = style('.dock-rail-btn[aria-checked="true"]');
    return {
      panel: getComputedStyle(dock).backgroundColor,
      header: style(".dock-header").backgroundColor,
      rail: style(".dock-rail").backgroundColor,
      normalBackground: normal.backgroundColor,
      normalText: normal.color,
      activeBackground: active.backgroundColor,
      activeText: active.color,
      titleText: style(".dock-title").color,
    };
  });
}

test("panel surfaces keep a visible, accessible grey hierarchy in light and dark themes", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await page.locator('#left-dock .dock-rail-btn[aria-checked="true"]').click();
  await page.locator('#right-dock .dock-rail-btn[aria-checked="true"]').click();

  for (const theme of ["light", "dark"] as const) {
    if (theme === "dark") {
      await page.locator("#github-btn").click();
      await page.locator("#theme-btn").click();
    }

    const colours = await panelColours(page);
    expect(colours.header).not.toBe(colours.panel);
    expect(colours.rail).not.toBe(colours.panel);
    expect(luminance(parseRgb(colours.rail))).toBeLessThan(luminance(parseRgb(colours.panel)));
    expect(contrast(parseRgb(colours.titleText), parseRgb(colours.header))).toBeGreaterThanOrEqual(4.5);
    const normalBackground = colours.normalBackground === "rgba(0, 0, 0, 0)" ? colours.rail : colours.normalBackground;
    expect(contrast(parseRgb(colours.normalText), parseRgb(normalBackground))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(parseRgb(colours.activeText), parseRgb(colours.activeBackground))).toBeGreaterThanOrEqual(4.5);

    const normalButton = page.locator('#left-dock .dock-rail-btn[aria-checked="false"]').first();
    await normalButton.hover();
    const hover = await normalButton.evaluate((button) => {
      const style = getComputedStyle(button);
      return { background: style.backgroundColor, text: style.color };
    });
    expect(contrast(parseRgb(hover.text), parseRgb(hover.background))).toBeGreaterThanOrEqual(4.5);
    await page.screenshot({ path: testInfo.outputPath(`final-${theme}.png`), fullPage: true });
  }
});
