/**
 * The single registry of formatting-toolbar commands — the ONE place a command is declared, from which
 * everything downstream is derived so the six hand-synced spots collapse to one:
 *  - the {@link FormatCommand} union (below, off the entries' `id`);
 *  - the Code/source-tract Markdown text transforms (md-format.ts's `formatMarkdown`);
 *  - the Formatted/WYSIWYG-tract ProseMirror commands and active-state detection (pm-commands.ts's
 *    `commandFor` / `activeFormats`);
 *  - the {@link isFormatCommand} DOM-boundary guard;
 *  - the toolbar buttons in index.html — kept in lockstep by format-registry.test.ts, which fails if a
 *    button's `data-format`/`title`/`aria-label` drifts from an entry (or a button/entry has no partner).
 *
 * Each entry carries its command's SEMANTICS declaratively in `kind` — a discriminated union both tracts
 * interpret — rather than a pair of opaque `mdTransform`/`pmCommand`/`pmActive` closures. That keeps this
 * module free of both CodeMirror and ProseMirror (a cheap import for the DOM boundary and tests) and, more
 * importantly, makes the derivation exhaustive: each tract switches on `kind.type` guarded by
 * `assertNever`, so introducing a command whose `kind` a tract doesn't handle fails `tsc` instead of
 * silently no-op'ing. The task's `{mdTransform, pmCommand, pmActive}` are exactly those per-tract
 * interpreters (`formatMarkdown` / `commandFor` / `activeFormats`), keyed off `kind`.
 *
 * Adding a command is one entry here (plus its button in index.html, which the sync test then enforces).
 * Adding a whole new KIND of command (a new `kind.type`) additionally forces every tract's exhaustive
 * switch to grow a branch — the compiler points at each site.
 */

/**
 * A command's formatting semantics, shared by both editor surfaces:
 *  - `inline`  — an inline mark: a Markdown `marker` (source tract), the lang-markdown syntax `node` its
 *                valid CommonMark form parses to (used to detect an existing wrapper to toggle off), and
 *                the ProseMirror `mark` name (formatted tract);
 *  - `heading` — an ATX heading of the given `level` (`#`×level in source, a `heading` node in PM);
 *  - `list`    — a bullet or ordered list (`ordered`);
 *  - `quote`   — a blockquote;
 *  - `fence`   — a fenced code block;
 *  - `link` / `image` — insertion-friendly inline Markdown nodes with editable placeholder targets;
 *  - `table`   — a starter two-column table;
 *  - `rule`    — a thematic break.
 */
export type FormatKind =
  | {
      readonly type: "inline";
      readonly marker: string;
      readonly node: string;
      readonly mark: string;
    }
  | { readonly type: "heading"; readonly level: 1 | 2 | 3 }
  | { readonly type: "list"; readonly ordered: boolean }
  | { readonly type: "quote" }
  | { readonly type: "fence" }
  | { readonly type: "link" }
  | { readonly type: "image" }
  | { readonly type: "table" }
  | { readonly type: "rule" };

/** One formatting command's declaration. `label` is the button's `title` + `aria-label` (the two are
 *  identical); `hotkey` is the CodeMirror/ProseMirror keymap spelling for the keyboard shortcut. */
export interface FormatCommandDef {
  readonly id: string;
  readonly label: string;
  readonly hotkey: string;
  readonly kind: FormatKind;
}

/**
 * The formatting commands, in toolbar order. This array is the source of truth; the `FormatCommand`
 * union, both tracts' handling, and the buttons' validation all read off it. Keep the order aligned with
 * the `#format-bar` buttons in index.html — format-registry.test.ts asserts both the membership and the
 * order match.
 */
export const FORMAT_REGISTRY = [
  {
    id: "bold",
    label: "Bold (Ctrl+B)",
    hotkey: "Mod-b",
    kind: { type: "inline", marker: "**", node: "StrongEmphasis", mark: "strong" },
  },
  {
    id: "italic",
    label: "Italic (Ctrl+I)",
    hotkey: "Mod-i",
    kind: { type: "inline", marker: "*", node: "Emphasis", mark: "em" },
  },
  {
    id: "strike",
    label: "Strikethrough (Ctrl+Shift+X)",
    hotkey: "Mod-Shift-x",
    kind: { type: "inline", marker: "~~", node: "Strikethrough", mark: "strikethrough" },
  },
  {
    id: "inlineCode",
    label: "Inline code (Ctrl+`)",
    hotkey: "Mod-`",
    kind: { type: "inline", marker: "`", node: "InlineCode", mark: "code" },
  },
  {
    id: "h1",
    label: "Heading 1 (Ctrl+Alt+1)",
    hotkey: "Mod-Alt-1",
    kind: { type: "heading", level: 1 },
  },
  {
    id: "h2",
    label: "Heading 2 (Ctrl+Alt+2)",
    hotkey: "Mod-Alt-2",
    kind: { type: "heading", level: 2 },
  },
  {
    id: "h3",
    label: "Heading 3 (Ctrl+Alt+3)",
    hotkey: "Mod-Alt-3",
    kind: { type: "heading", level: 3 },
  },
  {
    id: "bullet",
    label: "Bullet list (Ctrl+Shift+8)",
    hotkey: "Mod-Shift-8",
    kind: { type: "list", ordered: false },
  },
  {
    id: "ordered",
    label: "Numbered list (Ctrl+Shift+7)",
    hotkey: "Mod-Shift-7",
    kind: { type: "list", ordered: true },
  },
  { id: "quote", label: "Quote (Ctrl+Shift+>)", hotkey: "Mod-Shift-.", kind: { type: "quote" } },
  {
    id: "code",
    label: "Code block (Ctrl+Shift+E)",
    hotkey: "Mod-Shift-e",
    kind: { type: "fence" },
  },
  { id: "link", label: "Insert link (Ctrl+K)", hotkey: "Mod-k", kind: { type: "link" } },
  {
    id: "table",
    label: "Insert table (Ctrl+Alt+T)",
    hotkey: "Mod-Alt-t",
    kind: { type: "table" },
  },
  {
    id: "image",
    label: "Insert image reference (Ctrl+Shift+I)",
    hotkey: "Mod-Shift-i",
    kind: { type: "image" },
  },
  {
    id: "rule",
    label: "Insert divider (Ctrl+Shift+R)",
    hotkey: "Mod-Shift-r",
    kind: { type: "rule" },
  },
] as const satisfies readonly FormatCommandDef[];

/** The formatting commands the toolbar issues (shared by both editor surfaces), derived from the
 *  registry so the union can never fall out of step with what's actually declared. */
export type FormatCommand = (typeof FORMAT_REGISTRY)[number]["id"];

const BY_ID: ReadonlyMap<string, FormatCommandDef> = new Map(
  FORMAT_REGISTRY.map((command) => [command.id, command]),
);

/** The registry entry for a command. Always present for a {@link FormatCommand}; throws otherwise
 *  (a value crossed a boundary the types promised it couldn't). */
export function formatDef(command: FormatCommand): FormatCommandDef {
  const def = BY_ID.get(command);
  if (def === undefined) {
    throw new Error(`SpecDesk: no formatting command '${command}' in the registry`);
  }
  return def;
}

/** Validate a `data-format` attribute (DOM boundary) into a {@link FormatCommand}; false for anything else. */
export function isFormatCommand(value: string | undefined): value is FormatCommand {
  return value !== undefined && BY_ID.has(value);
}

/** Whether a command represents a state that can be toggled at the selection. Pure insertion actions
 *  use ordinary buttons and therefore must not expose the WAI-ARIA toggle-button state. */
export function isToggleFormat(command: FormatCommand): boolean {
  const { type } = formatDef(command).kind;
  return type !== "image" && type !== "table" && type !== "rule";
}
