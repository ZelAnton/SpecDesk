import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import { type FullApp, launchAndAttach, stopAndDump } from "../lib/full-app";
import { waitForGeometrySettle } from "../lib/geometry";

// Layer 2: drive the REAL SpecDesk.Host.exe over CDP through the whole Edit → type → Save-version flow
// and assert the NATIVE effects the mock host can't — the working copy autosaved to DISK, and Save
// version committed to the real git repo on the host-suggested branch. One app for the file (serial).
// Serial (one real app for the file); a longer timeout than the 30s default so the sequential
// autosave (8s) + git-commit (15s) polls have headroom to surface their descriptive message on a slow
// run rather than a generic test timeout.
test.describe.configure({ mode: "serial", timeout: 60_000 });

const EDIT_MARKER = "AT5-autosave-marker-line";
const VERSION_NOTE = "A-T5 saved version note";

let ctx: FullApp;

test.beforeAll(async () => {
  ctx = await launchAndAttach();
});

test.afterAll(async ({}, testInfo) => {
  await stopAndDump(ctx, testInfo);
});

/** Read a file, swallowing a transient read error (the host may be mid-write) so `expect.poll` retries. */
function readOrEmpty(path: string): string {
  try {
    return readFileSync(path, "utf8");
  } catch {
    return "";
  }
}

/** `git` output in the fixture repo, swallowing a transient failure so `expect.poll` retries. */
function git(repo: string, ...args: string[]): string {
  try {
    return execFileSync("git", args, { cwd: repo, encoding: "utf8" });
  } catch {
    return "";
  }
}

test("Edit -> type -> autosave-to-disk -> Save version commits to the fixture repo on a spec/ branch", async ({}, testInfo) => {
  const { page, app } = ctx;

  // The app auto-loaded the fixture welcome.md; both real editors are up.
  await expect(page.locator("#editor .cm-editor")).toHaveCount(1);
  await expect(page.locator("#formatted .ProseMirror")).toHaveCount(1);
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "home");
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]').click();
  await page.locator('#left-dock .dock-rail-btn[aria-label="Editor"]').click();
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "editor");
  await waitForGeometrySettle(page);

  // 1) Edit -> the host suggests a draft name from .spectool.toml's `spec/{docSlug}-{date:yyyyMMdd}`.
  await page.locator("#edit-btn").click();
  await expect(page.locator("#branch-name-bar")).toBeVisible();
  const branchInput = page.locator("#branch-name-input");
  await expect(branchInput).not.toHaveValue("");
  const suggested = await branchInput.inputValue();
  expect(suggested, `suggested draft name "${suggested}" should match spec/welcome-YYYYMMDD`).toMatch(
    /^spec\/welcome-\d{8}$/,
  );
  await page.locator("#branch-name-confirm").click();

  // 2) Editing state: Save version becomes available (it is hidden in the published state).
  await expect(page.locator("#save-version-btn")).toBeVisible();

  // 3) Type into the REAL CodeMirror, then wait for the working-copy autosave (~1500ms idle) to hit DISK.
  await page.locator("#editor .cm-content").click();
  await page.keyboard.type(`\n${EDIT_MARKER}\n`);
  await expect
    .poll(() => readOrEmpty(app.welcome), {
      message: "the typed text should autosave to the fixture welcome.md on disk",
      timeout: 8000,
    })
    .toContain(EDIT_MARKER);

  // 4) Save version -> the note prompt -> type a note -> confirm.
  await page.locator("#save-version-btn").click();
  await expect(page.locator("#version-note-bar")).toBeVisible();
  await page.locator("#version-note-input").fill(VERSION_NOTE);
  await page.locator("#version-note-confirm").click();

  // 5) The NATIVE git effect: a commit carrying the note lands ON the host-suggested spec/ branch in the
  // real repo. Scoping `git log` to `suggested` (not `--all`) proves the commit is on THAT branch — not
  // merely that some commit with the note and some spec/ branch each exist independently.
  await expect
    .poll(() => git(app.repo, "log", suggested, "--format=%s"), {
      message: "Save version should commit the note onto the suggested spec/ branch",
      timeout: 15_000,
    })
    .toContain(VERSION_NOTE);
  expect(
    git(app.repo, "branch", "--list", "spec/*").trim(),
    "a spec/ branch should exist",
  ).not.toBe("");

  // 6) The chrome reflects the saved version (author-facing wording, never git vocabulary).
  await expect(page.locator("#status")).toContainText("Version saved");

  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
