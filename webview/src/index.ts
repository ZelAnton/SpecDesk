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
  type DocLoadedPayload,
  type ErrorPayload,
  type ImageInsertedPayload,
  Kinds,
  type PreviewPayload,
} from "./protocol.js";
import { rafThrottle } from "./raf.js";
import { ScrollSync } from "./scroll-sync.js";

function wire(): void {
  const editorEl = document.querySelector<HTMLElement>("#editor");
  const previewEl = document.querySelector<HTMLElement>("#preview");
  const statusEl = document.querySelector<HTMLElement>("#status");
  const openBtn = document.querySelector<HTMLButtonElement>("#open-btn");
  const saveBtn = document.querySelector<HTMLButtonElement>("#save-btn");
  const wrapBtn = document.querySelector<HTMLButtonElement>("#wrap-btn");
  const exportLogBtn = document.querySelector<HTMLButtonElement>("#export-log-btn");
  if (!editorEl || !previewEl) {
    return;
  }

  const preview = new Preview(previewEl);
  let sync: ScrollSync | undefined;

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
    onScroll: (topLine) => sync?.fromEditor(topLine),
    onScrollSettle: () => sync?.snapPreviewToEditor(),
    onCursor: (line) => {
      activeLine = line;
      preview.highlightSourceLine(line);
      applyHover();
    },
    onHover: setHover,
    onGeometryChange: () => reconcile(),
  });

  preview.setOnHover(setHover);

  sync = new ScrollSync(editor, preview);
  const reportPreviewScroll = rafThrottle(() => sync?.fromPreview());
  previewEl.addEventListener("scroll", reportPreviewScroll);

  // Height-sync: equalize editor/preview block heights so the panes align pixel-for-pixel.
  // The per-reconcile summary goes to the structured log (file), not the status bar.
  const heightSync = new HeightSync(editor, preview, (summary) => log.debug(summary));
  const reconcile = rafThrottle(() => {
    sync?.suppress();
    heightSync.reconcile();
  });
  preview.setOnContentResize(reconcile);
  void document.fonts.ready.then(reconcile);

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
      sync?.suppress();
      preview.scrollToSourceLine(editor.topVisibleLineExact());
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
      if (statusEl) {
        statusEl.textContent = payload.path;
      }
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

  ipc.start();
  postReady();
  log.info("Webview ready");
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", wire);
} else {
  wire();
}
