import { setHidden, setText } from "../util/dom.js";
import type { GitHubAccountPayload, GitHubCodePayload } from "../wire/protocol.js";

/** The host actions the account affordance triggers (each maps to one IPC message), plus the
 *  affordance's own DOM elements (each may be absent from the markup). */
export interface SignInDeps {
  /** The global account-menu trigger. */
  accountBtn: HTMLButtonElement | null;
  menu: HTMLElement | null;
  connectBtn: HTMLButtonElement | null;
  signOutBtn: HTMLButtonElement | null;
  /** The sign-in code bar and its contents. */
  bar: HTMLElement | null;
  text: HTMLElement | null;
  userCode: HTMLElement | null;
  openBtn: HTMLButtonElement | null;
  status: HTMLElement | null;
  cancelBtn: HTMLButtonElement | null;
  /** Start connecting a GitHub account. */
  signIn: () => void;
  /** Cancel an in-flight sign-in. */
  cancelSignIn: () => void;
  /** Disconnect the GitHub account. */
  signOut: () => void;
  /** Open the GitHub authorization page in the OS browser. */
  openUrl: (url: string) => void;
}

/** Prefix a bare GitHub handle with `@` for display (idempotent). */
function atHandle(login: string): string {
  return login.startsWith("@") ? login : `@${login}`;
}

/**
 * The "Connect to GitHub" account affordance plus the sign-in code bar. The host drives it with two
 * events: `github.code` ({@link showCode} — display the one-time code) and `github.account`
 * ({@link applyAccount} — the connection state). Plain language only; the author never sees
 * OAuth/token/device-flow vocabulary.
 */
export class SignInController {
  private readonly accountBtn: HTMLButtonElement | null;
  private readonly menu: HTMLElement | null;
  private readonly connectBtn: HTMLButtonElement | null;
  private readonly signOutBtn: HTMLButtonElement | null;
  private readonly bar: HTMLElement | null;
  private readonly text: HTMLElement | null;
  private readonly userCode: HTMLElement | null;
  private readonly openBtn: HTMLButtonElement | null;
  private readonly status: HTMLElement | null;
  private readonly cancelBtn: HTMLButtonElement | null;
  private verificationUri = "";

  constructor(private readonly deps: SignInDeps) {
    this.accountBtn = deps.accountBtn;
    this.menu = deps.menu;
    this.connectBtn = deps.connectBtn;
    this.signOutBtn = deps.signOutBtn;
    this.bar = deps.bar;
    this.text = deps.text;
    this.userCode = deps.userCode;
    this.openBtn = deps.openBtn;
    this.status = deps.status;
    this.cancelBtn = deps.cancelBtn;

    this.accountBtn?.addEventListener("click", () => {
      this.setMenuOpen(this.menu === null || this.menu.hidden === true, true);
    });
    this.accountBtn?.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        this.setMenuOpen(true, true);
      }
    });
    this.connectBtn?.addEventListener("click", () => {
      this.setMenuOpen(false);
      this.deps.signIn();
    });
    this.signOutBtn?.addEventListener("click", () => {
      this.setMenuOpen(false);
      this.deps.signOut();
    });
    this.menu?.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.setMenuOpen(false);
        this.accountBtn?.focus();
        return;
      }
      if (event.key === "Tab") {
        this.setMenuOpen(false);
        return;
      }
      if (
        event.key !== "ArrowDown" &&
        event.key !== "ArrowUp" &&
        event.key !== "Home" &&
        event.key !== "End"
      ) {
        return;
      }
      const items = this.menuItems();
      if (items.length === 0) {
        return;
      }
      event.preventDefault();
      const current = items.indexOf(document.activeElement as HTMLButtonElement);
      const delta = event.key === "ArrowDown" ? 1 : -1;
      const next =
        event.key === "Home"
          ? 0
          : event.key === "End"
            ? items.length - 1
            : current < 0
              ? 0
              : (current + delta + items.length) % items.length;
      items[next]?.focus();
    });
    this.menu?.addEventListener("click", (event) => {
      if (
        event.target instanceof Element &&
        event.target.closest('[role="menuitem"], [role="menuitemcheckbox"]')
      ) {
        this.setMenuOpen(false);
      }
    });
    document.addEventListener("click", (event) => {
      const target = event.target;
      if (
        !(target instanceof Node) ||
        this.accountBtn?.contains(target) ||
        this.menu?.contains(target)
      ) {
        return;
      }
      this.setMenuOpen(false);
    });
    this.openBtn?.addEventListener("click", () => {
      if (this.verificationUri.length > 0) {
        this.deps.openUrl(this.verificationUri);
      }
    });
    this.cancelBtn?.addEventListener("click", () => {
      // Harmless if the flow already ended (the host's cancel is a no-op then); always closes the bar.
      this.deps.cancelSignIn();
      setHidden(this.bar, true);
    });
  }

  /** Show the one-time code for the author to enter at GitHub. */
  showCode(payload: GitHubCodePayload): void {
    this.verificationUri = payload.verificationUri;
    setText(this.text, "To connect, open GitHub and enter this code:");
    setText(this.userCode, payload.userCode);
    setHidden(this.userCode, false);
    setHidden(this.openBtn, false);
    setText(this.status, "Waiting for you to authorize on GitHub…");
    setText(this.cancelBtn, "Cancel");
    setHidden(this.bar, false);
    this.setMenuOpen(false);

    // Device-flow authorization is intentionally completed in the user's normal browser. Opening the
    // GitHub page as soon as the host issues the code makes repository actions a single continuous flow;
    // the visible button remains as a retry if the OS declines the first launch.
    this.deps.openUrl(payload.verificationUri);
  }

  /** Update the account affordance from the host's connection state. */
  applyAccount(payload: GitHubAccountPayload): void {
    setHidden(this.accountBtn, false);
    const handle = payload.login && payload.login.length > 0 ? atHandle(payload.login) : "";
    this.accountBtn?.setAttribute(
      "aria-label",
      payload.signedIn && handle ? `Account, signed in as ${handle}` : "Account",
    );
    setHidden(this.connectBtn, !payload.available || payload.signedIn);
    setHidden(this.signOutBtn, !payload.available || !payload.signedIn);
    setText(this.signOutBtn, handle ? `Sign out ${handle}` : "Sign out");

    if (payload.signedIn) {
      setHidden(this.bar, true);
      return;
    }
    // Signed out. A message means the flow ended without success (couldn't start / expired / declined /
    // unreachable) — surface it in the bar, revealing the bar even if the code was never shown (an up-front
    // failure). A plain signed-out state (no message) just closes the bar.
    if (payload.message !== undefined && payload.message.length > 0) {
      setHidden(this.bar, false);
      setText(this.text, payload.message);
      setHidden(this.userCode, true);
      setHidden(this.openBtn, true);
      setText(this.status, "");
      setText(this.cancelBtn, "Close");
    } else {
      setHidden(this.bar, true);
    }
  }

  private setMenuOpen(open: boolean, focusFirst = false): void {
    setHidden(this.menu, !open);
    this.accountBtn?.setAttribute("aria-expanded", String(open));
    if (open && focusFirst) {
      this.menuItems()[0]?.focus();
    }
  }

  private menuItems(): HTMLButtonElement[] {
    if (!this.menu) {
      return [];
    }
    return Array.from(
      this.menu.querySelectorAll<HTMLButtonElement>('[role="menuitem"], [role="menuitemcheckbox"]'),
    ).filter((item) => !item.hidden && !item.disabled);
  }
}
