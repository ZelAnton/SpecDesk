/**
 * SpecDesk webview entrypoint (PoC-2). Wires the CodeMirror editor, the rendered preview, and
 * scroll-sync to the native host: debounced edits go out as `editor.changed`; `preview.html` and
 * `doc.loaded` events come back. All Markdown rendering is native — this stays thin.
 */

import { Dialogs } from "./chrome/dialogs.js";
import { FormatToolbar } from "./chrome/format-toolbar.js";
import { LifecycleChrome } from "./chrome/lifecycle-chrome.js";
import { SegmentedControl, type SegmentedOption } from "./chrome/segmented-control.js";
import { SignInController } from "./chrome/signin.js";
import { isSplit, isViewMode, paneVisibility, type ViewMode } from "./chrome/view-mode.js";
import { MarkdownEditor } from "./editors/editor.js";
import { FormattedEditor } from "./editors/formatted.js";
import { Preview } from "./review/preview.js";
import { ReviewController } from "./review/review.js";
import { ReviewsPanel } from "./review/reviews-panel.js";
import { HeightSync } from "./sync/height-sync.js";
import { type Pane, SplitSync } from "./sync/sync-coordinator.js";
import { attachImageCapture } from "./util/image-capture.js";
import { log } from "./util/log.js";
import { rafThrottle } from "./util/raf.js";
import {
  parseBranchNameSuggested,
  parseDiffResult,
  parseDocLoaded,
  parseError,
  parseGitHubAccount,
  parseGitHubCode,
  parseImageInserted,
  parsePreview,
  parsePrList,
  parsePrSuggested,
  parseStatus,
  parseVersionNoteSuggested,
} from "./wire/decoders.js";
import { ipc, postReady } from "./wire/ipc.js";
import { isReviewState, Kinds } from "./wire/protocol.js";

/** The slice of a pane the Split cross-mirror needs — both MarkdownEditor and FormattedEditor satisfy it. */
interface MirrorTarget {
  getText(): string;
  hasPendingChange(): boolean;
}

/**
 * Whether an edit's text should be mirrored into `destination`: false when it already matches (nothing
 * to do) or `destination` itself has a not-yet-reported edit pending. Each pane's onChange only fires
 * once ITS OWN 120ms debounce settles, so the two panes' change notifications are not ordered: if the
 * author edits pane A, then (within that same 120ms window) edits pane B, A's debounce — started first —
 * can fire first and reach here BEFORE B's does. Mirroring A's (now-stale) text into B at that point
 * would silently clobber B's still-unsent keystrokes with an older snapshot. Skipping the mirror here
 * loses nothing: B's own debounce fires shortly after and mirrors ITS (newer) text back.
 */
export function shouldMirrorInto(text: string, destination: MirrorTarget): boolean {
  return destination.getText() !== text && !destination.hasPendingChange();
}

function wire(): void {
  const editorEl = document.querySelector<HTMLElement>("#editor");
  const previewEl = document.querySelector<HTMLElement>("#preview");
  const formattedEl = document.querySelector<HTMLElement>("#formatted");
  const statusEl = document.querySelector<HTMLElement>("#status");
  const openBtn = document.querySelector<HTMLButtonElement>("#open-btn");
  const editBtn = document.querySelector<HTMLButtonElement>("#edit-btn");
  const saveVersionBtn = document.querySelector<HTMLButtonElement>("#save-version-btn");
  const sendForReviewBtn = document.querySelector<HTMLButtonElement>("#send-for-review-btn");
  const updateReviewBtn = document.querySelector<HTMLButtonElement>("#update-review-btn");
  const discardBtn = document.querySelector<HTMLButtonElement>("#discard-btn");
  const saveBtn = document.querySelector<HTMLButtonElement>("#save-btn");
  const wrapBtn = document.querySelector<HTMLButtonElement>("#wrap-btn");
  const exportLogBtn = document.querySelector<HTMLButtonElement>("#export-log-btn");
  const themeBtn = document.querySelector<HTMLButtonElement>("#theme-btn");
  const reviewsBtn = document.querySelector<HTMLButtonElement>("#reviews-btn");
  const panesEl = document.querySelector<HTMLElement>("#panes");
  const skipLink = document.querySelector<HTMLAnchorElement>(".skip-link");
  const modeCodeBtn = document.querySelector<HTMLButtonElement>("#mode-code");
  const modeSplitBtn = document.querySelector<HTMLButtonElement>("#mode-split");
  const modeFormattedBtn = document.querySelector<HTMLButtonElement>("#mode-formatted");
  const compareBtn = document.querySelector<HTMLButtonElement>("#compare-btn");
  const reviewEmptyEl = document.querySelector<HTMLElement>("#review-empty-bar");
  const reviewOverflowEl = document.querySelector<HTMLElement>("#review-overflow-bar");
  const formatBar = document.querySelector<HTMLElement>("#format-bar");
  const formatButtons = Array.from(
    document.querySelectorAll<HTMLButtonElement>("#format-bar button[data-format]"),
  );

  // The inline prompt bars' own elements (dialogs.ts).
  const branchNameBar = document.querySelector<HTMLElement>("#branch-name-bar");
  const branchNameInput = document.querySelector<HTMLInputElement>("#branch-name-input");
  const branchNameConfirm = document.querySelector<HTMLButtonElement>("#branch-name-confirm");
  const branchNameCancel = document.querySelector<HTMLButtonElement>("#branch-name-cancel");
  const versionNoteBar = document.querySelector<HTMLElement>("#version-note-bar");
  const versionNoteInput = document.querySelector<HTMLInputElement>("#version-note-input");
  const versionNoteTextarea = document.querySelector<HTMLTextAreaElement>("#version-note-textarea");
  const versionNoteExpand = document.querySelector<HTMLButtonElement>("#version-note-expand");
  const versionNoteConfirm = document.querySelector<HTMLButtonElement>("#version-note-confirm");
  const versionNoteCancel = document.querySelector<HTMLButtonElement>("#version-note-cancel");
  const prTextBar = document.querySelector<HTMLElement>("#pr-text-bar");
  const prTitleInput = document.querySelector<HTMLInputElement>("#pr-title-input");
  const prBodyTextarea = document.querySelector<HTMLTextAreaElement>("#pr-body-textarea");
  const prTextConfirm = document.querySelector<HTMLButtonElement>("#pr-text-confirm");
  const prTextCancel = document.querySelector<HTMLButtonElement>("#pr-text-cancel");

  // The GitHub account affordance + sign-in code bar's own elements (signin.ts).
  const githubBtn = document.querySelector<HTMLButtonElement>("#github-btn");
  const githubSigninBar = document.querySelector<HTMLElement>("#github-signin-bar");
  const githubSigninText = document.querySelector<HTMLElement>("#github-signin-text");
  const githubUserCode = document.querySelector<HTMLElement>("#github-user-code");
  const githubOpenBtn = document.querySelector<HTMLButtonElement>("#github-open-btn");
  const githubSigninStatus = document.querySelector<HTMLElement>("#github-signin-status");
  const githubCancelBtn = document.querySelector<HTMLButtonElement>("#github-cancel-btn");

  // The "My reviews" panel's own elements (reviews-panel.ts).
  const reviewsPanelEl = document.querySelector<HTMLElement>("#reviews-panel");
  const reviewsList = document.querySelector<HTMLElement>("#reviews-list");
  const reviewsStatus = document.querySelector<HTMLElement>("#reviews-status");
  const reviewsCloseBtn = document.querySelector<HTMLButtonElement>("#reviews-close");
  const reviewsUrlInput = document.querySelector<HTMLInputElement>("#reviews-url-input");
  const reviewsUrlOpenBtn = document.querySelector<HTMLButtonElement>("#reviews-url-open");

  if (!editorEl || !previewEl || !formattedEl) {
    return;
  }
  // Non-null aliases for the three required host elements: the guard above narrows them here, but that
  // control-flow narrowing does not carry into the nested wire* closures below, so pin it once.
  const editorRoot = editorEl;
  const previewRoot = previewEl;
  const formattedRoot = formattedEl;

  // Whether the document is currently editable (a draft is in progress). Drives the read-only
  // "start typing → offer to begin a draft" behaviour in both editors.
  let editing = false;
  // Whether the document is under review (In review / Changes requested / Approved). While it is, a
  // window-focus refreshes the review status from GitHub — a reviewer may have acted out of band.
  let underReview = false;
  // The active view mode. Code = source only; Split = source editor + formatted (WYSIWYG) editor,
  // both editable and synced live; Formatted = WYSIWYG only. Declared before the editor callbacks
  // below so their closures read the live value. The single declared source of truth for the STARTING
  // mode is the `data-mode` attribute #panes carries in index.html (the same attribute the CSS keys
  // pane visibility off) — read it back here rather than repeating the literal, so the DOM and this
  // variable cannot drift apart; "split" is only a defensive fallback if the markup is ever missing it.
  const initialModeAttr = panesEl?.dataset.mode;
  let mode: ViewMode = isViewMode(initialModeAttr) ? initialModeAttr : "split";
  // One monotonic version across BOTH editors: the native side drops stale preview results by
  // version, so the two surfaces must share a single counter.
  let docVersion = 0;

  // The editors and their controllers are forward-declared here so every wiring group below can close
  // over them; wireEditors() / wireLifecycle() assign them. Every read happens later (inside a callback
  // or an ipc handler that fires after all four groups have run), so the forward reference is safe.
  let editor: MarkdownEditor;
  let formatted: FormattedEditor;
  let review: ReviewController;
  let formatToolbar: FormatToolbar;
  let heightSync: HeightSync;
  // The single owner of both panes' Split scroll position: it couples them through the line↔px map,
  // reveals the synced highlight, and restores the reading position on a mode switch — replacing the old
  // ScrollSync driver lock with deterministic, echo-free writes (sync-coordinator.ts).
  let splitSync: SplitSync;
  let lifecycleChrome: LifecycleChrome;

  // Show a plain (non-lifecycle) message in the status area — a document path, a host error, or a
  // "can't do that yet" notice — clearing the lifecycle dot's state colour (the next status re-colours it).
  function showPlainStatus(text: string): void {
    if (statusEl) {
      statusEl.textContent = text;
      delete statusEl.dataset.state;
    }
  }

  // One-shot request→reply for a host suggestion (draft name / version note / review text): await the
  // reply, decode it, and fall back on a malformed reply or a transport fault. One place for the shared
  // request/parse/failure shape so the three suggest callbacks can't drift.
  async function requestSuggestion<T>(
    kind: string,
    parse: (payload: unknown) => T | null,
    fallback: T,
  ): Promise<T> {
    try {
      const reply = await ipc.request(kind);
      return parse(reply.payload) ?? fallback;
    } catch (error) {
      log.warn(`Could not fetch a suggestion (${kind})`, String(error));
      return fallback;
    }
  }

  // Review-status refresh cadence. While a document is under review the status is polled — a reviewer can
  // act while the SpecDesk window stays focused, so a focus event alone would miss it — and also refreshed
  // whenever the window regains focus (the "check GitHub, come back" gesture). Both just send review.refresh;
  // the host single-flights and coalesces (a request arriving mid-read queues exactly one follow-up), so
  // rapid focus/poll overlap can't fan out queries and a focus refresh is never dropped.
  const REVIEW_POLL_INTERVAL_MS = 45_000;
  let reviewPollTimer: number | undefined;

  // Poll while under review, but each tick only fires when the window actually has focus — an unfocused
  // window can't show a change the author isn't looking at, and the focus handler already catches any
  // decision on return, so background polling would just burn GitHub API budget. Checking focus at tick time
  // (not a cached flag) is robust to a missed focus/blur event. Called whenever `underReview` changes.
  function syncReviewPolling(): void {
    if (underReview && reviewPollTimer === undefined) {
      reviewPollTimer = window.setInterval(() => {
        if (document.hasFocus()) {
          ipc.send(Kinds.reviewRefresh);
        }
      }, REVIEW_POLL_INTERVAL_MS);
    } else if (!underReview && reviewPollTimer !== undefined) {
      window.clearInterval(reviewPollTimer);
      reviewPollTimer = undefined;
    }
  }

  // The inline prompt bars (draft name on Edit, version note on Save version, review text on Send). They
  // reach the host only through these callbacks — the integrator keeps the ipc/Kinds knowledge (dialogs.ts).
  const dialogs = new Dialogs({
    branchNameBar,
    branchNameInput,
    branchNameConfirm,
    branchNameCancel,
    versionNoteBar,
    versionNoteInput,
    versionNoteTextarea,
    versionNoteExpand,
    versionNoteConfirm,
    versionNoteCancel,
    prTextBar,
    prTitleInput,
    prBodyTextarea,
    prTextConfirm,
    prTextCancel,
    suggestBranchName: () =>
      requestSuggestion(
        Kinds.branchNameRequest,
        (p) => parseBranchNameSuggested(p)?.name ?? null,
        "",
      ),
    onBranchName: (branchName) => ipc.send(Kinds.docEdit, { branchName }),
    suggestVersionNote: () =>
      requestSuggestion(
        Kinds.versionNoteRequest,
        (p) => parseVersionNoteSuggested(p)?.note ?? null,
        "",
      ),
    onVersionNote: (note) => {
      ipc.send(Kinds.docSaveVersion, { note });
      // Saving a version advances the base the overlay diffs against, so any showing overlay is now stale.
      review.clear();
    },
    // On a failed suggestion (malformed reply or transport fault) return a blocked result so the prompt
    // stays closed rather than opening empty — the host reply is the sole authority on readiness.
    suggestPrText: () =>
      requestSuggestion(Kinds.prSuggestedRequest, parsePrSuggested, {
        title: "",
        body: "",
        blocked: "Couldn't prepare the review. Try again.",
      }),
    // The send can't proceed (not connected, not a GitHub repo, no saved version): show the plain reason
    // the same way a host error is shown, and leave the prompt closed.
    onPrBlocked: (reason) => showPlainStatus(reason),
    onPrText: ({ title, body }) => ipc.send(Kinds.docSendForReview, { title, body }),
  });

  // Wiring group 1 — the editing surfaces: both editors, their live cross-mirror + highlight/scroll
  // sync, the height-sync padding, the review/compare overlay, the formatting toolbar, image paste,
  // and the native preview / diff / doc.loaded events that feed them.
  function wireEditors(): void {
    // The native Markdig render. No longer a visible pane (Split now pairs the source editor with the
    // editable WYSIWYG), and — since T-089 — the host no longer renders/sends it on every edit either
    // (there is no consumer, visible or otherwise, today). `preview` and its previewHtml handler below
    // stay wired as the ready sink for the day a real consumer (diff/comments) needs it.
    const preview = new Preview(previewRoot);
    // A web link clicked in the preview opens in the OS browser (the host re-validates the scheme).
    preview.setOnOpenLink((url) => ipc.send(Kinds.linkOpen, { url }));

    const sendDoc = (text: string): void => {
      docVersion += 1;
      ipc.send(Kinds.editorChanged, { text }, { version: docVersion });
    };

    // Cross-pane highlight sync: a single active source line (the caret line) and a single hovered
    // source line, shown in BOTH panes — the source editor highlights the line, the formatted view
    // highlights the block containing it. Whichever pane the user interacts with reports its position
    // (rAF-throttled), and both panes are updated together, so the highlights stay in step in Split.
    // `reveal` is the pane the user just navigated the caret in (null = a text edit or a programmatic
    // set — highlight only, no reveal). After a deliberate caret move in Split, hand the reveal to the
    // coordinator: it brings the synced highlight into view on the OTHER (passive) pane — accumulated
    // block-height drift can place it outside that pane's viewport — as the sole owner of that pane's
    // scroll, so the reveal can't echo back, and it stands the reveal down while a scroll just coupled the
    // panes (the one anti-judder fallback, replacing the old drive/syncedRecently lock).
    const setActive = (line: number | null, reveal: Pane | null): void => {
      editor.setActiveLine(line);
      formatted.setActiveLine(line);
      if (reveal !== null && line !== null && isSplit(mode)) {
        splitSync.reveal(line, reveal);
      }
    };
    const setHover = (line: number | null): void => {
      editor.setHoverLine(line);
      formatted.setHoverLine(line);
    };

    // Height-sync: pad the source editor with spacers so each source block's top lines up with its
    // rendered block in the formatted pane (the formatted view is the fixed reference, never padded).
    // Only meaningful in Split; rAF-throttled so a burst of edits/resizes reconciles once per frame.
    // Reconciling shifts the editor's line tops (new spacers) and can nudge its scrollTop (the spacer-
    // weight compensation), so afterwards the coordinator's `reconciled()` invalidates its maps, absorbs
    // the editor's post-nudge scroll (so that programmatic move is not read as a user scroll), and
    // re-aligns the passive pane from whichever pane is active — the coordinator now owns that "which pane
    // leads" decision (formerly a `leadingPane` variable tracked here).
    const reconcileHeights = rafThrottle(() => {
      if (isSplit(mode)) {
        heightSync.reconcile();
        splitSync.reconciled();
      }
    });

    // Live content sync. An edit in one editor goes to the native pipeline (sendDoc) AND is mirrored
    // into the other — guarded by shouldMirrorInto (content equality, plus the pending-edit check below)
    // so the mirror never echoes back and never clobbers an unsent edit. The mirror applies the SMALLEST
    // change that reconciles the two panes (editor.mirror / formatted.mirror), so the passive pane's
    // caret, selection and undo history survive the sync (a whole-document replace would collapse them);
    // it is silent so a mirror that DOES apply can't re-fire as an edit. After mirroring, the other
    // pane's scroll is re-aligned so it doesn't jump.
    const onEditorChange = (text: string): void => {
      // A genuine edit invalidates the compare snapshot — drop the overlay (the mirror below is silent
      // and re-applies nothing once cleared).
      review.clear();
      sendDoc(text);
      if (shouldMirrorInto(text, formatted)) {
        formatted.mirror(text);
        // Re-align the just-mirrored pane to the editor's top line through the coordinator (which records
        // the write, so the re-align can't echo back), so the mirrored content doesn't jump.
        if (isSplit(mode)) {
          splitSync.syncFrom("editor");
        }
      }
      reconcileHeights();
    };
    const onFormattedChange = (text: string): void => {
      review.clear();
      sendDoc(text);
      if (shouldMirrorInto(text, editor)) {
        editor.mirror(text);
        if (isSplit(mode)) {
          splitSync.syncFrom("formatted");
        }
      }
      reconcileHeights();
    };

    const offerDraft = (): void => {
      if (!editing) {
        void dialogs.openBranchName();
      }
    };

    editor = new MarkdownEditor(editorRoot, {
      onChange: onEditorChange,
      onScroll: () => {
        // The coordinator decides everything: a genuine editor scroll becomes active and couples the
        // formatted pane; its own echo (the scrollTop it just wrote) is ignored deterministically — no
        // driver lock, and the "which pane leads" bookkeeping now lives in the coordinator, not here.
        if (isSplit(mode)) {
          splitSync.onEditorScroll();
        }
      },
      onScrollSettle: () => {
        // The rAF-coupled frames can trail a momentum scroll's final position. Re-run the same exact
        // coordinator path once the source pane stops moving; the coordinator suppresses an echo settle.
        if (isSplit(mode)) {
          splitSync.settle("editor");
        }
      },
      onCursor: (line, navigated) => {
        setActive(line, navigated ? "editor" : null);
        // The source pane's toolbar state depends on the caret's syntax-tree context (T-100) — refresh
        // on every caret report, mirroring the formatted pane's onActiveChange below.
        formatToolbar.refresh();
      },
      onHover: (line) => setHover(line),
      onGeometryChange: () => reconcileHeights(),
      onEditAttempt: offerDraft,
      onFocus: () => {
        // Focus declares the editor active and best-effort syncs the formatted pane from it (the
        // coordinator owns that); the reversible map keeps a focus change from jumping the sibling.
        if (isSplit(mode)) {
          splitSync.onFocus("editor");
        }
        formatToolbar.setFocused("editor");
      },
      // A web link Ctrl/Cmd-clicked in the source opens in the OS browser (the host re-validates it).
      onOpenLink: (url) => ipc.send(Kinds.linkOpen, { url }),
    });

    // The formatted (WYSIWYG) editor — a sibling view of the same Markdown. Edits serialize back via
    // block-splice and go out through the SAME `editor.changed` channel as source edits.
    formatted = new FormattedEditor(formattedRoot, {
      onChange: onFormattedChange,
      onEditAttempt: offerDraft,
      onScroll: () => {
        if (isSplit(mode)) {
          splitSync.onFormattedScroll();
        }
      },
      // Symmetric with the source editor: re-snap once the formatted pane's momentum/trackpad scroll
      // settles, through the same coordinator path (an echo settle is suppressed).
      onScrollSettle: () => {
        if (isSplit(mode)) {
          splitSync.settle("formatted");
        }
      },
      onCursor: (line, navigated) => setActive(line, navigated ? "formatted" : null),
      onHover: (line) => setHover(line),
      onContentResize: () => reconcileHeights(),
      onFocus: () => {
        if (isSplit(mode)) {
          splitSync.onFocus("formatted");
        }
        formatToolbar.setFocused("formatted");
      },
      onActiveChange: () => formatToolbar.refresh(),
      // A web link clicked in the WYSIWYG view opens in the OS browser (the host re-validates the scheme).
      onOpenLink: (url) => ipc.send(Kinds.linkOpen, { url }),
    });

    // The source editor is padded to match the formatted view's block heights (formatted is the fixed
    // reference). Assigned now that both panes exist; reconcileHeights() drives it.
    heightSync = new HeightSync(editor, formatted);

    // The single scroll coordinator, now that both panes exist. It owns each pane's scrollTop for Split
    // coupling, reveal, and mode-switch restore — the editors expose the small line↔px surface it needs.
    splitSync = new SplitSync(editor, formatted);

    // The review/compare overlay state machine, now that both surfaces exist. It washes changes in both
    // editors (the first is the canonical head it diffs against); the integrator keeps the ipc knowledge,
    // stamping the live docVersion on the compare request and feeding the parsed `diff.result` back in.
    review = new ReviewController({
      surfaces: [editor, formatted],
      setPressed: (on) => compareBtn?.setAttribute("aria-pressed", String(on)),
      requestCompare: (base) => ipc.send(Kinds.diffRequest, { base }, { version: docVersion }),
      docVersion: () => docVersion,
      onEmptyState: (showing) => {
        if (reviewEmptyEl) {
          reviewEmptyEl.hidden = !showing;
        }
      },
      // A compare that overflowed the native node-pair guard: nothing is washed (see review.ts), so
      // surface a distinct notice from the "no changes" one — there ARE changes, just too many to diff.
      onOverflow: (showing) => {
        if (reviewOverflowEl) {
          reviewOverflowEl.hidden = !showing;
        }
      },
    });

    // The formatting toolbar, now that both editors exist. It routes a command to the source editor
    // (Markdown transform) or the formatted editor (ProseMirror command) by mode + last-focused pane,
    // owns the buttons' listeners, and reflects the formatted pane's active formats on aria-pressed.
    formatToolbar = new FormatToolbar({
      buttons: formatButtons,
      applyInSource: (command) => editor.applyFormat(command),
      applyInFormatted: (command) => formatted.format(command),
      activeInSource: () => editor.activeFormats(),
      activeInFormatted: () => formatted.activeFormats(),
      disabledInFormatted: () => formatted.disabledFormats(),
      mode: () => mode,
    });

    // Re-measure CodeMirror on window resize and re-pad to the formatted heights (both panes reflow at a
    // new width); reconcileHeights also invalidates the coordinator's maps, which the next scroll rebuilds.
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
          const payload = parseImageInserted(reply.payload);
          if (payload?.markdown) {
            editor.insertAtMarker(image.markerId, payload.markdown);
          } else {
            editor.discardMarker(image.markerId);
          }
        } catch (error) {
          log.error("Image paste request failed", String(error));
          editor.discardMarker(image.markerId);
        }
      })();
    });

    // The host does not send this on every edit any more (see the `preview` comment above) — kept so a
    // future on-demand render still has somewhere to land without any webview-side changes.
    ipc.on(Kinds.previewHtml, (message) => {
      const payload = parsePreview(message.payload);
      if (payload) {
        preview.apply(payload.html, message.version ?? 0);
      }
    });

    // The compare result: the changed blocks of the working copy vs the last saved version. Parsing stays
    // here (ipc/decoder knowledge belongs to the integrator); the controller version-gates a stale one
    // (the author edited past the snapshot the request was taken at — same gate as the preview) and
    // otherwise washes the changes in BOTH editors (pane visibility is CSS, so Split shows both at once).
    // A malformed payload (decodes to null) is dropped, same as every other handler here — NOT treated as
    // an empty entries list, which would otherwise wash nothing and surface a false "no changes" notice.
    ipc.on(Kinds.diffResult, (message) => {
      const payload = parseDiffResult(message.payload);
      if (payload) {
        review.applyResult(message.version ?? 0, payload.entries, payload.overflow);
      }
    });

    ipc.on(Kinds.docLoaded, (message) => {
      const payload = parseDocLoaded(message.payload);
      if (payload) {
        // Drop any review overlay BEFORE re-hydrating: the marks belong to the old document, and the
        // setText calls below would otherwise re-apply them (clamped) against the new one for a frame.
        review.clear();
        // Silent: the host already has this text (it just sent it) — a non-silent setText would fire
        // the source editor's onChange after its 120ms debounce, round-tripping it back to the host as
        // a spurious editor.changed (bumping docVersion and triggering a redundant re-render) even
        // though nothing was actually edited. sameDocument is passed false explicitly (it otherwise
        // defaults to silent, i.e. true) — unlike the Split mirror / mode-switch hydration silent calls,
        // this IS a genuinely different document, so any pending image-insert marker from the previous
        // one is dropped, not restored.
        editor.setText(payload.text, true, false);
        // Resolve relative image links in the formatted view against the document's folder. Set before
        // setText so the image node views render with the correct app://repo/… src.
        formatted.setDocDir(payload.docDir);
        // Hydrate the formatted view now too — the source editor's setText above is silent, so it never
        // fires onChange/mirrors into this pane on its own; without this explicit call the Split pane
        // would stay blank (or show the previous document) after a load. formatted.setText is itself
        // silent by construction (ProseMirror updateState, not a dispatched transaction), so this sends
        // nothing either.
        formatted.setText(payload.text);
        // Reset BOTH panes' scroll to the document's start: setText above only replaces content, it does
        // NOT reset scrollTop, so a pane keeps whatever position the PREVIOUS document left it at — an
        // arbitrary depth for a shorter old doc, or the browser's clamp for a longer one, and the two
        // panes generally disagree. The coordinator parks both at the top AND records the writes, so the
        // programmatic reset can't drive a cross-pane sync. The new document also invalidates its maps.
        splitSync.reset();
        splitSync.invalidate();
        // Seed the synced highlight at the top of the document (both panes). No reveal: a freshly loaded
        // doc is scrolled to the top, so line 0 is already visible — and this is not a user navigation.
        setActive(0, null);
        setHover(null);
        // Align the source editor's line heights to the freshly rendered formatted blocks.
        reconcileHeights();
        // Read-only until the author clicks Edit (which forks a working branch). A freshly loaded
        // document is Published: the chrome offers Edit and hides the draft-only actions.
        editing = false;
        underReview = false; // a freshly loaded doc has no open review — stop polling/focus-refreshing.
        syncReviewPolling();
        lifecycleChrome.setLifecycle("published");
        // The path is not a lifecycle state — show it plainly (clears the dot's state colour).
        showPlainStatus(payload.path);
        dialogs.closeAll();
      }
    });

    // "Show changes" toggles the review overlay. Entering asks the host to diff against the last saved
    // version (version-stamped so a stale reply is dropped); the marks arrive via `diff.result` and are
    // applied in the handler above. Exiting clears the overlay in both editors.
    compareBtn?.addEventListener("click", () => review.toggle());
  }

  // Wiring group 2 — the lifecycle chrome: the action buttons + the format bar (index.ts owns the wire
  // kinds and pane-editable coordination; the chrome owns the show/hide policy) and the status/error
  // stream that colours the lifecycle dot.
  function wireLifecycle(): void {
    // The lifecycle action buttons + the format bar. index.ts owns the wire kinds (so the actions are
    // passed as callbacks) and the pane-editable coordination; the chrome owns the show/hide policy.
    lifecycleChrome = new LifecycleChrome({
      openBtn,
      editBtn,
      saveVersionBtn,
      sendForReviewBtn,
      updateReviewBtn,
      discardBtn,
      saveBtn,
      formatBar,
      setPaneEditable: (editable) => {
        editor.setEditable(editable);
        formatted.setEditable(editable);
      },
      onOpen: () => ipc.send(Kinds.docOpen),
      onEdit: () => void dialogs.openBranchName(),
      onSaveVersion: () => void dialogs.openVersionNote(),
      // Open the send-for-review prompt so the author confirms/edits the outward-facing PR title/body; on
      // confirm it sends doc.sendForReview with that text (see onPrText).
      onSendForReview: () => void dialogs.openPrText(),
      // Push the newly-saved versions to the already-open review (the host gates on a connected account and
      // a GitHub remote, and surfaces any problem as a plain status message).
      onUpdateReview: () => ipc.send(Kinds.docUpdateReview),
      onDiscard: () => {
        review.clear();
        ipc.send(Kinds.docDiscard);
      },
      onSave: () => ipc.send(Kinds.docSave),
    });

    // Lifecycle status (Draft / Unsaved changes / Version saved / Published). The author never sees
    // git vocabulary; the Edit button gives way to "Save version" + Discard once a draft is in
    // progress. Committing is explicit — typing only autosaves to disk, it never commits.
    ipc.on(Kinds.status, (message) => {
      const payload = parseStatus(message.payload);
      if (!payload) {
        return;
      }
      if (statusEl) {
        statusEl.textContent = payload.label;
        // Colour the lifecycle dot for this state (styles.css §8: one token family per state).
        statusEl.dataset.state = payload.state;
      }
      // Editing is only possible once a working branch exists (draft state).
      editing = payload.state !== "published";
      const wasUnderReview = underReview;
      underReview = isReviewState(payload.state);
      syncReviewPolling();
      if (underReview && !wasUnderReview) {
        // Just entered review (e.g. Send for review) — read the live decision now rather than waiting up to a
        // full poll interval for the first refresh.
        ipc.send(Kinds.reviewRefresh);
      }
      lifecycleChrome.setLifecycle(payload.state);
      if (editing) {
        formatToolbar.refresh();
      }
      if (!editing) {
        // Leaving editing (e.g. Discard) — close the draft-only prompts (version note, send-for-review) so a
        // stale confirm can't fire against the now-published doc. NOT the "name this draft" prompt, which is
        // legitimately open in the published state before a draft exists.
        dialogs.closeVersionNote();
        dialogs.closePrText();
      }
    });

    ipc.on(Kinds.error, (message) => {
      const payload = parseError(message.payload);
      if (payload) {
        // An error message is not a lifecycle state — show it plainly (drops the dot's state colour).
        showPlainStatus(payload.message);
      }
    });
  }

  // Wiring group 3 — GitHub: the "Connect to GitHub" affordance + sign-in code bar, the "My reviews"
  // browse panel, and the two triggers that refresh the review status from GitHub (window focus + the
  // under-review poll set up in syncReviewPolling).
  function wireGitHub(): void {
    // While the document is under review, refresh the review status from GitHub whenever the window regains
    // focus — the author typically switches to GitHub to see/act on a review, then back. The host single-
    // flights and no-ops unless genuinely under review; the poll (focus-gated at tick time) covers the
    // window-stays-focused case.
    window.addEventListener("focus", () => {
      if (underReview) {
        ipc.send(Kinds.reviewRefresh); // just came back — check for a decision made while away
      }
    });

    // GitHub account affordance (PoC-5): the host drives the "Connect to GitHub" button + the sign-in code
    // bar via github.code (the one-time code to display) and github.account (the connection state).
    const signInController = new SignInController({
      accountBtn: githubBtn,
      bar: githubSigninBar,
      text: githubSigninText,
      userCode: githubUserCode,
      openBtn: githubOpenBtn,
      status: githubSigninStatus,
      cancelBtn: githubCancelBtn,
      signIn: () => ipc.send(Kinds.githubSignIn),
      cancelSignIn: () => ipc.send(Kinds.githubSignInCancel),
      signOut: () => ipc.send(Kinds.githubSignOut),
      openUrl: (url) => ipc.send(Kinds.linkOpen, { url }),
    });

    // "My reviews" browse panel (PoC-5): lists the user's open reviews and opens any by link, on GitHub.
    const reviewsPanel = new ReviewsPanel({
      panel: reviewsPanelEl,
      list: reviewsList,
      status: reviewsStatus,
      closeBtn: reviewsCloseBtn,
      urlInput: reviewsUrlInput,
      urlOpenBtn: reviewsUrlOpenBtn,
      requestReviews: () =>
        requestSuggestion(Kinds.prListRequest, parsePrList, {
          items: [],
          error: "Couldn't load your reviews. Check your connection and try again.",
        }),
      openUrl: (url) => ipc.send(Kinds.linkOpen, { url }),
    });
    reviewsBtn?.addEventListener("click", () => void reviewsPanel.open());
    ipc.on(Kinds.githubCode, (message) => {
      const payload = parseGitHubCode(message.payload);
      if (payload) {
        signInController.showCode(payload);
      }
    });
    ipc.on(Kinds.githubAccount, (message) => {
      const payload = parseGitHubAccount(message.payload);
      if (payload) {
        signInController.applyAccount(payload);
        // "Send for review" is a dead end without GitHub configured (no Connect affordance), so the chrome
        // hides it unless sign-in is available. Sign-in *state* (signed in or not) is the host's gate.
        lifecycleChrome.setGitHubAvailable(payload.available);
        // "My reviews" browses the account's reviews — only meaningful once connected.
        if (reviewsBtn) {
          reviewsBtn.hidden = !payload.signedIn;
        }
        if (!payload.signedIn) {
          reviewsPanel.close();
        }
      }
    });
  }

  // Wiring group 4 — the view-mode switch and the view/toolbar chrome around it: mode changes (Code /
  // Split / Formatted), the wrap toggle, the theme toggle, export-log, and the skip link.
  function wireViewMode(): void {
    // The view-switch radiogroup; assigned below once the mode buttons are gathered, referenced earlier
    // only inside applyMode, which runs after the control is wired (on a click / arrow key).
    let viewModeControl: SegmentedControl<ViewMode>;

    // Switch between code / split / formatted. Panes are only shown/hidden (CSS keyed off
    // #panes[data-mode]), never destroyed. A surface that becomes visible is hydrated from the current
    // text (silently), and the reading position is carried across in the source-line coordinate. Pane
    // visibility is the single policy in view-mode.ts (paneVisibility) — Split shows both; the visible
    // pane (the source editor when shown, else the formatted view) is the source of truth for the
    // reading position and the canonical text.
    function applyMode(next: ViewMode): void {
      if (next === mode) {
        return;
      }
      const prev = mode;
      const prevVis = paneVisibility(prev);
      const nextVis = paneVisibility(next);
      // The reading position comes from the pane that owns it: in Split (both visible) the ACTIVE pane —
      // whichever the author was last reading — not unconditionally the source editor; in a single-pane
      // mode the sole visible pane. The coordinator holds the active-pane state, so it resolves this.
      const line = splitSync.readingLine(prevVis.editor, prevVis.preview);
      const text = prevVis.editor ? editor.getText() : formatted.getText();

      if (nextVis.editor && !prevVis.editor) {
        editor.setText(text, true);
      }
      if (nextVis.preview && !prevVis.preview) {
        formatted.setText(text);
      }

      mode = next;
      if (panesEl) {
        panesEl.dataset.mode = next;
      }
      viewModeControl.setSelected(next);
      editor.setEditable(editing);
      formatted.setEditable(editing);
      // The format target depends on the mode (Code→source, Formatted→WYSIWYG), so refresh the buttons.
      formatToolbar.refresh();

      // The visible pane(s) changed width / were un-hidden. CodeMirror re-measures asynchronously after
      // refresh(), so wait two frames before reading pane geometry and restoring scroll (PoC-11 timing):
      // the first lets its measurement run, the second performs height reconciliation and restoration.
      // ProseMirror needs no refresh call because the browser reflows its existing DOM automatically;
      // only SpecDesk's cached formatted-block measurements must be discarded before they are read.
      editor.refresh();
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          if (nextVis.preview) {
            formatted.invalidateGeometry();
          }
          // Re-pad the editor to the formatted heights BEFORE restoring scroll, so the scroll target
          // accounts for the spacers. Outside Split there is nothing to align — drop the spacers so the
          // source has no meaningless gaps. Either way the geometry changed, so the coordinator's maps
          // are stale — invalidate them (the next couple rebuilds from the new layout).
          if (isSplit(next)) {
            heightSync.reconcile();
          } else {
            heightSync.clear();
          }
          splitSync.invalidate();
          // Restore the reading position on each newly-visible pane through the coordinator (self-contained
          // per-pane scroll-to-line, so it works while the sibling is hidden; the write is recorded so it
          // can't echo into a false sync). The line is kept FRACTIONAL (T-065): both panes' scroll-to-line
          // interpolate the sub-line part across the block, so a mode switch lands the reading position
          // exactly where it was instead of snapping to the nearest whole line.
          const restorePanes: Pane[] = [];
          if (nextVis.editor) {
            restorePanes.push("editor");
          }
          if (nextVis.preview) {
            restorePanes.push("formatted");
          }
          splitSync.restore(line, restorePanes);
        });
      });
    }

    let wrap = true;
    wrapBtn?.addEventListener("click", () => {
      wrap = !wrap;
      editor.setLineWrapping(wrap);
      wrapBtn.textContent = `Wrap: ${wrap ? "on" : "off"}`;
      wrapBtn.setAttribute("aria-pressed", String(wrap));
    });

    exportLogBtn?.addEventListener("click", () => {
      ipc.send(Kinds.logExport);
    });

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

    // The view switch is a radiogroup (design §7/§11): clicks and arrow keys select a mode, and applyMode
    // reflects the selection back via setSelected. Only buttons present in the DOM join the group.
    const modeOptions: SegmentedOption<ViewMode>[] = [];
    if (modeCodeBtn) {
      modeOptions.push({ el: modeCodeBtn, value: "code" });
    }
    if (modeSplitBtn) {
      modeOptions.push({ el: modeSplitBtn, value: "split" });
    }
    if (modeFormattedBtn) {
      modeOptions.push({ el: modeFormattedBtn, value: "formatted" });
    }
    viewModeControl = new SegmentedControl(modeOptions, applyMode);
    // Reflect the DOM-derived starting `mode` into the radiogroup through the exact same path a
    // user-driven switch uses (setSelected) — the aria-checked/tabindex the buttons carry in the
    // markup are inert placeholders, not a second source of truth to keep in sync by hand.
    viewModeControl.setSelected(mode);

    // Skip link (a11y): jump keyboard focus past the toolbar into the editing surface visible in the
    // current mode — the source editor in Code/Split, the formatted editor in Formatted.
    skipLink?.addEventListener("click", (event) => {
      event.preventDefault();
      if (paneVisibility(mode).editor) {
        editor.focus();
      } else {
        formatted.focus();
      }
    });
  }

  wireEditors();
  wireLifecycle();
  wireGitHub();
  wireViewMode();

  ipc.start();
  postReady();
  log.info("Webview ready");
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", wire);
} else {
  wire();
}
