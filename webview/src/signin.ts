import type { GitHubAccountPayload, GitHubCodePayload } from "./protocol.js";

/** The host actions the account affordance triggers (each maps to one IPC message). */
export interface SignInDeps {
  /** Start connecting a GitHub account. */
  signIn: () => void;
  /** Cancel an in-flight sign-in. */
  cancelSignIn: () => void;
  /** Disconnect the GitHub account. */
  signOut: () => void;
  /** Open the GitHub authorization page in the OS browser. */
  openUrl: (url: string) => void;
}

function setText(element: HTMLElement | null, text: string): void {
  if (element) {
    element.textContent = text;
  }
}

function setHidden(element: HTMLElement | null, hidden: boolean): void {
  if (element) {
    element.hidden = hidden;
  }
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
  private readonly accountBtn = document.querySelector<HTMLButtonElement>("#github-btn");
  private readonly bar = document.querySelector<HTMLElement>("#github-signin-bar");
  private readonly text = document.querySelector<HTMLElement>("#github-signin-text");
  private readonly userCode = document.querySelector<HTMLElement>("#github-user-code");
  private readonly openBtn = document.querySelector<HTMLButtonElement>("#github-open-btn");
  private readonly status = document.querySelector<HTMLElement>("#github-signin-status");
  private readonly cancelBtn = document.querySelector<HTMLButtonElement>("#github-cancel-btn");
  private signedIn = false;
  private verificationUri = "";

  constructor(private readonly deps: SignInDeps) {
    this.accountBtn?.addEventListener("click", () => {
      if (this.signedIn) {
        this.deps.signOut();
      } else {
        this.deps.signIn();
      }
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
    // Hide the account button while the bar is the active affordance (no restart race); the terminal
    // github.account event restores it.
    setHidden(this.accountBtn, true);
  }

  /** Update the account affordance from the host's connection state. */
  applyAccount(payload: GitHubAccountPayload): void {
    this.signedIn = payload.signedIn;
    setHidden(this.accountBtn, !payload.available);
    setText(
      this.accountBtn,
      payload.signedIn
        ? payload.login && payload.login.length > 0
          ? `Sign out ${atHandle(payload.login)}`
          : "Sign out"
        : "Connect to GitHub",
    );

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
}
