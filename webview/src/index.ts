/**
 * SpecDesk webview entrypoint (PoC-2). Wires the CodeMirror editor, the rendered preview, and
 * scroll-sync to the native host: debounced edits go out as `editor.changed`; `preview.html` and
 * `doc.loaded` events come back. All Markdown rendering is native — this stays thin.
 */

import { MarkdownEditor } from "./editor.js";
import { HeightSync } from "./height-sync.js";
import { attachImageCapture } from "./image-capture.js";
import { ipc, postReady } from "./ipc.js";
import { log } from "./log.js";
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
import { ScrollSync } from "./scroll-sync.js";
import { isSplit, scrollAuthority, type ViewMode } from "./view-mode.js";

function wire(): void {
  const editorEl = document.querySelector<HTMLElement>("#editor");
  const previewEl = document.querySelector<HTMLElement>("#preview");
  const statusEl = document.querySelector<HTMLElement>("#status");
  const openBtn = document.querySelector<HTMLButtonElement>("#open-btn");
  const editBtn = document.querySelector<HTMLButtonElement>("#edit-btn");
  const saveVersionBtn = document.querySelector<HTMLButtonElement>("#save-version-btn");
  const discardBtn = document.querySelector<HTMLButtonElement>("#discard-btn");
  const saveBtn = document.querySelector<HTMLButtonElement>("#save-btn");
  const wrapBtn = document.querySelector<HTMLButtonElement>("#wrap-btn");
  const exportLogBtn = document.querySelector<HTMLButtonElement>("#export-log-btn");
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
  if (!editorEl || !previewEl) {
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

  const preview = new Preview(previewEl);
  let sync: ScrollSync | undefined;
  // Whether the document is currently editable (a draft is in progress). Drives the read-only
  // "start typing → offer to begin a draft" behaviour below.
  let editing = false;
  // The active view mode (code / split / formatted). Split is the default and the only mode where
  // both panes are visible, so height-sync and scroll-sync run only then. Declared before the editor
  // callbacks and `reconcile` below so their closures read the live value.
  let mode: ViewMode = "split";

  // Two cross-pane highlights: the caret line (prominent) and the mouse-hover line (faint,
  // auxiliary). Hover is suppressed when it coincides with the caret line so they don't fight.
  let activeLine = 0;
  let hoverLine: number | null = null;
  const applyHover = rafThrottle(() => {
    const line = hoverLine === activeLine ? null : hoverLine;
    editor.setHoverLine(line);
    preview.highlightHoverLine(line);
  });
  const setHover = (line: number | null): void => {
    hoverLine = line;
    applyHover();
  };

  const editor = new MarkdownEditor(editorEl, {
    onChange: (text, version) => {
      ipc.send(Kinds.editorChanged, { text }, { version });
    },
    onScroll: () => {
      if (isSplit(mode)) {
        sync?.fromEditor();
      }
    },
    onScrollSettle: () => {
      if (isSplit(mode)) {
        sync?.snapPreviewToEditor();
      }
    },
    onCursor: (line) => {
      activeLine = line;
      preview.highlightSourceLine(line);
      applyHover();
    },
    onHover: setHover,
    onGeometryChange: () => reconcile(),
    // Trying to type in a read-only document offers to start a draft (which forks a branch).
    onEditAttempt: () => {
      if (!editing) {
        void openBranchName();
      }
    },
  });

  preview.setOnHover(setHover);

  sync = new ScrollSync(editor, preview);
  const reportPreviewScroll = rafThrottle(() => {
    if (isSplit(mode)) {
      sync?.fromPreview();
    }
  });
  previewEl.addEventListener("scroll", reportPreviewScroll);

  // Height-sync: equalize editor/preview block heights so the panes align pixel-for-pixel, and feed
  // the resulting aligned anchors to scroll-sync (which maps pane→pane by interpolating between them).
  // The per-reconcile summary goes to the structured log (file), not the status bar.
  const heightSync = new HeightSync(
    editor,
    preview,
    (summary) => log.debug(summary),
    (anchors) => sync?.setAnchors(anchors),
  );
  // Height-sync equalizes two visible panes, so it only runs in split. In code/formatted one pane is
  // hidden (zero geometry); switching back to split re-runs it (see applyMode). The preview still
  // re-renders in every mode — only this equalization step is gated.
  const reconcile = rafThrottle(() => {
    if (!isSplit(mode)) {
      return;
    }
    sync?.suppress();
    heightSync.reconcile();
  });
  preview.setOnContentResize(reconcile);
  void document.fonts.ready.then(reconcile);

  // Switch the editor between code / split / formatted. The panes are only shown/hidden (CSS keyed
  // off #panes[data-mode]), never destroyed, so each keeps its caret/scroll/DOM. We carry the
  // reading position across in the stable source-line coordinate so it survives the width reflow of
  // a pane going full-width, and re-run height-sync on return to split.
  function applyMode(next: ViewMode): void {
    if (next === mode) {
      return;
    }
    // Capture the reading position from the pane that currently owns scroll, as a (possibly
    // fractional) source line — stable across the width reflow a pane undergoes when it goes
    // full-width or is un-hidden.
    const line =
      scrollAuthority(mode) === "editor"
        ? editor.topVisibleLineExact()
        : preview.topVisibleSourceLine();

    mode = next;
    if (panesEl) {
      panesEl.dataset.mode = next;
    }
    modeCodeBtn?.setAttribute("aria-pressed", String(next === "code"));
    modeSplitBtn?.setAttribute("aria-pressed", String(next === "split"));
    modeFormattedBtn?.setAttribute("aria-pressed", String(next === "formatted"));

    // Mute the echo window for the whole transition: the panes resize over the next couple of frames
    // and CodeMirror may nudge its scroll, which (with `mode` already switched) would otherwise drive
    // the preview off now-stale anchors before the restore below runs.
    sync?.suppress();

    // The visible pane(s) just changed width (or were un-hidden), so CodeMirror must re-measure
    // before we read/set its scroll geometry. That re-measure is asynchronous: a ResizeObserver
    // notification is delivered after this frame's rAF callbacks, and CodeMirror runs its measure
    // the frame after that. So we restore on the SECOND frame — restoring one frame earlier can read
    // stale geometry, and because a bare re-measure fires no transaction nothing would re-run
    // height-sync, leaving the split panes misaligned until the next edit/scroll.
    editor.refresh();
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        if (isSplit(next)) {
          // Re-equalize for the new widths, then position BOTH panes at the captured line. The
          // editor's scrollIntoView is applied asynchronously, so we must not drive the preview off
          // its (still stale) scrollTop here — instead scroll the preview to the same source line
          // directly. `suppress()` mutes the echo window so these two programmatic scrolls don't
          // drive each other; once the editor's scroll lands, the normal scroll/settle handlers
          // pixel-align the preview.
          sync?.suppress();
          heightSync.reconcile();
          editor.scrollToSourceLine(Math.floor(line));
          preview.scrollToSourceLine(line);
          preview.highlightSourceLine(activeLine);
        } else if (next === "code") {
          editor.scrollToSourceLine(Math.floor(line));
        } else {
          preview.scrollToSourceLine(line);
          preview.highlightSourceLine(activeLine);
        }
      });
    });
  }

  // On window resize both panes rewrap independently and height-sync must re-run. CodeMirror keeps
  // its pixel scroll position (so the editor's top line shifts), while the preview keeps its own —
  // so they drift apart. Re-equalize during the drag, then once it settles realign the preview to
  // the editor's top line.
  let resizeSettle: ReturnType<typeof setTimeout> | undefined;
  window.addEventListener("resize", () => {
    reconcile();
    if (resizeSettle !== undefined) {
      clearTimeout(resizeSettle);
    }
    resizeSettle = setTimeout(() => {
      heightSync.reconcile();
      // reconcile() refreshed the anchors; realign the preview to the editor through them.
      sync?.snapPreviewToEditor();
    }, 150);
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
    if (payload && preview.apply(payload.html, message.version ?? 0)) {
      reconcile();
    }
  });

  ipc.on(Kinds.docLoaded, (message) => {
    const payload = message.payload as DocLoadedPayload | undefined;
    if (payload) {
      editor.setText(payload.text);
      // Read-only until the author clicks Edit (which forks a working branch).
      editing = false;
      editor.setEditable(false);
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
    reconcile(); // re-equalize for the new wrap mode (rafThrottle defers it past the relayout)
  });

  exportLogBtn?.addEventListener("click", () => {
    ipc.send(Kinds.exportLog);
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
