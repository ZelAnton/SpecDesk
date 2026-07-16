import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
	await serveBundle(context);
	await installMockHost(context);
});

test("Markdown commands stay on one row and move into a measured keyboard menu", async ({
	page,
}, testInfo) => {
	await page.setViewportSize({ width: 460, height: 720 });
	await page.goto(BASE_URL);
	await waitForSent(page, "ready");
	await loadDoc(page, {
		path: "C:\\work\\specs\\toolbar.md",
		text: "# Toolbar\n\nSelect a view.\n",
	});
	await emit(page, {
		kind: "status",
		payload: { state: "draft", label: "Saved", branch: "spec/toolbar" },
	});

	const toolbar = page.locator("#editor-toolbar");
	const more = toolbar.locator(".toolbar-overflow-trigger");
	await expect(more).toBeVisible();
	const visibleRows = await toolbar
		.locator(
			"#editor-actions > button:not(.toolbar-overflowed), #view-modes > button:not(.toolbar-overflowed), #format-bar > button:not(.toolbar-overflowed), .toolbar-overflow-trigger",
		)
		.evaluateAll((controls) => {
			const rendered = controls
				.map((control) => control.getBoundingClientRect())
				.filter((rect) => rect.width > 0 && rect.height > 0);
			return Array.from(new Set(rendered.map((rect) => Math.round(rect.top))));
		});
	expect(visibleRows).toHaveLength(1);

	await more.focus();
	await more.press("ArrowDown");
	const menu = toolbar.locator(".toolbar-overflow-menu");
	await expect(menu).toBeVisible();
	await expect(menu.locator("button").first()).toBeFocused();
	const sourceLabels = await toolbar
		.locator(".toolbar-overflowed")
		.evaluateAll((controls) =>
			controls.map(
				(control) =>
					control.getAttribute("aria-label")?.trim() ||
					control.getAttribute("title")?.trim() ||
					control.textContent?.trim() ||
					"Command",
			),
		);
	await expect(menu.locator("button")).toHaveText(sourceLabels);
	const toolbarBox = await toolbar.boundingBox();
	const menuBox = await menu.boundingBox();
	if (toolbarBox === null || menuBox === null)
		throw new Error("overflow menu geometry is missing");
	expect(menuBox.x).toBeGreaterThanOrEqual(toolbarBox.x);
	expect(menuBox.x + menuBox.width).toBeLessThanOrEqual(
		toolbarBox.x + toolbarBox.width,
	);
	await page.screenshot({
		path: testInfo.outputPath("toolbar-overflow-menu-narrow.png"),
		fullPage: true,
	});
	await menu.press("Escape");
	await expect(menu).toBeHidden();
	await expect(more).toBeFocused();

	await more.click();
	const formatted = menu.getByRole("menuitemradio", { name: "Formatted" });
	await expect(formatted).toBeVisible();
	await formatted.click();
	await expect(page.locator("#panes")).toHaveAttribute(
		"data-mode",
		"formatted",
	);
	await expect(menu).toBeHidden();
	await page.screenshot({
		path: testInfo.outputPath("toolbar-overflow-narrow.png"),
		fullPage: true,
	});

	await page.setViewportSize({ width: 2400, height: 900 });
	await expect(more).toBeHidden();
	await expect(toolbar.locator(".toolbar-overflowed")).toHaveCount(0);

	await page.evaluate(() => {
		document.documentElement.style.fontSize = "48px";
	});
	await expect(more).toBeVisible();

	await page.evaluate(() => {
		document.documentElement.style.removeProperty("font-size");
	});
	await expect(more).toBeHidden();
	await expect(toolbar.locator(".toolbar-overflowed")).toHaveCount(0);
	await page.screenshot({
		path: testInfo.outputPath("toolbar-overflow-wide.png"),
		fullPage: true,
	});
});
