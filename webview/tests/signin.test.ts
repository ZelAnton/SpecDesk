// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { SignInController } from "../src/signin.js";

// Markup mirroring the GitHub account affordance + sign-in code bar in index.html (both start hidden).
function setupDom(): void {
  document.body.innerHTML = `
    <button id="github-btn" hidden>Connect to GitHub</button>
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
  const controller = new SignInController({ signIn, cancelSignIn, signOut, openUrl });
  return { controller, signIn, cancelSignIn, signOut, openUrl };
}

describe("SignInController — account button", () => {
  it("hides the affordance when sign-in is unavailable", () => {
    const { controller } = mount();
    controller.applyAccount({ available: false, signedIn: false });
    expect(el("github-btn").hidden).toBe(true);
  });

  it("offers Connect when signed out, and signs in on click", () => {
    const { controller, signIn } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    expect(el("github-btn").hidden).toBe(false);
    expect(el("github-btn").textContent).toBe("Connect to GitHub");
    el("github-btn").click();
    expect(signIn).toHaveBeenCalledTimes(1);
  });

  it("shows the handle when signed in, and signs out on click", () => {
    const { controller, signOut } = mount();
    controller.applyAccount({ available: true, signedIn: true, login: "octocat" });
    expect(el("github-btn").textContent).toBe("Sign out @octocat");
    el("github-btn").click();
    expect(signOut).toHaveBeenCalledTimes(1);
  });

  it("falls back to a plain Sign out when the handle is unknown", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: true, login: "" });
    expect(el("github-btn").textContent).toBe("Sign out");
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

  it("hides the account button while connecting, then restores it", () => {
    const { controller } = mount();
    controller.applyAccount({ available: true, signedIn: false });
    expect(el("github-btn").hidden).toBe(false);
    controller.showCode({
      userCode: "WXYZ-1234",
      verificationUri: "https://github.com/login/device",
    });
    expect(el("github-btn").hidden).toBe(true); // no restart race while the bar is up
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
    expect(el("github-btn").textContent).toBe("Sign out @octocat");
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
});
