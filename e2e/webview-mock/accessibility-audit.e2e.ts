import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page, type TestInfo } from "@playwright/test";
import { openDockTool } from "../lib/dock";
import { emit, installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

/**
 * Layer 1 accessibility gate (design concept §11: WCAG AA, full keyboard reach, aria-correct chrome).
 * Runs an axe-core scan (WCAG 2.0/2.1 A + AA rules) against the built bundle's real DOM on every key
 * surface — the editor in its three view modes, the workspace-panel left rail, the inline prompt bars
 * (dialogs.ts), the native pull-request document, and the GitHub account/sign-in chrome — each in both
 * the light (default) and dark (`data-theme="dark"`) theme. Only `serious`/`critical` violations fail
 * the run (see {@link SERIOUS_IMPACTS}); anything below that threshold is informational and does not
 * block CI, matching the criteria for this gate.
 *
 * A hidden element (an unopened panel, a closed prompt bar) is excluded from axe's evaluation by the
 * rules themselves (axe does not flag `display:none`/`hidden` subtrees), so scanning the whole
 * `document` on every call — rather than `.include()`-ing just the surface under test — is both
 * simpler and still surface-scoped in practice: only what is actually revealed at that point is judged.
 */

type Theme = "light" | "dark";
const THEMES: readonly Theme[] = ["light", "dark"];
const SERIOUS_IMPACTS = new Set(["serious", "critical"]);

/**
 * Known, not-yet-fixed serious/critical violations, sanctioned explicitly here (never a silent skip).
 * Matched by BOTH the surface label and the axe rule id, so a new serious/critical violation — on this
 * rule elsewhere, or on any other rule on this surface — still fails the scan. Discovered by this very
 * gate on first run (the criteria for this task expects and wants that); each is a genuine pre-existing
 * issue, out of scope for this test-only task, and tracked here rather than fixed silently or skipped.
 */
const KNOWN_A11Y_REASONS: Readonly<Record<string, string>> = {
  "aria-allowed-attr":
    'The persistent dock rail\'s mode buttons (left/right rail, workspace/dock.ts) pair role="radio" ' +
    "(the mode selection) with aria-expanded (whether that mode's panel is open); ARIA 1.2 does not " +
    "list aria-expanded among role=radio's supported states, so every rail button is flagged on every " +
    "surface that shows the rail (effectively all of them). Pre-existing dock-rail markup; a fix needs " +
    "its own follow-up (e.g. a separate aria-live/description instead of aria-expanded on the radio).",
  "aria-input-field-name":
    "CodeMirror's own generated .cm-content contenteditable region carries no accessible name in the " +
    "raw/split source editor. It is still fully keyboard-operable and is reached via the labeled " +
    "view-mode toolbar; a fix would need an aria-label wired through CodeMirror's own configuration. " +
    "Pre-existing CodeMirror integration gap, out of scope for this test-only gate addition.",
  "color-contrast":
    "A handful of existing surfaces fall slightly under the AA contrast ratio in one or both themes: " +
    "the segmented view-mode buttons' inactive state (#mode-code/#mode-split/#mode-formatted), " +
    "CodeMirror gutter line numbers, panel section headings/hint text in Navigator, Repositories, and " +
    "Change requests, the pull-request check-state badge, and the GitHub sign-in status line. " +
    "Pre-existing across the design's current token application; tracked for a follow-up contrast pass, " +
    "out of scope for this test-only gate addition.",
};

/** The exact (surface, rule) pairs this gate's own first run found — see {@link KNOWN_A11Y_REASONS} for
 *  why each is sanctioned rather than fixed here. A surface not listed, or a rule not listed for a
 *  listed surface, still fails the scan on any serious/critical violation. */
const KNOWN_EXCEPTION_SURFACES: ReadonlyArray<{ surface: string; ruleIds: readonly string[] }> = [
  { surface: "editor — split", ruleIds: ["aria-allowed-attr", "aria-input-field-name", "color-contrast"] },
  {
    surface: "editor — raw (code)",
    ruleIds: ["aria-allowed-attr", "aria-input-field-name", "color-contrast"],
  },
  { surface: "editor — formatted", ruleIds: ["aria-allowed-attr", "color-contrast"] },
  { surface: "left rail — Navigator", ruleIds: ["aria-allowed-attr", "color-contrast"] },
  { surface: "left rail — Repositories", ruleIds: ["aria-allowed-attr", "color-contrast"] },
  { surface: "left rail — Change requests", ruleIds: ["aria-allowed-attr", "color-contrast"] },
  {
    surface: "left rail — Disk",
    ruleIds: ["aria-allowed-attr", "aria-input-field-name", "color-contrast"],
  },
  {
    surface: "dialogs — name new draft",
    ruleIds: ["aria-allowed-attr", "aria-input-field-name", "color-contrast"],
  },
  {
    surface: "dialogs — describe changes",
    ruleIds: ["aria-allowed-attr", "aria-input-field-name", "color-contrast"],
  },
  {
    surface: "dialogs — send for review",
    ruleIds: ["aria-allowed-attr", "aria-input-field-name", "color-contrast"],
  },
  { surface: "pull-request document", ruleIds: ["aria-allowed-attr", "color-contrast"] },
  { surface: "GitHub account status", ruleIds: ["aria-allowed-attr"] },
  { surface: "GitHub sign-in code prompt", ruleIds: ["aria-allowed-attr", "color-contrast"] },
];

const KNOWN_EXCEPTIONS: ReadonlyArray<{ surface: string; ruleId: string; reason: string }> =
  KNOWN_EXCEPTION_SURFACES.flatMap(({ surface, ruleIds }) =>
    ruleIds.map((ruleId) => ({ surface, ruleId, reason: KNOWN_A11Y_REASONS[ruleId] ?? "" })),
  );

/** Light is the bare `:root` (no attribute); dark sets `data-theme="dark"` — mirrors `applyTheme` in
 *  `src/index.ts` exactly, without going through the account-menu toggle (immaterial to a contrast/aria
 *  scan and avoids coupling every surface's scan to the menu's own open/closed state). */
async function setTheme(page: Page, theme: Theme): Promise<void> {
  await page.evaluate((t) => {
    if (t === "dark") {
      document.documentElement.dataset.theme = "dark";
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
  }, theme);
}

/** Run axe-core (WCAG 2.0/2.1 A + AA) and fail only on unsanctioned serious/critical violations. */
async function assertNoSeriousViolations(
  page: Page,
  testInfo: TestInfo,
  surface: string,
  theme: Theme,
): Promise<void> {
  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  const serious = results.violations.filter((violation) => SERIOUS_IMPACTS.has(violation.impact ?? ""));
  const unsanctioned = serious.filter(
    (violation) =>
      !KNOWN_EXCEPTIONS.some(
        (exception) => exception.surface === surface && exception.ruleId === violation.id,
      ),
  );
  if (unsanctioned.length > 0) {
    const details = unsanctioned
      .map(
        (violation) =>
          `- ${violation.id} (${violation.impact}): ${violation.help} — ${violation.nodes.length} node(s): ${violation.nodes
            .map((node) => node.target.join(" "))
            .join(", ")}`,
      )
      .join("\n");
    testInfo.annotations.push({
      type: "a11y-violations",
      description: `${surface} (${theme})\n${details}`,
    });
    throw new Error(
      `Accessibility scan found unaddressed serious/critical violation(s) on "${surface}" (${theme} theme):\n${details}`,
    );
  }
}

/** Wait for the newest sent frame of `kind` and return its correlation id (the reply the mock host must
 *  echo back for the awaiting `ipc.request()` to resolve). */
async function correlationId(page: Page, kind: string): Promise<string> {
  await waitForSent(page, kind);
  const frame = (await sentFrames(page)).filter((f) => f.kind === kind).at(-1);
  if (frame?.id === undefined) {
    throw new Error(`missing correlation id for "${kind}"`);
  }
  return frame.id;
}

/** Select a view mode via its segmented-control button, or the measured overflow menu if the button
 *  itself does not fit at the current toolbar width (see toolbar-shell.e2e.ts for the same fallback). */
async function switchViewMode(
  page: Page,
  mode: "code" | "split" | "formatted",
  label: "Code" | "Split" | "Formatted",
): Promise<void> {
  const trigger = page.locator(`#mode-${mode}`);
  if (await trigger.isVisible()) {
    await trigger.click();
  } else {
    await page.locator("#editor-toolbar .toolbar-overflow-trigger").click();
    await page
      .locator("#editor-toolbar .toolbar-overflow-menu")
      .getByRole("menuitemradio", { name: label })
      .click();
  }
  await expect(page.locator(`#panes[data-mode="${mode}"]`)).toHaveCount(1);
}

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("editor surfaces (raw, split, formatted) have no serious/critical accessibility violations", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, {
    path: "C:\\work\\specs\\guides\\intro.md",
    docDir: "guides",
    text:
      "# Intro\n\nA paragraph with **bold** and _italic_ text, and [a link](https://example.com).\n\n" +
      "- One\n- Two\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n",
  });

  // The default mode is Split (see index.html's `#panes[data-mode="split"]`).
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "editor — split", theme);
  }
  await switchViewMode(page, "code", "Code");
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "editor — raw (code)", theme);
  }
  await switchViewMode(page, "formatted", "Formatted");
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "editor — formatted", theme);
  }
});

test("workspace-panel left rail (Navigator, Repositories, Change requests, Disk) has no serious/critical accessibility violations", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "workspace.state",
    payload: {
      recent: [
        { path: "C:\\specs\\repo", label: "repo", isFolder: true },
        { path: "C:\\specs\\repo\\intro.md", label: "intro.md", isFolder: false },
      ],
      favorites: [{ path: "C:\\specs\\repo\\intro.md", label: "intro.md", isFolder: false }],
      repositories: [
        {
          id: "acme/specs",
          name: "acme/specs",
          url: "https://github.com/acme/specs",
          defaultBranch: "main",
          clones: [],
        },
      ],
    },
  });

  await openDockTool(page, "left", "Navigator");
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "left rail — Navigator", theme);
  }

  await openDockTool(page, "left", "Repositories");
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "left rail — Repositories", theme);
  }

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await page.locator('#left-dock .dock-rail-btn[aria-label="Change requests"]').click();
  const changeRequestsId = await correlationId(page, "pr.list.request");
  await emit(page, {
    kind: "pr.list",
    id: changeRequestsId,
    payload: {
      items: [
        {
          number: 1,
          title: "Clarify the refund window",
          url: "https://github.com/acme/specs/pull/1",
          repo: "acme/specs",
          role: "author",
          status: "inReview",
          label: "In review",
        },
      ],
      error: null,
    },
  });
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "left rail — Change requests", theme);
  }

  await emit(page, {
    kind: "tree",
    payload: {
      root: "C:\\specs\\repo",
      requestId: 0,
      nodes: [
        {
          name: "guides",
          path: "C:\\specs\\repo\\guides",
          isDirectory: true,
          children: [],
          hasChildren: true,
        },
        {
          name: "README.md",
          path: "C:\\specs\\repo\\README.md",
          isDirectory: false,
          children: [],
          hasChildren: false,
        },
      ],
    },
  });
  await openDockTool(page, "left", "Disk");
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "left rail — Disk", theme);
  }
});

test("the inline prompt bars (dialogs.ts) have no serious/critical accessibility violations", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, {
    path: "C:\\work\\specs\\guides\\intro.md",
    docDir: "guides",
    text: "# Intro\n\nBody.\n",
  });

  // "Name new draft" — offered from Edit while the document is still published.
  await page.locator("#edit-btn").click();
  const branchId = await correlationId(page, "branch.name.request");
  await emit(page, { kind: "branch.name.suggested", id: branchId, payload: { name: "spec/audit-a11y" } });
  await expect(page.locator("#branch-name-bar")).toBeVisible();
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "dialogs — name new draft", theme);
  }
  await page.locator("#branch-name-cancel").click();
  await expect(page.locator("#branch-name-bar")).toBeHidden();

  // "Please describe changes" and "Send for review" both require a draft already in progress.
  await emit(page, {
    kind: "status",
    payload: { state: "draft", label: "Saved", branch: "spec/audit-a11y" },
  });
  await page.locator("#save-version-btn").click();
  const versionId = await correlationId(page, "version.note.request");
  await emit(page, {
    kind: "version.note.suggested",
    id: versionId,
    payload: { note: "Clarify the refund window" },
  });
  await expect(page.locator("#version-note-bar")).toBeVisible();
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "dialogs — describe changes", theme);
  }
  await page.locator("#version-note-cancel").click();
  await expect(page.locator("#version-note-bar")).toBeHidden();

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await page.locator("#send-for-review-btn").click();
  const prTextId = await correlationId(page, "pr.suggested.request");
  await emit(page, {
    kind: "pr.suggested",
    id: prTextId,
    payload: { title: "Clarify the refund window", body: "Make the customer-facing rule unambiguous." },
  });
  await expect(page.locator("#pr-text-bar")).toBeVisible();
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "dialogs — send for review", theme);
  }
  await page.locator("#pr-text-cancel").click();
});

test("the native pull-request document has no serious/critical accessibility violations", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await page.locator("#github-btn").click();
  await page.locator("#reviews-btn").click();
  const listId = await correlationId(page, "pr.list.request");
  await emit(page, {
    kind: "pr.list",
    id: listId,
    payload: {
      items: [
        {
          number: 42,
          title: "Clarify the refund window",
          url: "https://github.com/octo/spec/pull/42",
          repo: "octo/spec",
          role: "reviewer",
          status: "inReview",
          label: "In review",
        },
      ],
      error: null,
    },
  });
  await page.locator("#reviews-panel .review-open").click();
  const detailsId = await correlationId(page, "pr.details.request");
  await emit(page, {
    kind: "pr.details",
    id: detailsId,
    payload: {
      number: 42,
      repo: "octo/spec",
      title: "Clarify the refund window",
      body: "Make the customer-facing rule unambiguous.",
      url: "https://github.com/octo/spec/pull/42",
      state: "open",
      isDraft: false,
      author: "alice",
      authorAvatarUrl: "",
      baseBranch: "main",
      headBranch: "spec/refunds",
      reviewers: [{ login: "sam", avatarUrl: "", kind: "user" }],
      comments: [
        {
          id: 9,
          kind: "conversation",
          path: "",
          author: "sam",
          avatarUrl: "",
          body: "Please make the date explicit.",
          createdAt: "2026-07-14T10:00:00Z",
          updatedAt: "2026-07-14T10:00:00Z",
          viewerDidAuthor: false,
        },
      ],
      commentsIncomplete: false,
      commitsIncomplete: false,
      commits: [
        {
          oid: "abcdef",
          shortOid: "abcdef0",
          title: "Clarify refunds",
          when: "2026-07-14T09:00:00Z",
          checkState: "success",
        },
      ],
      error: null,
    },
  });
  await expect(page.locator("#pull-request-view h1")).toHaveText("Clarify the refund window");
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "pull-request document", theme);
  }
});

test("GitHub account status and sign-in chrome have no serious/critical accessibility violations", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");

  // Signed in: avatar identity, status bar, and the open account menu.
  await emit(page, {
    kind: "github.account",
    payload: {
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: ["acme", "octo-labs"],
      avatarUrl: "https://avatars.githubusercontent.com/u/583231?v=4",
    },
  });
  await page.locator("#github-btn").click();
  await expect(page.locator("#account-menu")).toBeVisible();
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "GitHub account status", theme);
  }
  await page.locator("#github-btn").click();
  await expect(page.locator("#account-menu")).toBeHidden();

  // Signed out, then a one-time device code offered for sign-in.
  await emit(page, { kind: "github.account", payload: { available: true, signedIn: false } });
  await expect(page.locator("#github-auth-btn")).toBeVisible();
  await emit(page, {
    kind: "github.code",
    payload: { userCode: "ABCD-1234", verificationUri: "https://github.com/login/device" },
  });
  await expect(page.locator("#github-signin-bar")).toBeVisible();
  for (const theme of THEMES) {
    await setTheme(page, theme);
    await assertNoSeriousViolations(page, testInfo, "GitHub sign-in code prompt", theme);
  }
});
