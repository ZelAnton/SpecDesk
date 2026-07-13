import { expect, test } from "@playwright/test";
import { openDockTool } from "../lib/dock";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("repository autocomplete matches by repo name and keeps selection non-mutating", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.repositories",
    payload: {
      repositories: [
        { fullName: "acme/specifications", description: "Product specifications" },
        { fullName: "octocat/notes" },
      ],
    },
  });

  await openDockTool(page, "left", "Repositories");
  const input = page.locator(".repo-register-input");
  await input.fill("spec");
  await expect(page.locator('[role="option"]')).toHaveText(["acme/specifications"]);
  await expect(input).toHaveAttribute("aria-expanded", "true");
  await page.screenshot({ path: testInfo.outputPath("autocomplete.png"), fullPage: true });

  await input.press("Enter");
  await expect(input).toHaveValue("acme/specifications");
  expect(
    (await sentFrames(page)).some((frame) =>
      ["repo.cloneManaged", "repo.cloneToFolder"].includes(frame.kind),
    ),
  ).toBe(false);
});
