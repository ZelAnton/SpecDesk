// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { RegisteredRepo, WorkspaceStatePayload } from "../../src/wire/protocol.js";
import { RepositoriesPanel } from "../../src/workspace/tools/repositories-panel.js";

const REPO: RegisteredRepo = {
  id: "acme/specs",
  name: "acme/specs",
  url: "https://github.com/acme/specs",
};
const STATE: WorkspaceStatePayload = { recent: [], favorites: [], repositories: [REPO] };

function ready() {
  const onRegister = vi.fn<(url: string) => void>();
  const onUnregister = vi.fn<(id: string) => void>();
  const onOpenRepo = vi.fn<(repo: RegisteredRepo) => void>();
  const panel = new RepositoriesPanel({ onRegister, onUnregister, onOpenRepo });
  const body = document.createElement("div");
  document.body.appendChild(body);
  panel.mount(body);
  const input = body.querySelector<HTMLInputElement>(".repo-register-input");
  const add = body.querySelector<HTMLButtonElement>(".repo-register-add");
  if (!input || !add) {
    throw new Error("repositories panel did not mount its register form");
  }
  return { panel, body, input, add, onRegister, onUnregister, onOpenRepo };
}

describe("RepositoriesPanel", () => {
  it("shows the empty hint until repositories are set", () => {
    const { body } = ready();
    expect(body.querySelector<HTMLElement>(".repo-empty")?.hidden).toBe(false);
    expect(body.querySelector<HTMLElement>(".repo-empty")?.textContent).toBe(
      "Register a repository to keep it handy.",
    );
    expect(body.querySelector<HTMLElement>(".repo-list")?.hidden).toBe(true);
  });

  it("registers the typed repo on submit and clears the field", () => {
    const { input, add, onRegister } = ready();
    input.value = "  owner/name  ";
    add.click(); // a submit button inside the form fires its submit handler
    expect(onRegister).toHaveBeenCalledWith("owner/name"); // trimmed
    expect(input.value).toBe(""); // cleared for the next entry
  });

  it("ignores a blank register submit", () => {
    const { input, add, onRegister } = ready();
    input.value = "   ";
    add.click();
    expect(onRegister).not.toHaveBeenCalled();
  });

  it("renders repo rows: clicking opens the repo, the trailing × removes it", () => {
    const { panel, body, onUnregister, onOpenRepo } = ready();
    panel.setState(STATE);

    expect(body.querySelector<HTMLElement>(".repo-empty")?.hidden).toBe(true);
    const open = body.querySelector<HTMLButtonElement>(".repo-open");
    expect(open?.textContent).toBe("acme/specs");
    expect(open?.title).toBe(REPO.url);
    open?.click();
    expect(onOpenRepo).toHaveBeenCalledWith(REPO);

    const remove = body.querySelector<HTMLButtonElement>(".repo-remove");
    expect(remove?.getAttribute("aria-label")).toBe("Remove repository acme/specs");
    remove?.click();
    expect(onUnregister).toHaveBeenCalledWith("acme/specs");
  });
});
