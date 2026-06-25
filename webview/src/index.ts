/**
 * SpecDesk webview entrypoint (PoC-2). Wires the CodeMirror editor, the rendered preview, and
 * scroll-sync to the native host: debounced edits go out as `editor.changed`; `preview.html` and
 * `doc.loaded` events come back. All Markdown rendering is native — this stays thin.
 */

import { MarkdownEditor } from "./editor.js";
import { FormattedEditor } from "./formatted.js";
import { HeightSync } from "./height-sync.js";
import { attachImageCapture } from "./image-capture.js";
import { ipc, postReady } from "./ipc.js";
import { log } from "./log.js";
import type { FormatCommand } from "./md-format.js";
import { Preview } from "./preview.js";
import {
  type BranchNameSuggestedPayload,
  type DocLoadedPayload,
  type ErrorPayload,
  type ImageInsertedPayload,
  Kinds,
  type PreviewPayload,
  type StatusPayload,
  type VersionNoteSuggestedPayload,
} from "./protocol.js";
import { rafThrottle } from "./raf.js";
import { isSplit, type ViewMode } from "./view-mode.js";

function wire(): void {
  const editorEl = document.querySelector<HTMLElement>("#editor");
  const previewEl = document.querySelector<HTMLElement>("#preview");
  const formattedEl = document.querySelector<HTMLElement>("#formatted");
  const statusEl = document.querySelector<HTMLElement>("#status");
  const openBtn = document.querySelector<HTMLButtonElement>("#open-btn");
  const editBtn = document.querySelector<HTMLButtonElement>("#edit-btn");
  const saveVersionBtn = document.querySelector<HTMLButtonElement>("#save-version-btn");
  const discardBtn = document.querySelector<HTMLButtonElement>("#discard-btn");
  const saveBtn = document.querySelector<HTMLButtonElement>("#save-btn");
  const wrapBtn = document.querySelector<HTMLButtonElement>("#wrap-btn");
  const exportLogBtn = document.querySelector<HTMLButtonElement>("#export-log-btn");
  const themeBtn = document.querySelector<HTMLButtonElement>("#theme-btn");
  const panesEl = document.querySelector<HTMLElement>("#panes");
  const modeCodeBtn = document.querySelector<HTMLButtonElement>("#mode-code");
  const modeSplitBtn = document.querySelector<HTMLButtonElement>("#mode-split");
  const modeFormattedBtn = document.querySelector<HTMLButtonElement>("#mode-formatted");
  const versionNoteBar = document.querySelector<HTMLElement>("#version-note-bar");
  const versionNoteInput = document.querySelector<HTMLInputElement>("#version-note-input");
  const versionNoteTextarea = document.querySelector<HTMLTextAreaElement>("#version-note-textarea");
  const versionNoteExpand = document.querySelector<HTMLButtonElement>("#version-note-expand");
  const versionNoteConfirm = document.querySelector<HTMLButtonElement>("#version-note-confirm");
  const versionNoteCancel = document.querySelector<HTMLButtonElement>("#version-note-cancel");
  const branchNameBar = document.querySelector<HTMLElement>("#branch-name-bar");
  const branchNameInput = document.querySelector<HTMLInputElement>("#branch-name-input");
  const branchNameConfirm = document.querySelector<HTMLButtonElement>("#branch-name-confirm");
  const branchNameCancel = document.querySelector<HTMLButtonElement>("#branch-name-cancel");
  const formatBar = document.querySelector<HTMLElement>("#format-bar");
  const formatButtons = Array.from(
    document.querySelectorAll<HTMLButtonElement>("#format-bar button[data-format]"),
  );
  if (!editorEl || !previewEl || !formattedEl) {
    return;
  }

  // The draft-name (branch) prompt, revealed by "Edit". The host suggests a name; the author keeps
  // or changes it, then confirming forks the working branch and begins editing. Cancel/Esc backs out.
  function closeBranchName(): void {
    if (branchNameBar) {
      branchNameBar.hidden = true;
    }
  }

  // Keep the draft name a valid git ref as the author types: backslashes become '/', and spaces or
  // any other disallowed character become '_'. Length is preserved (1:1), so the caret stays put.
  // The host sanitizes again on submit (collapsing/trimming) as the authority.
  function sanitizeDraftName(value: string): string {
    return value.replace(/\\/g, "/").replace(/[^A-Za-z0-9._/-]/g, "_");
  }

  function confirmBranchName(): void {
    const branchName = branchNameInput?.value.trim() ?? "";
    ipc.send(Kinds.actionEdit, { branchName });
    closeBranchName();
  }

  async function openBranchName(): Promise<void> {
    // Already prompting → don't stack requests (e.g. repeated keystrokes in the read-only editor).
    if (branchNameBar && !branchNameBar.hidden) {
      return;
    }
    let suggested = "";
    try {
      const reply = await ipc.request(Kinds.branchNameRequest);
      suggested = (reply.payload as BranchNameSuggestedPayload | undefined)?.name ?? "";
    } catch (error) {
      log.warn("Could not fetch a suggested draft name", String(error));
    }
    if (branchNameInput) {
      branchNameInput.value = suggested;
    }
    if (branchNameBar) {
      branchNameBar.hidden = false;
    }
    branchNameInput?.focus();
    branchNameInput?.select();
  }

  // The version-note (commit message) inline editor. "Save version" asks the host for a suggested
  // note, lets the author edit it, then sends the explicit commit. It is single-line by default and
  // expands into a multi-line textarea on demand (⌄ button or Down arrow). Cancel/Esc backs out.
  function versionNoteMultiline(): boolean {
    return versionNoteTextarea !== null && !versionNoteTextarea.hidden;
  }

  function closeVersionNote(): void {
    if (versionNoteBar) {
      versionNoteBar.hidden = true;
    }
  }

  // Swap the single-line input for the multi-line textarea, carrying the text and caret intent over.
  function expandVersionNote(): void {
    if (!versionNoteTextarea || !versionNoteInput || versionNoteMultiline()) {
      return;
    }
    versionNoteTextarea.value = versionNoteInput.value;
    versionNoteInput.hidden = true;
    if (versionNoteExpand) {
      versionNoteExpand.hidden = true;
    }
    versionNoteTextarea.hidden = false;
    versionNoteTextarea.focus();
    const end = versionNoteTextarea.value.length;
    versionNoteTextarea.setSelectionRange(end, end);
  }

  function confirmVersionNote(): void {
    const raw = versionNoteMultiline()
      ? (versionNoteTextarea?.value ?? "")
      : (versionNoteInput?.value ?? "");
    ipc.send(Kinds.actionSaveVersion, { note: raw.trim() });
    closeVersionNote();
  }

  async function openVersionNote(): Promise<void> {
    if (versionNoteBar && !versionNoteBar.hidden) {
      return;
    }
    let suggested = "";
    try {
      const reply = await ipc.request(Kinds.versionNoteRequest);
      suggested = (reply.payload as VersionNoteSuggestedPayload | undefined)?.note ?? "";
    } catch (error) {
      log.warn("Could not fetch a suggested version note", String(error));
    }
    // Always reopen in the compact single-line state.
    if (versionNoteTextarea) {
      versionNoteTextarea.hidden = true;
    }
    if (versionNoteExpand) {
      versionNoteExpand.hidden = false;
    }
    if (versionNoteInput) {
      versionNoteInput.hidden = false;
      versionNoteInput.value = suggested;
    }
    if (versionNoteBar) {
      versionNoteBar.hidden = false;
    }
    versionNoteInput?.focus();
    versionNoteInput?.select();
  }

  // The native Markdig render. No longer a visible pane (Split now pairs the source editor with the
  // editable WYSIWYG); kept updated, hidden, as the canonical render for the future diff/comments.
  const preview = new Preview(previewEl);
  // A web link clicked in the preview opens in the OS browser (the host re-validates the scheme).
  preview.setOnOpenLink((url) => ipc.send(Kinds.openExternal, { url }));
  // Whether the document is currently editable (a draft is in progress). Drives the read-only
  // "start typing → offer to begin a draft" behaviour in both editors.
  let editing = false;
  // The active view mode. Code = source only; Split = source editor + formatted (WYSIWYG) editor,
  // both editable and synced live; Formatted = WYSIWYG only. Declared before the editor callbacks
  // below so their closures read the live value.
  let mode: ViewMode = "split";

  // One monotonic version across BOTH editors: the native side drops stale preview results by
  // version, so the two surfaces must share a single counter.
  let docVersion = 0;
  const sendDoc = (text: string): void => {
    docVersion += 1;
    ipc.send(Kinds.editorChanged, { text }, { version: docVersion });
  };

  // Block-level scroll-sync between the two editable panes in split (they couple by source line —
  // there is no shared native render to height-equalise against any more). A "driver" lock lets the
  // pane the user is scrolling keep driving while the other pane's programmatic echo is ignored;
  // suppressScroll() mutes both briefly around programmatic scrolls (edit mirror / mode switch).
  const SCROLL_SYNC_MS = 120;
  let scrollDriver: "editor" | "formatted" | "none" = "none";
  let scrollDriverUntil = 0;
  // When a genuine pane scroll last drove scroll-sync (top-aligned the other pane). While this is
  // recent, the passive pane is already being positioned by scroll-sync, so a caret-move reveal must
  // stand down — otherwise the two fight over the passive pane's scrollTop and it judders (most visible
  // holding an arrow key inside a tall table/list, where sub-block drift makes the two disagree).
  let lastScrollSyncAt = 0;
  const claimScroll = (who: "editor" | "formatted"): boolean => {
    const now = Date.now();
    if (scrollDriver !== who && now < scrollDriverUntil) {
      return false; // the other pane is driving (or a suppress window is active) — ignore the echo
    }
    scrollDriver = who;
    scrollDriverUntil = now + SCROLL_SYNC_MS;
    return true;
  };
  const suppressScroll = (): void => {
    scrollDriver = "none";
    scrollDriverUntil = Date.now() + SCROLL_SYNC_MS;
  };
  // Claim `who` as the authoritative scroll pane for the next sync window. Used when a deliberate caret
  // move in `who` reveals the synced highlight in the OTHER pane: that other pane's programmatic reveal
  // scroll must not echo back and drive `who`, yet `who`'s own caret-induced scroll must still sync
  // normally — so we make `who` the driver rather than muting both panes (which suppressScroll does).
  const driveScroll = (who: "editor" | "formatted"): void => {
    scrollDriver = who;
    scrollDriverUntil = Date.now() + SCROLL_SYNC_MS;
  };

  // Forward-declared so the cross-sync callbacks below can reference both editors; assigned just after.
  let editor: MarkdownEditor;
  let formatted: FormattedEditor;

  // Cross-pane highlight sync: a single active source line (the caret line) and a single hovered
  // source line, shown in BOTH panes — the source editor highlights the line, the formatted view
  // highlights the block containing it. Whichever pane the user interacts with reports its position
  // (rAF-throttled), and both panes are updated together, so the highlights stay in step in Split.
  // `reveal` is the pane the user just navigated the caret in (null = a text edit or a programmatic
  // set — highlight only, no reveal). After a deliberate caret move in Split, bring the synced
  // highlight into view on the OTHER (passive) pane: accumulated block-height drift can place it
  // outside that pane's viewport, where it would be invisible. Reveal is minimal (nearest) and only
  // touches the passive pane — never the one the user is reading. driveScroll keeps the active pane
  // authoritative so the passive reveal scroll doesn't echo back, while the active pane's own
  // scroll-sync keeps working (suppressScroll would have muted that too).
  const setActive = (line: number | null, reveal: "editor" | "formatted" | null): void => {
    editor.setActiveLine(line);
    formatted.setActiveLine(line);
    // Skip the reveal while scroll-sync is actively positioning the passive pane (a scroll drove it
    // within the last window): the two would otherwise fight over its scrollTop and judder. A discrete
    // caret move with no recent scroll (a click / single arrow) still reveals normally.
    if (
      reveal !== null &&
      line !== null &&
      isSplit(mode) &&
      Date.now() - lastScrollSyncAt >= SCROLL_SYNC_MS
    ) {
      driveScroll(reveal);
      if (reveal === "editor") {
        formatted.revealActiveBlock();
      } else {
        editor.revealSourceLine(line);
      }
    }
  };
  const setHover = (line: number | null): void => {
    editor.setHoverLine(line);
    formatted.setHoverLine(line);
  };

  // Formatting toolbar. It applies to the pane the author last worked in: Code → source editor
  // (Markdown text transforms), Formatted → WYSIWYG (ProseMirror commands), Split → whichever pane
  // last had focus (default the source editor). Active-button state is shown for the formatted pane.
  let lastFocused: "editor" | "formatted" = "editor";
  const formatTarget = (): "editor" | "formatted" =>
    mode === "code" ? "editor" : mode === "formatted" ? "formatted" : lastFocused;
  const refreshFormatButtons = (): void => {
    const active =
      formatTarget() === "formatted" ? formatted.activeFormats() : new Set<FormatCommand>();
    for (const button of formatButtons) {
      const command = button.dataset.format as FormatCommand;
      button.setAttribute("aria-pressed", String(active.has(command)));
    }
  };
  const runFormat = (command: FormatCommand): void => {
    if (formatTarget() === "formatted") {
      formatted.format(command);
    } else {
      editor.applyFormat(command);
    }
    refreshFormatButtons();
  };

  // Height-sync: pad the source editor with spacers so each source block's top lines up with its
  // rendered block in the formatted pane (the formatted view is the fixed reference, never padded).
  // Only meaningful in Split; rAF-throttled so a burst of edits/resizes reconciles once per frame.
  // suppressScroll() guards against the spacer change nudging the editor's scroll into a false sync.
  let heightSync: HeightSync;
  const reconcileHeights = rafThrottle(() => {
    if (isSplit(mode)) {
      suppressScroll();
      heightSync.reconcile();
    }
  });

  // Live content sync. An edit in one editor goes to the native pipeline (sendDoc) AND is mirrored
  // into the other — guarded by content equality so the mirror never echoes back, with a silent
  // setText so the mirror can't re-fire as an edit. After mirroring, the other pane's scroll is
  // re-aligned so it doesn't jump.
  const onEditorChange = (text: string): void => {
    sendDoc(text);
    if (formatted.getText() !== text) {
      formatted.setText(text);
      if (isSplit(mode)) {
        suppressScroll();
        formatted.scrollToSourceLine(editor.topVisibleLineExact());
      }
    }
    reconcileHeights();
  };
  const onFormattedChange = (text: string): void => {
    sendDoc(text);
    if (editor.getText() !== text) {
      editor.setText(text, true);
      if (isSplit(mode)) {
        suppressScroll();
        editor.scrollToSourceLine(formatted.topVisibleSourceLine());
      }
    }
    reconcileHeights();
  };

  const offerDraft = (): void => {
    if (!editing) {
      void openBranchName();
    }
  };

  editor = new MarkdownEditor(editorEl, {
    onChange: onEditorChange,
    onScroll: () => {
      if (isSplit(mode) && claimScroll("editor")) {
        formatted.scrollToSourceLine(editor.topVisibleLineExact());
        lastScrollSyncAt = Date.now();
      }
    },
    onScrollSettle: () => {},
    onCursor: (line, navigated) => setActive(line, navigated ? "editor" : null),
    onHover: (line) => setHover(line),
    onGeometryChange: () => reconcileHeights(),
    onEditAttempt: offerDraft,
    onFocus: () => {
      lastFocused = "editor";
      refreshFormatButtons();
    },
    // A web link Ctrl/Cmd-clicked in the source opens in the OS browser (the host re-validates it).
    onOpenLink: (url) => ipc.send(Kinds.openExternal, { url }),
  });

  // The formatted (WYSIWYG) editor — a sibling view of the same Markdown. Edits serialize back via
  // block-splice and go out through the SAME `editor.changed` channel as source edits.
  formatted = new FormattedEditor(formattedEl, {
    onChange: onFormattedChange,
    onEditAttempt: offerDraft,
    onScroll: () => {
      if (isSplit(mode) && claimScroll("formatted")) {
        editor.scrollToSourceLine(formatted.topVisibleSourceLine());
        lastScrollSyncAt = Date.now();
      }
    },
    onCursor: (line, navigated) => setActive(line, navigated ? "formatted" : null),
    onHover: (line) => setHover(line),
    onContentResize: () => reconcileHeights(),
    onFocus: () => {
      lastFocused = "formatted";
      refreshFormatButtons();
    },
    onActiveChange: () => refreshFormatButtons(),
    // A web link clicked in the WYSIWYG view opens in the OS browser (the host re-validates the scheme).
    onOpenLink: (url) => ipc.send(Kinds.openExternal, { url }),
  });

  // The source editor is padded to match the formatted view's block heights (formatted is the fixed
  // reference). Assigned now that both panes exist; reconcileHeights() drives it.
  heightSync = new HeightSync(editor, formatted);

  // Which surfaces a mode shows. Split shows both; the (in-sync) visible one is the source of truth
  // for the reading position and the canonical text when switching.
  const editorVisible = (m: ViewMode): boolean => m === "code" || m === "split";
  const formattedVisible = (m: ViewMode): boolean => m === "split" || m === "formatted";

  // Switch between code / split / formatted. Panes are only shown/hidden (CSS keyed off
  // #panes[data-mode]), never destroyed. A surface that becomes visible is hydrated from the current
  // text (silently), and the reading position is carried across in the source-line coordinate.
  function applyMode(next: ViewMode): void {
    if (next === mode) {
      return;
    }
    const prev = mode;
    const line = editorVisible(prev)
      ? editor.topVisibleLineExact()
      : formatted.topVisibleSourceLine();
    const text = editorVisible(prev) ? editor.getText() : formatted.getText();

    if (editorVisible(next) && !editorVisible(prev)) {
      editor.setText(text, true);
    }
    if (formattedVisible(next) && !formattedVisible(prev)) {
      formatted.setText(text);
    }

    mode = next;
    if (panesEl) {
      panesEl.dataset.mode = next;
    }
    modeCodeBtn?.setAttribute("aria-pressed", String(next === "code"));
    modeSplitBtn?.setAttribute("aria-pressed", String(next === "split"));
    modeFormattedBtn?.setAttribute("aria-pressed", String(next === "formatted"));
    editor.setEditable(editing);
    formatted.setEditable(editing);
    // The format target depends on the mode (Code→source, Formatted→WYSIWYG), so refresh the buttons.
    refreshFormatButtons();

    // The visible pane(s) changed width / were un-hidden, so re-measure before restoring scroll.
    // CodeMirror's re-measure is asynchronous, so restore on the SECOND frame (the PoC-11 timing).
    suppressScroll();
    editor.refresh();
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        suppressScroll();
        if (formattedVisible(next)) {
          formatted.refresh();
        }
        // Re-pad the editor to the formatted heights BEFORE restoring scroll, so the scroll target
        // accounts for the spacers. Outside Split there is nothing to align — drop the spacers so the
        // source has no meaningless gaps.
        if (isSplit(next)) {
          heightSync.reconcile();
        } else {
          heightSync.clear();
        }
        if (formattedVisible(next)) {
          formatted.scrollToSourceLine(Math.floor(line));
        }
        if (editorVisible(next)) {
          editor.scrollToSourceLine(Math.floor(line));
        }
      });
    });
  }

  // Re-measure CodeMirror on window resize and re-pad to the formatted heights (both panes reflow at
  // a new width); block-level scroll-sync re-aligns on the next scroll.
  window.addEventListener("resize", () => {
    editor.refresh();
    reconcileHeights();
  });

  attachImageCapture(editor, (image) => {
    void (async () => {
      try {
        const reply = await ipc.request(Kinds.imagePaste, {
          base64: image.base64,
          originalName: image.originalName,
          mime: image.mime,
        });
        const payload = reply.payload as ImageInsertedPayload | undefined;
        if (payload?.markdown) {
          editor.insertAt(image.pos, payload.markdown);
        }
      } catch (error) {
        log.error("Image paste request failed", String(error));
      }
    })();
  });

  ipc.on(Kinds.previewHtml, (message) => {
    const payload = message.payload as PreviewPayload | undefined;
    if (payload) {
      preview.apply(payload.html, message.version ?? 0);
    }
  });

  ipc.on(Kinds.docLoaded, (message) => {
    const payload = message.payload as DocLoadedPayload | undefined;
    if (payload) {
      editor.setText(payload.text);
      // Resolve relative image links in the formatted view against the document's folder. Set before
      // setText so the image node views render with the correct app://repo/… src.
      formatted.setDocDir(payload.docDir);
      // Hydrate the formatted view now too, so the Split pane isn't blank for one debounce interval
      // until the editor's mirror fires. setText is silent (no onChange), so this sends nothing.
      formatted.setText(payload.text);
      // Seed the synced highlight at the top of the document (both panes). No reveal: a freshly loaded
      // doc is scrolled to the top, so line 0 is already visible — and this is not a user navigation.
      setActive(0, null);
      setHover(null);
      // Align the source editor's line heights to the freshly rendered formatted blocks.
      reconcileHeights();
      // Read-only until the author clicks Edit (which forks a working branch).
      editing = false;
      editor.setEditable(false);
      formatted.setEditable(false);
      if (formatBar) {
        formatBar.hidden = true;
      }
      if (statusEl) {
        statusEl.textContent = payload.path;
      }
      // A freshly loaded document is Published: offer Edit, hide the draft-only actions.
      if (editBtn) {
        editBtn.hidden = false;
      }
      if (saveVersionBtn) {
        saveVersionBtn.hidden = true;
      }
      if (discardBtn) {
        discardBtn.hidden = true;
      }
      closeBranchName();
      closeVersionNote();
    }
  });

  // Lifecycle status (Draft / Unsaved changes / Version saved / Published). The author never sees
  // git vocabulary; the Edit button gives way to "Save version" + Discard once a draft is in
  // progress. Committing is explicit — typing only autosaves to disk, it never commits.
  ipc.on(Kinds.status, (message) => {
    const payload = message.payload as StatusPayload | undefined;
    if (!payload) {
      return;
    }
    if (statusEl) {
      statusEl.textContent = payload.label;
    }
    editing = payload.state !== "published";
    // Editing is only possible once a working branch exists (draft state).
    editor.setEditable(editing);
    formatted.setEditable(editing);
    // The formatting toolbar is editing chrome — shown only while a draft is in progress.
    if (formatBar) {
      formatBar.hidden = !editing;
    }
    if (editing) {
      refreshFormatButtons();
    }
    if (editBtn) {
      editBtn.hidden = editing;
    }
    if (saveVersionBtn) {
      saveVersionBtn.hidden = !editing;
    }
    if (discardBtn) {
      discardBtn.hidden = !editing;
    }
    if (!editing) {
      closeVersionNote();
    }
  });

  ipc.on(Kinds.error, (message) => {
    const payload = message.payload as ErrorPayload | undefined;
    if (payload && statusEl) {
      statusEl.textContent = payload.message;
    }
  });

  openBtn?.addEventListener("click", () => {
    ipc.send(Kinds.actionOpen);
  });
  editBtn?.addEventListener("click", () => {
    void openBranchName();
  });
  branchNameConfirm?.addEventListener("click", confirmBranchName);
  branchNameCancel?.addEventListener("click", closeBranchName);
  // Live-clean the draft name to a valid ref as it is typed, keeping the caret in place.
  branchNameInput?.addEventListener("input", () => {
    if (!branchNameInput) {
      return;
    }
    const caret = branchNameInput.selectionStart;
    const cleaned = sanitizeDraftName(branchNameInput.value);
    if (cleaned !== branchNameInput.value) {
      branchNameInput.value = cleaned;
      if (caret !== null) {
        branchNameInput.setSelectionRange(caret, caret);
      }
    }
  });
  branchNameInput?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      confirmBranchName();
    } else if (event.key === "Escape") {
      event.preventDefault();
      closeBranchName();
    }
  });
  saveVersionBtn?.addEventListener("click", () => {
    void openVersionNote();
  });
  versionNoteConfirm?.addEventListener("click", confirmVersionNote);
  versionNoteCancel?.addEventListener("click", closeVersionNote);
  versionNoteExpand?.addEventListener("click", expandVersionNote);
  // Single-line: Enter saves, Down arrow expands to the multi-line editor, Esc cancels.
  versionNoteInput?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      confirmVersionNote();
    } else if (event.key === "ArrowDown") {
      event.preventDefault();
      expandVersionNote();
    } else if (event.key === "Escape") {
      event.preventDefault();
      closeVersionNote();
    }
  });
  // Multi-line: Enter inserts a newline (default), Ctrl/Cmd+Enter saves, Esc cancels.
  versionNoteTextarea?.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      confirmVersionNote();
    } else if (event.key === "Escape") {
      event.preventDefault();
      closeVersionNote();
    }
  });
  discardBtn?.addEventListener("click", () => {
    ipc.send(Kinds.actionDiscard);
  });
  saveBtn?.addEventListener("click", () => {
    ipc.send(Kinds.actionSave);
  });

  let wrap = true;
  wrapBtn?.addEventListener("click", () => {
    wrap = !wrap;
    editor.setLineWrapping(wrap);
    wrapBtn.textContent = `Wrap: ${wrap ? "on" : "off"}`;
    wrapBtn.setAttribute("aria-pressed", String(wrap));
  });

  exportLogBtn?.addEventListener("click", () => {
    ipc.send(Kinds.exportLog);
  });

  // Formatting toolbar buttons. mousedown is prevented so the click never steals focus from the
  // editor — the selection it acts on stays intact and `lastFocused` keeps pointing at the right pane.
  for (const button of formatButtons) {
    const command = button.dataset.format as FormatCommand;
    button.addEventListener("mousedown", (event) => event.preventDefault());
    button.addEventListener("click", () => runFormat(command));
  }

  // Light/dark theme. The bare :root is the light (cool) theme, so "light" means no data-theme
  // attribute and dark sets data-theme="dark" (see styles.css). Default to the OS colour scheme; the
  // toolbar toggle flips it. Persistence across app restarts is out of scope for this pass.
  function applyTheme(dark: boolean): void {
    if (dark) {
      document.documentElement.dataset.theme = "dark";
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
    if (themeBtn) {
      themeBtn.setAttribute("aria-pressed", String(dark));
      themeBtn.textContent = dark ? "Light" : "Dark";
    }
  }
  applyTheme(window.matchMedia("(prefers-color-scheme: dark)").matches);
  themeBtn?.addEventListener("click", () => {
    applyTheme(document.documentElement.dataset.theme !== "dark");
  });

  modeCodeBtn?.addEventListener("click", () => applyMode("code"));
  modeSplitBtn?.addEventListener("click", () => applyMode("split"));
  modeFormattedBtn?.addEventListener("click", () => applyMode("formatted"));

  ipc.start();
  postReady();
  log.info("Webview ready");
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", wire);
} else {
  wire();
}
