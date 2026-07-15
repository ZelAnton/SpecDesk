import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("My reviews opens the native pull-request document without navigating to GitHub", async ({
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
  await waitForSent(page, "pr.list.request");
  const listRequest = (await sentFrames(page)).find(
    (frame) =>
      frame.kind === "pr.list.request" &&
      (frame.payload as { scope?: string } | undefined)?.scope === undefined,
  );
  if (listRequest?.id === undefined) throw new Error("missing My reviews correlation");

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: false },
  });
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "bob" },
  });
  const accountMenu = page.locator("#account-menu");
  if (!(await accountMenu.isVisible())) await page.locator("#github-btn").click();
  await page.locator("#reviews-btn").click();
  await expect
    .poll(
      async () =>
        (await sentFrames(page)).filter(
          (frame) =>
            frame.kind === "pr.list.request" &&
            (frame.payload as { scope?: string } | undefined)?.scope === undefined,
        ).length,
    )
    .toBe(2);
  const replacementListRequest = (await sentFrames(page))
    .filter(
      (frame) =>
        frame.kind === "pr.list.request" &&
        (frame.payload as { scope?: string } | undefined)?.scope === undefined,
    )
    .at(-1);
  if (replacementListRequest?.id === undefined) {
    throw new Error("missing replacement-account My reviews correlation");
  }

  await emit(page, {
    kind: "pr.list",
    id: listRequest.id,
    payload: {
      items: [
        {
          number: 1,
          title: "Retired account review",
          url: "https://github.com/old/spec/pull/1",
          repo: "old/spec",
          role: "reviewer",
          status: "inReview",
          label: "In review",
        },
      ],
      error: null,
    },
  });
  await expect(page.locator("#reviews-panel .review-open")).toHaveCount(0);
  await emit(page, {
    kind: "pr.list",
    id: replacementListRequest.id,
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
  await waitForSent(page, "pr.details.request");
  const detailsRequest = (await sentFrames(page))
    .filter((frame) => frame.kind === "pr.details.request")
    .at(-1);
  expect(detailsRequest?.payload).toEqual({ repo: "octo/spec", number: 42 });
  expect((await sentFrames(page)).filter((frame) => frame.kind === "link.open")).toHaveLength(0);
  if (detailsRequest?.id === undefined) throw new Error("missing PR details correlation");
  await emit(page, {
    kind: "pr.details",
    id: detailsRequest.id,
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

  const review = page.locator("#pull-request-view");
  await expect(review.locator("h1")).toHaveText("Clarify the refund window");
  await expect(review).toContainText("Description");
  await expect(review).toContainText("Make the customer-facing rule unambiguous.");
  await expect(review).toContainText("History");
  await expect(review).toContainText("Clarify refunds");
  await expect(review).toContainText("Comments");
  await expect(review).toContainText("Please make the date explicit.");
  await expect(page.locator("#context-panels")).toBeVisible();
  await expect(page.locator('[data-context="repository"]')).toContainText("octo/spec");
  await expect(page.locator('[data-context="pull-request"]')).toContainText("spec/refunds");
  await page.locator('[data-context="pull-request"]').click();
  await expect(
    page.locator('#left-dock .dock-rail-btn[aria-label="Change requests"]'),
  ).toHaveAttribute("aria-expanded", "true");
  await page.screenshot({
    path: testInfo.outputPath("my-reviews-native-document.png"),
    fullPage: true,
  });
});

test("opens a pull request, comments, and the selected-comment reader inside SpecDesk", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  const reviewItem = {
    number: 42,
    title: "Clarify the refund window",
    url: "https://github.com/octo/spec/pull/42",
    repo: "octo/spec",
    role: "author",
    status: "inReview",
    label: "In review",
  };
  const details = {
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
        kind: "review",
        path: "billing.md",
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
  };
  if (!(await page.locator("#left-dock .dock-main").isVisible())) {
    await page.locator('#left-dock .dock-rail-btn[aria-label="Change requests"]').click();
  }
  await waitForSent(page, "pr.list.request");
  const listRequest = (await sentFrames(page)).find(
    (frame) =>
      frame.kind === "pr.list.request" &&
      (frame.payload as { scope?: string } | undefined)?.scope === "pullRequests",
  );
  if (listRequest?.id === undefined) throw new Error("missing pull request list correlation");
  await emit(page, {
    kind: "pr.list",
    id: listRequest.id,
    payload: {
      items: [reviewItem],
      error: null,
    },
  });
  await page.locator(".remote-review-open").click();
  await waitForSent(page, "pr.details.request");
  const detailsRequest = (await sentFrames(page)).find(
    (frame) => frame.kind === "pr.details.request",
  );
  if (detailsRequest?.id === undefined) throw new Error("missing PR details correlation");
  await emit(page, {
    kind: "pr.details",
    id: detailsRequest.id,
    payload: details,
  });

  await expect(page.locator("#pull-request-view h1")).toHaveText("Clarify the refund window");
  await expect(page.locator("#pull-request-view")).toContainText("Checks: success");
  await page.locator('#right-dock .dock-rail-btn[aria-label="Comments"]').click();
  await expect(page.locator(".pr-comment-row")).toContainText("billing.md");
  const commentDraft = page.locator(".pr-comment-compose textarea");
  await commentDraft.fill("clarify this");
  await commentDraft.evaluate((input: HTMLTextAreaElement) => {
    input.setSelectionRange(0, 7);
    input.dispatchEvent(new Event("select"));
  });
  const commentFormat = page.getByRole("toolbar", { name: "Format selected text" });
  await expect(commentFormat).toBeHidden();
  await commentDraft.hover({ position: { x: 30, y: 16 } });
  await expect(commentFormat).toBeVisible();
  await commentFormat.getByTitle("Bold").click();
  await expect(commentDraft).toHaveValue("**clarify** this");
  await page.locator(".pr-comment-open").click();
  await expect(page.locator("#bottom-dock .selected-pr-comment")).toContainText(
    "Please make the date explicit.",
  );
  await page.screenshot({ path: testInfo.outputPath("pull-request-document.png"), fullPage: true });

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: false },
  });
  await expect(page.locator("#home-view")).toBeVisible();
  await expect(page.locator("#pull-request-view")).not.toContainText("Clarify the refund window");
  await expect(page.locator(".pr-comment-row")).toHaveCount(0);
  await expect(page.locator("#bottom-dock .selected-pr-comment")).not.toContainText(
    "Please make the date explicit.",
  );
  await expect(page.locator('#bottom-dock [aria-label="Application activity"]')).not.toContainText(
    "octo/spec",
  );

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  if (!(await page.locator("#left-dock .dock-main").isVisible())) {
    await page.locator('#left-dock .dock-rail-btn[aria-label="Change requests"]').click();
  }
  await waitForSent(page, "pr.list.request");
  const reopenedListRequest = (await sentFrames(page))
    .filter(
      (frame) =>
        frame.kind === "pr.list.request" &&
        (frame.payload as { scope?: string } | undefined)?.scope === "pullRequests",
    )
    .at(-1);
  if (reopenedListRequest?.id === undefined) throw new Error("missing reopened list correlation");
  await emit(page, {
    kind: "pr.list",
    id: reopenedListRequest.id,
    payload: { items: [reviewItem], error: null },
  });
  await page.locator(".remote-review-open").click();
  await waitForSent(page, "pr.details.request");
  const reopenedDetailsRequest = (await sentFrames(page))
    .filter((frame) => frame.kind === "pr.details.request")
    .at(-1);
  if (reopenedDetailsRequest?.id === undefined) throw new Error("missing reopened PR correlation");
  await emit(page, { kind: "pr.details", id: reopenedDetailsRequest.id, payload: details });
  await expect(page.locator("#pull-request-view h1")).toHaveText("Clarify the refund window");

  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]').click();
  await page.locator('#left-dock .nav-item[data-view="home"]').click();
  await expect(page.locator("#home-view")).toBeVisible();
  await expect(page.locator('#right-dock .dock-rail-btn[aria-label="Comments"]')).toBeHidden();
  await expect(page.locator("#pull-request-view")).not.toContainText("Clarify the refund window");
});
