import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

// The persisted workspace store the host serves on `workspace.request`. `intro.md` is both recent AND a
// favorite; `repo` is a recent folder that is not a favorite.
const STATE = {
  kind: "workspace.state",
  payload: {
    recent: [
      { path: "C:\\specs\\repo", label: "repo", isFolder: true },
      { path: "C:\\specs\\repo\\intro.md", label: "intro.md", isFolder: false },
    ],
    favorites: [{ path: "C:\\specs\\repo\\intro.md", label: "intro.md", isFolder: false }],
    repositories: [{ id: "acme/specs", name: "acme/specs", url: "https://github.com/acme/specs" }],
  },
};

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

/** Select a left-rail mode; choosing an inactive mode opens its panel in the same action. */
async function openPanel(page: import("@playwright/test").Page, label: string): Promise<void> {
  await page.locator(`#left-dock .dock-rail-btn[aria-label="${label}"]`).click();
}

test("requests the workspace store on startup and populates the Recent panel", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");

  // The startup `workspace.request` (sent before `ready`) primes the panels.
  expect((await sentFrames(page)).some((f) => f.kind === "workspace.request")).toBe(true);

  await emit(page, STATE);
  await openPanel(page, "Recent");

  const recent = page.locator('#left-dock [data-tool="recent"]');
  await expect(recent.locator(".workspace-open")).toHaveText(["repo", "intro.md"]);

  // A file row opens the document (a plain file open does not reveal Files, so the Recent panel stays up).
  await recent.locator(".workspace-open", { hasText: "intro.md" }).click();
  expect((await sentFrames(page)).find((f) => f.kind === "doc.open")?.payload).toMatchObject({
    path: "C:\\specs\\repo\\intro.md",
  });

  // The `repo` folder is not a favorite yet — its star adds it (done before the folder open below, which
  // reveals the Files navigator and switches the left dock away from Recent).
  await recent.locator(".workspace-list-row", { hasText: "repo" }).locator(".workspace-star").click();
  expect((await sentFrames(page)).find((f) => f.kind === "workspace.favorite")?.payload).toMatchObject({
    path: "C:\\specs\\repo",
    favorite: true,
  });

  // A folder row opens the folder (and reveals Files).
  await recent.locator(".workspace-open", { hasText: "repo" }).click();
  expect((await sentFrames(page)).find((f) => f.kind === "folder.open")?.payload).toMatchObject({
    path: "C:\\specs\\repo",
  });
});

test("the Favorites panel lists the starred items", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, STATE);
  await openPanel(page, "Favorites");

  const favorites = page.locator('#left-dock [data-tool="favorites"]');
  await expect(favorites.locator(".workspace-open")).toHaveText(["intro.md"]);
  // A favorite's trailing star is a pressed toggle whose tooltip offers to remove it.
  const star = favorites.locator(".workspace-star");
  await expect(star).toHaveAttribute("aria-pressed", "true");
  await expect(star).toHaveAttribute("title", "Remove from favorites");
});

test("the Repositories panel opens a repo and registers a new one", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, STATE);
  await openPanel(page, "Repositories");

  const repos = page.locator('#left-dock [data-tool="repositories"]');
  await expect(repos.locator(".repo-open")).toHaveText(["acme/specs"]);

  // The clone menu sends `repo.cloneManaged` with the typed value (done first: opening a repo below reveals
  // the Files navigator and switches the left dock away from Repositories).
  await repos.locator(".repo-register-input").fill("owner/name");
  await waitForSent(page, "repo.cloneDestination.request");
  const destinationRequest = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.cloneDestination.request",
  );
  await emit(page, {
    kind: "repo.cloneDestination",
    payload: {
      url: "owner/name",
      requestId: (destinationRequest?.payload as { requestId: number }).requestId,
      path: "C:\\SpecDesk\\repos\\owner_name",
    },
  });
  await waitForSent(page, "repo.description.request");
  const descriptionRequest = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.description.request",
  );
  await emit(page, {
    kind: "repo.description",
    payload: {
      url: "owner/name",
      requestId: (descriptionRequest?.payload as { requestId: number }).requestId,
      state: "found",
      description: "Repository description",
    },
  });
  await repos.locator(".repo-register-add").click();
  await repos.locator('[role="menuitem"]').filter({ hasText: /^Clone…$/ }).click();
  await repos.locator(".repo-clone-confirm-yes").click();
  expect((await sentFrames(page)).find((f) => f.kind === "repo.cloneManaged")?.payload).toMatchObject({
    url: "owner/name",
    destinationPath: "C:\\SpecDesk\\repos\\owner_name",
  });

  // A6: clicking a repo clones it into a managed folder and opens it — sends `repo.open`.
  await repos.locator(".repo-open").click();
  expect((await sentFrames(page)).find((f) => f.kind === "repo.open")?.payload).toMatchObject({
    url: "https://github.com/acme/specs",
  });
});
