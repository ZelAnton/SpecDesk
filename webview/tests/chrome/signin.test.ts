// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { SignInController } from "../../src/chrome/signin.js";

// Markup mirroring the GitHub account affordance + sign-in code bar in index.html (both start hidden).
function setupDom(): void {
  document.body.innerHTML = `
		<button id="github-btn" aria-expanded="false"><img id="account-avatar" hidden /><span id="account-avatar-fallback">person</span><span id="account-notification-count" hidden>0</span></button>
		<button id="github-auth-btn" hidden>Sign in</button>
    <span id="github-account-status" hidden></span>
    <div id="account-menu" role="menu" hidden>
			<button id="account-notifications" role="menuitem">Notifications</button>
			<button id="account-settings" role="menuitem" disabled>Settings (coming soon)</button>
      <button id="account-refresh" role="menuitem" hidden>Refresh GitHub access</button>
      <button id="account-signout" role="menuitem" hidden>Sign out</button>
    </div>
    <div id="github-signin-bar" hidden>
      <span id="github-signin-text"></span>
      <code id="github-user-code"></code>
      <button id="github-open-btn"></button>
      <span id="github-signin-status"></span>
      <button id="github-cancel-btn"></button>
    </div>
  `;
}

function el(id: string): HTMLElement {
  const found = document.querySelector(`#${id}`);
  if (!(found instanceof HTMLElement)) {
    throw new Error(`#${id} missing`);
  }
  return found;
}

function mount(copyText = vi.fn<(text: string) => Promise<void>>().mockResolvedValue(undefined)) {
  setupDom();
  const signIn = vi.fn();
  const cancelSignIn = vi.fn();
  const signOut = vi.fn();
  const refreshAccount = vi.fn();
  const openUrl = vi.fn();
  const controller = new SignInController({
    accountBtn: document.querySelector<HTMLButtonElement>("#github-btn"),
    avatar: document.querySelector<HTMLImageElement>("#account-avatar"),
    avatarFallback: document.querySelector<HTMLElement>("#account-avatar-fallback"),
    notificationCount: document.querySelector<HTMLElement>("#account-notification-count"),
    authBtn: document.querySelector<HTMLButtonElement>("#github-auth-btn"),
    accountStatus: document.querySelector<HTMLElement>("#github-account-status"),
    menu: document.querySelector<HTMLElement>("#account-menu"),
    connectBtn: null,
    refreshBtn: document.querySelector<HTMLButtonElement>("#account-refresh"),
    signOutBtn: document.querySelector<HTMLButtonElement>("#account-signout"),
    bar: document.querySelector<HTMLElement>("#github-signin-bar"),
    text: document.querySelector<HTMLElement>("#github-signin-text"),
    userCode: document.querySelector<HTMLElement>("#github-user-code"),
    openBtn: document.querySelector<HTMLButtonElement>("#github-open-btn"),
    status: document.querySelector<HTMLElement>("#github-signin-status"),
    cancelBtn: document.querySelector<HTMLButtonElement>("#github-cancel-btn"),
    signIn,
    cancelSignIn,
    signOut,
    refreshAccount,
    openUrl,
    copyText,
  });
  return { controller, signIn, cancelSignIn, signOut, refreshAccount, openUrl, copyText };
}

describe("SignInController — account button", () => {
  it("keeps the general account menu available when GitHub sign-in is unavailable", () => {
    const { controller } = mount();
    controller.applyAccount({ available: false, signedIn: false });
    expect(el("github-btn").hidden).toBe(false);
    expect(el("account-signout").hidden).toBe(true);
  });

  it("offers Sign in only on the toolbar while signed out", () => {
    const { controller, signIn } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    expect(el("github-auth-btn").textContent).toBe("Sign in");
    expect(el("github-auth-btn").hidden).toBe(false);
    el("github-auth-btn").click();
    expect(signIn).toHaveBeenCalledTimes(1);
    el("github-btn").click();
    expect(el("account-menu").hidden).toBe(false);
    expect(signIn).toHaveBeenCalledTimes(1);
  });

  it("shows the handle in the accessible account name, and signs out from the menu", () => {
    const { controller, signOut } = mount();
    controller.applyAccount({ available: true, signedIn: true, login: "octocat" });
    expect(el("github-btn").getAttribute("aria-label")).toBe("Account, signed in as @octocat");
    expect(el("github-auth-btn").hidden).toBe(true);
    el("github-btn").click();
    expect(el("account-signout").textContent).toBe("Sign out @octocat");
    el("account-signout").click();
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it("renders GitHub's avatar and falls back to the neutral account glyph on load error", () => {
    const { controller } = mount();
    controller.applyAccount({
      available: true,
      signedIn: true,
      login: "octocat",
      avatarUrl: "https://avatars.githubusercontent.com/u/583231?v=4",
    });
    const avatar = el("account-avatar") as HTMLImageElement;
    expect(avatar.hidden).toBe(false);
    expect(avatar.alt).toBe("GitHub avatar for @octocat");
    expect(el("account-avatar-fallback").hidden).toBe(true);

    avatar.dispatchEvent(new Event("error"));
    expect(avatar.hidden).toBe(true);
    expect(el("account-avatar-fallback").hidden).toBe(false);
  });

  it("overlays a bounded notification count on the avatar", () => {
    const { controller } = mount();
    controller.setNotificationCount(125);
    expect(el("account-notification-count").hidden).toBe(false);
    expect(el("account-notification-count").textContent).toBe("99+");
    expect(el("github-btn").getAttribute("aria-label")).toContain("125 notifications");
    controller.applyAccount({
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: [],
    });
    expect(el("github-btn").getAttribute("aria-label")).toBe(
      "Account, signed in as @octocat, 125 notifications",
    );
    controller.setNotificationCount(0);
    expect(el("account-notification-count").hidden).toBe(true);
    expect(el("github-btn").getAttribute("aria-label")).toBe("Account, signed in as @octocat");
  });

  it("shows the account and authorized organizations in the status bar", () => {
    const { controller } = mount();
    controller.applyAccount({
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: ["acme", "octo-labs"],
    });

    expect(el("github-account-status").hidden).toBe(false);
    expect(el("github-account-status").textContent).toBe(
      "GitHub: @octocat · Organizations: acme, octo-labs",
    );
    controller.applyAccount({ available: true, signedIn: false });
    expect(el("github-account-status").hidden).toBe(true);
  });

  it("refreshes newly approved GitHub access explicitly and shows progress", () => {
    const { controller, refreshAccount } = mount();
    controller.applyAccount({
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: [],
    });

    el("github-btn").click();
    expect(el("account-refresh").hidden).toBe(false);
    el("account-refresh").click();

    expect(refreshAccount).toHaveBeenCalledOnce();
    expect(el("account-menu").hidden).toBe(true);
    expect(el("account-refresh").textContent).toContain("Refreshing");
    expect((el("account-refresh") as HTMLButtonElement).disabled).toBe(true);

    controller.applyAccount({
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: ["newly-approved"],
    });
    expect(el("account-refresh").textContent).toBe("Refresh GitHub access");
    expect((el("account-refresh") as HTMLButtonElement).disabled).toBe(false);
    expect(el("github-account-status").textContent).toContain("newly-approved");
  });

  it("refreshes a stale focused account once and throttles subsequent focus events", () => {
    const { controller, refreshAccount } = mount();
    controller.applyAccount({
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: ["acme"],
    });

    expect(controller.refreshIfStale(Date.now() + 4 * 60 * 1000)).toBe(false);
    expect(controller.refreshIfStale(Date.now() + 6 * 60 * 1000)).toBe(true);
    expect(controller.refreshIfStale(Date.now() + 12 * 60 * 1000)).toBe(false);
    expect(refreshAccount).toHaveBeenCalledOnce();
  });

  it("replaces the organization loading state with actionable failure text", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: true, login: "octocat" });
    expect(el("github-account-status").textContent).toContain("loading");

    controller.applyAccount({
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: [],
      message: "Organizations unavailable — refresh GitHub access.",
    });
    expect(el("github-account-status").textContent).toContain("refresh GitHub access");
  });

  it("falls back to a plain Sign out when the handle is unknown", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: true, login: "" });
    expect(el("account-signout").textContent).toBe("Sign out");
  });

  it("supports ArrowDown to open and Escape to return focus", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    el("github-btn").dispatchEvent(
      new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }),
    );
    expect(el("account-menu").hidden).toBe(false);
    expect(document.activeElement).toBe(el("account-notifications"));
    el("account-notifications").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
    expect(el("account-menu").hidden).toBe(true);
    expect(document.activeElement).toBe(el("github-btn"));
  });

  it("supports Home and End across the visible menu items", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    el("github-btn").click();

    el("account-notifications").dispatchEvent(
      new KeyboardEvent("keydown", { key: "End", bubbles: true }),
    );
    expect(document.activeElement).toBe(el("account-notifications"));
    el("account-notifications").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Home", bubbles: true }),
    );
    expect(document.activeElement).toBe(el("account-notifications"));
  });
});

describe("SignInController — code bar", () => {
  it("reveals the code and opens GitHub immediately, with a retry button", () => {
    const { controller, openUrl } = mount();
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });

    expect(el("github-signin-bar").hidden).toBe(false);
    expect(el("github-user-code").textContent).toBe("WXYZ-1234");
    expect(el("github-signin-status").textContent).toContain("Waiting");

    expect(openUrl).toHaveBeenCalledTimes(1);
    expect(openUrl).toHaveBeenLastCalledWith("https://github.com/login/device");

    el("github-open-btn").click();
    expect(openUrl).toHaveBeenCalledTimes(2);
  });

  it("copies the code before opening GitHub and reports success", async () => {
    const copyText = vi.fn<(text: string) => Promise<void>>().mockResolvedValue(undefined);
    const { controller, openUrl } = mount(copyText);

    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    await Promise.resolve();

    expect(copyText).toHaveBeenCalledWith("WXYZ-1234");
    expect(copyText.mock.invocationCallOrder[0]).toBeLessThan(
      openUrl.mock.invocationCallOrder[0] ?? 0,
    );
    expect(el("github-signin-text").textContent).toContain("Code copied");
  });

  it("keeps the visible code when clipboard access is denied", async () => {
    const copyText = vi
      .fn<(text: string) => Promise<void>>()
      .mockRejectedValue(new Error("denied"));
    const { controller } = mount(copyText);

    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    await Promise.resolve();

    expect(el("github-signin-text").textContent).toContain("enter this code");
    expect(el("github-user-code").textContent).toBe("WXYZ-1234");
  });

  it("does not overwrite a terminal sign-in error when clipboard copying finishes late", async () => {
    let finishCopy: (() => void) | undefined;
    const copyText = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          finishCopy = resolve;
        }),
    );
    const { controller } = mount(copyText);
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    controller.applyAccount({
      available: true,
      signedIn: false,
      message: "Your sign-in code expired.",
    });

    finishCopy?.();
    await Promise.resolve();

    expect(el("github-signin-text").textContent).toBe("Your sign-in code expired.");
  });

  it("cancels and closes the bar on cancel", () => {
    const { controller, cancelSignIn } = mount();
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    el("github-cancel-btn").click();
    expect(cancelSignIn).toHaveBeenCalledTimes(1);
    expect(el("github-signin-bar").hidden).toBe(true);
  });

  it("keeps the account menu trigger available while connecting", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    expect(el("github-btn").hidden).toBe(false);
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    expect(el("github-btn").hidden).toBe(false);
    controller.applyAccount({ available: true, signedIn: true, login: "octocat" });
    expect(el("github-btn").hidden).toBe(false); // restored by the terminal event
  });

  it("hides the bar on a successful connection", () => {
    const { controller } = mount();
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    controller.applyAccount({ available: true, signedIn: true, login: "octocat" });
    expect(el("github-signin-bar").hidden).toBe(true);
    expect(el("account-signout").textContent).toBe("Sign out @octocat");
  });

  it("keeps the bar with a plain-language message when sign-in fails", () => {
    const { controller } = mount();
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    controller.applyAccount({
      available: true,
      signedIn: false,
      message: "Your sign-in code expired.",
    });

    expect(el("github-signin-bar").hidden).toBe(false);
    expect(el("github-signin-text").textContent).toBe("Your sign-in code expired.");
    expect(el("github-user-code").hidden).toBe(true);
    expect(el("github-cancel-btn").textContent).toBe("Close");
  });

  it("surfaces an up-front failure even though no code was shown", () => {
    // StartSignInAsync failed before a code was issued — the bar was never up, but the error must show.
    const { controller } = mount();
    controller.applyAccount({
      available: true,
      signedIn: false,
      message: "Couldn't reach GitHub. Check your connection and try again.",
    });

    expect(el("github-signin-bar").hidden).toBe(false);
    expect(el("github-signin-text").textContent).toContain("Couldn't reach GitHub");
  });
});
