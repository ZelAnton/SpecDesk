// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import {
  CENTRAL_VIEW_ACTIVE_CLASS,
  CENTRAL_VIEW_CLASS,
  CentralFrame,
} from "../../src/workspace/central-frame.js";

/** A host frame seeded with `data-view="editor"` plus three sibling view elements (the editor panes and
 *  two alternates), mirroring the index.html shape the frame manages. */
function harness(initialView = "editor") {
  document.body.innerHTML = `
    <main id="central-frame" data-view="${initialView}">
      <div id="panes" class="${CENTRAL_VIEW_CLASS} ${CENTRAL_VIEW_ACTIVE_CLASS}"></div>
      <div id="home"></div>
      <div id="changes"></div>
    </main>`;
  const byId = (id: string) => {
    const el = document.querySelector<HTMLElement>(`#${id}`);
    if (el === null) {
      throw new Error(`no #${id}`);
    }
    return el;
  };
  const host = byId("central-frame");
  return { host, byId, frame: new CentralFrame(host) };
}

const isActive = (el: HTMLElement) => el.classList.contains(CENTRAL_VIEW_ACTIVE_CLASS);

describe("CentralFrame.register", () => {
  it("keeps the markup's active view active and hides every other registered view", () => {
    const { byId, frame } = harness();
    frame.register({ id: "editor", el: byId("panes") });
    frame.register({ id: "home", el: byId("home") });
    frame.register({ id: "changes", el: byId("changes") });

    expect(isActive(byId("panes"))).toBe(true);
    expect(isActive(byId("home"))).toBe(false);
    expect(isActive(byId("changes"))).toBe(false);
    // Every view gains the base class so the stylesheet's hide rule applies to it.
    for (const id of ["panes", "home", "changes"]) {
      expect(byId(id).classList.contains(CENTRAL_VIEW_CLASS)).toBe(true);
    }
    expect(frame.active()).toBe("editor");
    expect(frame.has("home")).toBe(true);
    expect(frame.has("missing")).toBe(false);
  });

  it("replacing a registered id keeps the latest element and strips the superseded one", () => {
    const { byId, frame } = harness();
    frame.register({ id: "editor", el: byId("panes") });
    frame.register({ id: "alt", el: byId("home") });
    // Re-register the same id with a different element; the latest record wins and the old element is
    // stripped so it can never resurface as a stray view.
    frame.register({ id: "alt", el: byId("changes") });
    expect(byId("home").classList.contains(CENTRAL_VIEW_CLASS)).toBe(false);

    frame.show("alt");
    expect(isActive(byId("changes"))).toBe(true);
    expect(isActive(byId("home"))).toBe(false);
    expect(isActive(byId("panes"))).toBe(false);
  });

  it("re-registering the ACTIVE id with a new element leaves only the new element active", () => {
    const { byId, frame } = harness();
    frame.register({ id: "editor", el: byId("panes") });
    // Rebuild the active view's element and re-register it under the same id: exactly one element must end
    // up active — the new one — never both (the superseded element is fully stripped).
    frame.register({ id: "editor", el: byId("home") });

    expect(isActive(byId("home"))).toBe(true);
    expect(isActive(byId("panes"))).toBe(false);
    expect(byId("panes").classList.contains(CENTRAL_VIEW_CLASS)).toBe(false);
    expect(frame.active()).toBe("editor");
  });

  it("adopts the starting view from the host's data-view, not a hardcoded default", () => {
    const { byId, frame } = harness("home");
    frame.register({ id: "editor", el: byId("panes") });
    frame.register({ id: "home", el: byId("home") });

    expect(frame.active()).toBe("home");
    expect(isActive(byId("home"))).toBe(true);
    // The panes carried the active flag in the markup, but they are not the declared starting view, so
    // register clears it — no two views end up active at once.
    expect(isActive(byId("panes"))).toBe(false);
  });
});

describe("CentralFrame.show", () => {
  function ready() {
    const h = harness();
    const onEditorShow = vi.fn();
    const onEditorHide = vi.fn();
    const onHomeShow = vi.fn();
    const onHomeHide = vi.fn();
    h.frame.register({
      id: "editor",
      el: h.byId("panes"),
      onShow: onEditorShow,
      onHide: onEditorHide,
    });
    h.frame.register({ id: "home", el: h.byId("home"), onShow: onHomeShow, onHide: onHomeHide });
    return { ...h, onEditorShow, onEditorHide, onHomeShow, onHomeHide };
  }

  it("swaps the active view: moves the flag, updates data-view, runs onHide then onShow", () => {
    const { host, byId, frame, onEditorHide, onHomeShow } = ready();
    const order: string[] = [];
    onEditorHide.mockImplementation(() => order.push("editor.onHide"));
    onHomeShow.mockImplementation(() => order.push("home.onShow"));

    frame.show("home");

    expect(isActive(byId("home"))).toBe(true);
    expect(isActive(byId("panes"))).toBe(false);
    expect(host.dataset.view).toBe("home");
    expect(frame.active()).toBe("home");
    // The previous view is hidden before the next is revealed and shown, so onShow measures a visible view.
    expect(order).toEqual(["editor.onHide", "home.onShow"]);
  });

  it("is a no-op when the requested view is already active (no re-fired hooks)", () => {
    const { byId, frame, onEditorShow, onEditorHide } = ready();
    frame.show("editor");

    expect(onEditorShow).not.toHaveBeenCalled();
    expect(onEditorHide).not.toHaveBeenCalled();
    expect(isActive(byId("panes"))).toBe(true);
    expect(frame.active()).toBe("editor");
  });

  it("ignores an unregistered id, leaving the active view untouched", () => {
    const { host, byId, frame, onEditorHide } = ready();
    frame.show("does-not-exist");

    expect(onEditorHide).not.toHaveBeenCalled();
    expect(isActive(byId("panes"))).toBe(true);
    expect(host.dataset.view).toBe("editor");
    expect(frame.active()).toBe("editor");
  });

  it("switches back to a previously shown view", () => {
    const { byId, frame } = ready();
    frame.show("home");
    frame.show("editor");

    expect(isActive(byId("panes"))).toBe(true);
    expect(isActive(byId("home"))).toBe(false);
    expect(frame.active()).toBe("editor");
  });
});
