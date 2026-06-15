/**
 * SpecDesk webview entrypoint (PoC-2). Wires the CodeMirror editor, the rendered preview, and
 * scroll-sync to the native host: debounced edits go out as `editor.changed`; `preview.html` and
 * `doc.loaded` events come back. All Markdown rendering is native — this stays thin.
 */

import { MarkdownEditor } from "./editor.js";
import { HeightSync } from "./height-sync.js";
import { attachImageCapture } from "./image-capture.js";
import { ipc, postReady } from "./ipc.js";
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
  const traceBtn = document.querySelector<HTMLButtonElement>("#trace-btn");
  if (!editorEl || !previewEl) {
    return;
  }

  const preview = new Preview(previewEl);
  let sync: ScrollSync | undefined;

  // Diagnostic trace: when on, every height-sync summary is timestamped into a buffer; turning it
  // off offers to save the buffer to a file (for diagnosing alignment without screenshots).
  let tracing = false;
  const traceLines: string[] = [];

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
  // TEMP: surface the computed adjustments in the status bar to diagnose alignment.
  const heightSync = new HeightSync(editor, preview, (summary) => {
    if (statusEl) {
      statusEl.textContent = summary;
    }
    if (tracing) {
      traceLines.push(`${performance.now().toFixed(0)}ms ${summary}`);
    }
  });
  const reconcile = rafThrottle(() => {
    sync?.suppress();
    heightSync.reconcile();
  });
  preview.setOnContentResize(reconcile);
  window.addEventListener("resize", reconcile);
  void document.fonts.ready.then(reconcile);

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
        if (statusEl) {
          statusEl.textContent = `Image insert failed: ${String(error)}`;
        }
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

  traceBtn?.addEventListener("click", () => {
    tracing = !tracing;
    traceBtn.textContent = `Trace: ${tracing ? "on" : "off"}`;
    traceBtn.setAttribute("aria-pressed", String(tracing));
    if (tracing) {
      traceLines.length = 0; // start a fresh capture
    } else {
      // Always hand the trace to the host so the Save dialog reliably opens, even if nothing was
      // captured (e.g. no reconcile happened while tracing).
      const text =
        traceLines.length > 0
          ? traceLines.join("\n")
          : "(no height-sync events were captured during this trace)";
      ipc.send(Kinds.traceSave, { text });
    }
  });

  ipc.start();
  postReady();
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", wire);
} else {
  wire();
}
