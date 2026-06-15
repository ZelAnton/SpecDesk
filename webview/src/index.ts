/**
 * SpecDesk webview entrypoint (PoC-2). Wires the CodeMirror editor, the rendered preview, and
 * scroll-sync to the native host: debounced edits go out as `editor.changed`; `preview.html` and
 * `doc.loaded` events come back. All Markdown rendering is native — this stays thin.
 */

import { MarkdownEditor } from "./editor.js";
import { ipc, postReady } from "./ipc.js";
import { Preview } from "./preview.js";
import {
  type DocLoadedPayload,
  type ErrorPayload,
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
  if (!editorEl || !previewEl) {
    return;
  }

  const preview = new Preview(previewEl);
  let sync: ScrollSync | undefined;

  const editor = new MarkdownEditor(editorEl, {
    onChange: (text, version) => {
      ipc.send(Kinds.editorChanged, { text }, { version });
    },
    onScroll: (topLine) => sync?.fromEditor(topLine),
  });

  sync = new ScrollSync(editor, preview);
  const reportPreviewScroll = rafThrottle(() => sync?.fromPreview());
  previewEl.addEventListener("scroll", reportPreviewScroll);

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

  ipc.start();
  postReady();
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", wire);
} else {
  wire();
}
