// @vitest-environment jsdom
import { EditorState } from "prosemirror-state";
import { describe, expect, it } from "vitest";
import indexHtml from "../../index.html?raw";
import { FORMAT_REGISTRY, formatDef, isFormatCommand } from "../../src/editors/format-registry.js";
import { formatMarkdown } from "../../src/editors/md-format.js";
import { activeFormats, commandFor } from "../../src/editors/pm-commands.js";
import { parser, schema } from "../../src/editors/pm-markdown.js";

const IDS = FORMAT_REGISTRY.map((command) => command.id);

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
});
