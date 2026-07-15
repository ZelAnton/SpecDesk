// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { EditorState } from "prosemirror-state";
import { describe, expect, it } from "vitest";
import indexHtml from "../../index.html?raw";
import { FORMAT_REGISTRY, formatDef, isFormatCommand } from "../../src/editors/format-registry.js";
import { formatMarkdown } from "../../src/editors/md-format.js";
import { activeFormats, commandFor } from "../../src/editors/pm-commands.js";
import { parser, schema } from "../../src/editors/pm-markdown.js";

const IDS = FORMAT_REGISTRY.map((command) => command.id);
const styles = readFileSync(join("styles.css"), "utf8");

describe("format registry is the single source of truth", () => {
  it("derives isFormatCommand (and the FormatCommand union) from the registry entries", () => {
    for (const id of IDS) {
      expect(isFormatCommand(id)).toBe(true);
    }
    expect(isFormatCommand("underline")).toBe(false);
    expect(isFormatCommand("")).toBe(false);
    expect(isFormatCommand(undefined)).toBe(false);
  });

  it("gives every command a unique id and a non-empty label", () => {
    expect(new Set(IDS).size).toBe(IDS.length);
    for (const command of FORMAT_REGISTRY) {
      expect(command.label.length).toBeGreaterThan(0);
    }
  });

  it("declares the standard formatting shortcuts in toolbar order", () => {
    expect(FORMAT_REGISTRY.map(({ id, hotkey, label }) => ({ id, hotkey, label }))).toEqual([
      { id: "bold", hotkey: "Mod-b", label: "Bold (Ctrl+B)" },
      { id: "italic", hotkey: "Mod-i", label: "Italic (Ctrl+I)" },
      { id: "strike", hotkey: "Mod-Shift-x", label: "Strikethrough (Ctrl+Shift+X)" },
      { id: "inlineCode", hotkey: "Mod-`", label: "Inline code (Ctrl+`)" },
      { id: "h1", hotkey: "Mod-Alt-1", label: "Heading 1 (Ctrl+Alt+1)" },
      { id: "h2", hotkey: "Mod-Alt-2", label: "Heading 2 (Ctrl+Alt+2)" },
      { id: "h3", hotkey: "Mod-Alt-3", label: "Heading 3 (Ctrl+Alt+3)" },
      { id: "bullet", hotkey: "Mod-Shift-8", label: "Bullet list (Ctrl+Shift+8)" },
      { id: "ordered", hotkey: "Mod-Shift-7", label: "Numbered list (Ctrl+Shift+7)" },
      { id: "quote", hotkey: "Mod-Shift-.", label: "Quote (Ctrl+Shift+>)" },
      { id: "code", hotkey: "Mod-Shift-e", label: "Code block (Ctrl+Shift+E)" },
      { id: "link", hotkey: "Mod-k", label: "Insert link (Ctrl+K)" },
      { id: "table", hotkey: "Mod-Alt-t", label: "Insert table (Ctrl+Alt+T)" },
      {
        id: "image",
        hotkey: "Mod-Shift-i",
        label: "Insert image reference (Ctrl+Shift+I)",
      },
      { id: "rule", hotkey: "Mod-Shift-r", label: "Insert divider (Ctrl+Shift+R)" },
    ]);
  });
});

// The whole point of the registry: a declared command is handled by BOTH tracts, with no default-branch
// silent no-op. If a future command's `kind.type` is left unhandled by a tract, that tract's
// `assertNever(kind)` default throws — so iterating the whole registry here fails loudly (the runtime
// counterpart of the compile-time exhaustiveness the `switch`es already enforce). A command declared in
// the registry therefore cannot exist without a handler in each tract.
describe("every registered command is handled by both editor tracts", () => {
  const state = EditorState.create({ doc: parser.parse("hello\n"), schema });

  for (const { id } of FORMAT_REGISTRY) {
    it(`the Code tract (formatMarkdown) handles ${id}`, () => {
      const edit = formatMarkdown("hello", 0, 5, id);
      expect(edit.from).toBeLessThanOrEqual(edit.to);
      expect(typeof edit.insert).toBe("string");
    });

    it(`the Formatted tract (commandFor) handles ${id}`, () => {
      expect(typeof commandFor(id)).toBe("function");
    });
  }

  it("activeFormats only ever reports commands that exist in the registry", () => {
    for (const command of activeFormats(state)) {
      expect(isFormatCommand(command)).toBe(true);
    }
  });
});

// Closes both sides of the drift the registry exists to prevent: a button whose data-format has no
// handler, and a registered command with no button. Reads the actual shipped markup (index.html?raw) so
// it validates what really ships, not a copy.
describe("the toolbar buttons stay in lockstep with the registry", () => {
  const html = new DOMParser().parseFromString(indexHtml, "text/html");
  const buttons = Array.from(
    html.querySelectorAll<HTMLButtonElement>("#format-bar button[data-format]"),
  );

  it("declares exactly the registry's commands, in the same order", () => {
    expect(buttons.map((button) => button.dataset.format)).toEqual(IDS);
  });

  it("has no button whose data-format is not a registered command", () => {
    for (const button of buttons) {
      expect(isFormatCommand(button.dataset.format)).toBe(true);
    }
  });

  it("labels each button's title and aria-label from its registry entry", () => {
    for (const button of buttons) {
      const command = button.dataset.format;
      if (!isFormatCommand(command)) {
        throw new Error(`button data-format '${command}' is not a registered command`);
      }
      const { label } = formatDef(command);
      expect(button.getAttribute("title")).toBe(label);
      expect(button.getAttribute("aria-label")).toBe(label);
    }
  });

  it("uses ordinary buttons for insertion-only image and divider actions", () => {
    for (const command of ["table", "image", "rule"]) {
      expect(
        html
          .querySelector<HTMLButtonElement>(`#format-bar button[data-format="${command}"]`)
          ?.hasAttribute("aria-pressed"),
      ).toBe(false);
    }
  });
});

describe("the shipped workspace chrome", () => {
  const html = new DOMParser().parseFromString(indexHtml, "text/html");
  const primary = html.querySelector("#toolbar");
  const editor = html.querySelector("#editor-toolbar");

  it("keeps context above the central view and Markdown actions in the editor toolbar", () => {
    expect(primary).not.toBeNull();
    expect(editor).not.toBeNull();

    const contextPanels = html.querySelector("#central-frame > #context-panels");
    expect(contextPanels).not.toBeNull();
    for (const id of [
      "current-repository",
      "current-branch",
      "current-local-path",
      "current-path",
    ]) {
      expect(contextPanels?.querySelector(`#${id}`), id).not.toBeNull();
      expect(primary?.querySelector(`#${id}`), id).toBeNull();
      expect(editor?.querySelector(`#${id}`), id).toBeNull();
    }

    for (const id of ["toolbar-search", "account-notifications", "github-btn"]) {
      expect(primary?.querySelector(`#${id}`), id).not.toBeNull();
      expect(editor?.querySelector(`#${id}`), id).toBeNull();
    }

    for (const id of [
      "edit-btn",
      "save-version-btn",
      "compare-btn",
      "discard-btn",
      "wrap-btn",
      "mode-code",
      "mode-split",
      "mode-formatted",
      "format-bar",
    ]) {
      expect(editor?.querySelector(`#${id}`), id).not.toBeNull();
      expect(primary?.querySelector(`#${id}`), id).toBeNull();
    }
  });

  it("defines distinct neutral roles for rails, panels, and panel headers in every theme", () => {
    const rails = Array.from(styles.matchAll(/--mode-rail:\s*([^;]+);/g), (match) =>
      (match[1] ?? "").trim(),
    );
    const panels = Array.from(styles.matchAll(/--panel:\s*([^;]+);/g), (match) =>
      (match[1] ?? "").trim(),
    );
    const headers = Array.from(styles.matchAll(/--panel-header:\s*([^;]+);/g), (match) =>
      (match[1] ?? "").trim(),
    );

    expect(rails).toHaveLength(3);
    expect(panels).toHaveLength(3);
    expect(headers).toHaveLength(3);
    for (let index = 0; index < rails.length; index += 1) {
      expect(new Set([rails[index], panels[index], headers[index]])).toHaveLength(3);
    }

    expect(styles).toMatch(/\.dock-rail\s*\{[^}]*background:\s*var\(--mode-rail\)/s);
    expect(styles).toMatch(/\.dock\s*\{[^}]*background:\s*var\(--panel\)/s);
    expect(styles).toMatch(/\.dock-header\s*\{[^}]*background:\s*var\(--panel-header\)/s);
    expect(styles).toMatch(/#editor-toolbar\s*\{[^}]*background:\s*var\(--panel-header\)/s);
  });
});
