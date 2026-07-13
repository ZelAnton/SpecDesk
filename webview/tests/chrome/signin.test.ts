// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { SignInController } from "../../src/chrome/signin.js";

// Markup mirroring the GitHub account affordance + sign-in code bar in index.html (both start hidden).
function setupDom(): void {
  document.body.innerHTML = `
    <button id="github-btn" aria-expanded="false">Account</button>
    <div id="account-menu" role="menu" hidden>
      <button id="account-settings" role="menuitem">Settings</button>
      <button id="account-connect" role="menuitem" hidden>Connect to GitHub</button>
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

function mount() {
  setupDom();
  const signIn = vi.fn();
  const cancelSignIn = vi.fn();
  const signOut = vi.fn();
  const openUrl = vi.fn();
  const controller = new SignInController({
    accountBtn: document.querySelector<HTMLButtonElement>("#github-btn"),
    menu: document.querySelector<HTMLElement>("#account-menu"),
    connectBtn: document.querySelector<HTMLButtonElement>("#account-connect"),
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
    openUrl,
  });
  return { controller, signIn, cancelSignIn, signOut, openUrl };
}

describe("SignInController — account button", () => {
  it("keeps the general account menu available when GitHub sign-in is unavailable", () => {
    const { controller } = mount();
    controller.applyAccount({ available: false, signedIn: false });
    expect(el("github-btn").hidden).toBe(false);
    expect(el("account-connect").hidden).toBe(true);
    expect(el("account-signout").hidden).toBe(true);
  });

  it("offers Connect when signed out, and signs in on click", () => {
    const { controller, signIn } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    el("github-btn").click();
    expect(el("account-menu").hidden).toBe(false);
    el("account-connect").click();
    expect(signIn).toHaveBeenCalledTimes(1);
  });

  it("shows the handle in the accessible account name, and signs out from the menu", () => {
    const { controller, signOut } = mount();
    controller.applyAccount({ available: true, signedIn: true, login: "octocat" });
    expect(el("github-btn").getAttribute("aria-label")).toBe("Account, signed in as @octocat");
    el("github-btn").click();
    expect(el("account-signout").textContent).toBe("Sign out @octocat");
    el("account-signout").click();
    expect(signOut).toHaveBeenCalledTimes(1);
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
    expect(document.activeElement).toBe(el("account-settings"));
    el("account-settings").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
    expect(el("account-menu").hidden).toBe(true);
    expect(document.activeElement).toBe(el("github-btn"));
  });

  it("supports Home and End across the visible menu items", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    el("github-btn").click();

    el("account-settings").dispatchEvent(
      new KeyboardEvent("keydown", { key: "End", bubbles: true }),
    );
    expect(document.activeElement).toBe(el("account-connect"));
    el("account-connect").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Home", bubbles: true }),
    );
    expect(document.activeElement).toBe(el("account-settings"));
  });
});

describe("SignInController — code bar", () => {
  it("reveals the code and opens GitHub", () => {
    const { controller, openUrl } = mount();
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });

    expect(el("github-signin-bar").hidden).toBe(false);
    expect(el("github-user-code").textContent).toBe("WXYZ-1234");
    expect(el("github-signin-status").textContent).toContain("Waiting");

    el("github-open-btn").click();
    expect(openUrl).toHaveBeenCalledWith("https://github.com/login/device");
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
