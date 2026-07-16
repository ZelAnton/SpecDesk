import { setHidden, setText } from "../util/dom.js";
import type { GitHubAccountPayload, GitHubCodePayload } from "../wire/protocol.js";

const AUTO_ACCOUNT_REFRESH_INTERVAL_MS = 5 * 60 * 1000;

/** The host actions the account affordance triggers (each maps to one IPC message), plus the
 *  affordance's own DOM elements (each may be absent from the markup). */
export interface SignInDeps {
  /** The global account-menu trigger. */
  accountBtn: HTMLButtonElement | null;
  avatar?: HTMLImageElement | null;
  avatarFallback?: HTMLElement | null;
  notificationCount?: HTMLElement | null;
  /** The direct GitHub Connect/Sign out action on the main toolbar. */
  authBtn: HTMLButtonElement | null;
  /** Read-only GitHub identity in the bottom status bar. */
  accountStatus: HTMLElement | null;
  menu: HTMLElement | null;
  connectBtn: HTMLButtonElement | null;
  refreshBtn: HTMLButtonElement | null;
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
  /** Re-read organizations and repositories available to the current authorization. */
  refreshAccount: () => void;
  /** Open the GitHub authorization page in the OS browser. */
  openUrl: (url: string) => void;
  /** Copy the one-time code before focus moves to the OS browser. */
  copyText: (text: string) => Promise<void>;
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
  private readonly authBtn: HTMLButtonElement | null;
  private readonly avatar: HTMLImageElement | null;
  private readonly avatarFallback: HTMLElement | null;
  private readonly notificationCount: HTMLElement | null;
  private readonly accountStatus: HTMLElement | null;
  private readonly menu: HTMLElement | null;
  private readonly connectBtn: HTMLButtonElement | null;
  private readonly refreshBtn: HTMLButtonElement | null;
  private readonly signOutBtn: HTMLButtonElement | null;
  private readonly bar: HTMLElement | null;
  private readonly text: HTMLElement | null;
  private readonly userCode: HTMLElement | null;
  private readonly openBtn: HTMLButtonElement | null;
  private readonly status: HTMLElement | null;
  private readonly cancelBtn: HTMLButtonElement | null;
  private accountLabel = "Account";
  private notifications = 0;
  private verificationUri = "";
  private codeGeneration = 0;
  private available = false;
  private signedIn = false;
  private refreshingAccount = false;
  private lastAccountDetailsAt = Number.NEGATIVE_INFINITY;

  constructor(private readonly deps: SignInDeps) {
    this.accountBtn = deps.accountBtn;
    this.authBtn = deps.authBtn;
    this.avatar = deps.avatar ?? null;
    this.avatarFallback = deps.avatarFallback ?? null;
    this.notificationCount = deps.notificationCount ?? null;
    this.accountStatus = deps.accountStatus;
    this.menu = deps.menu;
    this.connectBtn = deps.connectBtn;
    this.refreshBtn = deps.refreshBtn;
    this.signOutBtn = deps.signOutBtn;
    this.bar = deps.bar;
    this.text = deps.text;
    this.userCode = deps.userCode;
    this.openBtn = deps.openBtn;
    this.status = deps.status;
    this.cancelBtn = deps.cancelBtn;
    this.avatar?.addEventListener("error", () => this.showAvatarFallback());

    this.accountBtn?.addEventListener("click", () => {
      this.setMenuOpen(this.menu === null || this.menu.hidden === true, true);
    });
    this.accountBtn?.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        this.setMenuOpen(true, true);
      }
    });
    this.authBtn?.addEventListener("click", () => {
      if (!this.available) {
        return;
      }
      if (this.signedIn) {
        this.deps.signOut();
      } else {
        this.deps.signIn();
      }
    });
    this.connectBtn?.addEventListener("click", () => {
      this.setMenuOpen(false);
      this.deps.signIn();
    });
    this.refreshBtn?.addEventListener("click", () => {
      if (!this.signedIn || this.refreshingAccount) return;
      this.startAccountRefresh();
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
      this.codeGeneration++;
      this.deps.cancelSignIn();
      setHidden(this.bar, true);
    });
  }

  /** Show the one-time code for the author to enter at GitHub. */
  showCode(payload: GitHubCodePayload): void {
    const generation = ++this.codeGeneration;
    this.verificationUri = payload.verificationUri;
    setText(this.text, "To connect, open GitHub and enter this code:");
    setText(this.userCode, payload.userCode);
    setHidden(this.userCode, false);
    setHidden(this.openBtn, false);
    setText(this.status, "Waiting for you to authorize on GitHub…");
    setText(this.cancelBtn, "Cancel");
    setHidden(this.bar, false);
    this.setMenuOpen(false);

    // GitHub's documented device flow still requires the one-time code. Start copying it while the
    // WebView has focus, before opening the OS browser; failure is harmless because the code remains visible.
    void this.deps
      .copyText(payload.userCode)
      .then(() => {
        if (generation === this.codeGeneration) {
          setText(this.text, "Code copied. Paste it into GitHub:");
        }
      })
      .catch(() => {
        // Clipboard access can be denied by WebView/OS policy. The visible code is the accessible fallback.
      });

    // Device-flow authorization is intentionally completed in the user's normal browser. Opening the
    // GitHub page as soon as the host issues the code makes repository actions a single continuous flow;
    // the visible button remains as a retry if the OS declines the first launch.
    this.deps.openUrl(payload.verificationUri);
  }

  /** Update the account affordance from the host's connection state. */
  applyAccount(payload: GitHubAccountPayload): void {
    this.available = payload.available;
    this.signedIn = payload.available && payload.signedIn;
    setHidden(this.accountBtn, false);
    setHidden(this.authBtn, !payload.available || payload.signedIn);
    setHidden(this.accountStatus, !payload.available || !payload.signedIn);
    const handle = payload.login && payload.login.length > 0 ? atHandle(payload.login) : "";
    const authLabel = "Sign in";
    setText(this.authBtn, authLabel);
    this.authBtn?.setAttribute("aria-label", authLabel);
    if (this.authBtn !== null) {
      this.authBtn.title = authLabel;
    }
    this.accountLabel = payload.signedIn && handle ? `Account, signed in as ${handle}` : "Account";
    this.updateAccountLabel();
    this.applyAvatar(payload, handle);
    setHidden(this.connectBtn, !payload.available || payload.signedIn);
    if (!this.signedIn) {
      this.refreshingAccount = false;
      this.lastAccountDetailsAt = Number.NEGATIVE_INFINITY;
    } else if (payload.organizations === undefined) {
      this.refreshingAccount = true;
    } else {
      this.refreshingAccount = false;
      this.lastAccountDetailsAt = Date.now();
    }
    this.updateRefreshButton();
    setHidden(this.signOutBtn, !payload.available || !payload.signedIn);
    setText(this.signOutBtn, handle ? `Sign out ${handle}` : "Sign out");

    if (payload.available && payload.signedIn) {
      const identity = handle ? `GitHub: ${handle}` : "GitHub connected";
      const organizations = payload.organizations;
      const access =
        payload.message && payload.message.length > 0
          ? payload.message
          : organizations === undefined
            ? "Organizations: loading…"
            : organizations.length > 0
              ? `Organizations: ${organizations.join(", ")}`
              : "No authorized organizations";
      setText(this.accountStatus, `${identity} · ${access}`);
    }

    if (payload.signedIn) {
      this.codeGeneration++;
      setHidden(this.bar, true);
      return;
    }
    this.codeGeneration++;
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

  /** Refresh after returning to a stale signed-in window without issuing focus-request storms. */
  refreshIfStale(now = Date.now()): boolean {
    if (
      !this.signedIn ||
      this.refreshingAccount ||
      now - this.lastAccountDetailsAt < AUTO_ACCOUNT_REFRESH_INTERVAL_MS
    ) {
      return false;
    }
    this.startAccountRefresh();
    return true;
  }

  /** Notification transport lands separately; zero keeps the badge out of the way. */
  setNotificationCount(count: number): void {
    const normalized = Math.max(0, Math.trunc(count));
    this.notifications = normalized;
    setText(this.notificationCount, normalized > 99 ? "99+" : String(normalized));
    setHidden(this.notificationCount, normalized === 0);
    this.updateAccountLabel();
  }

  private updateAccountLabel(): void {
    const suffix =
      this.notifications > 0
        ? `, ${this.notifications} ${this.notifications === 1 ? "notification" : "notifications"}`
        : "";
    this.accountBtn?.setAttribute("aria-label", `${this.accountLabel}${suffix}`);
  }

  private applyAvatar(payload: GitHubAccountPayload, handle: string): void {
    if (!payload.signedIn || payload.avatarUrl === undefined || payload.avatarUrl.length === 0) {
      this.showAvatarFallback();
      return;
    }
    if (this.avatar !== null) {
      this.avatar.alt = handle ? `GitHub avatar for ${handle}` : "GitHub account avatar";
      this.avatar.src = payload.avatarUrl;
      setHidden(this.avatar, false);
    }
    setHidden(this.avatarFallback, true);
  }

  private showAvatarFallback(): void {
    if (this.avatar !== null) {
      this.avatar.removeAttribute("src");
      this.avatar.alt = "";
      setHidden(this.avatar, true);
    }
    setHidden(this.avatarFallback, false);
  }

  private startAccountRefresh(): void {
    this.refreshingAccount = true;
    this.updateRefreshButton();
    this.setMenuOpen(false);
    this.deps.refreshAccount();
  }

  private updateRefreshButton(): void {
    setHidden(this.refreshBtn, !this.available || !this.signedIn);
    if (this.refreshBtn === null) return;
    this.refreshBtn.disabled = this.refreshingAccount;
    this.refreshBtn.setAttribute("aria-busy", String(this.refreshingAccount));
    setText(
      this.refreshBtn,
      this.refreshingAccount ? "Refreshing GitHub access…" : "Refresh GitHub access",
    );
    this.refreshBtn.title = "Check for newly approved organizations and repositories";
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
