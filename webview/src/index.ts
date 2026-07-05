/**
 * SpecDesk webview entrypoint (PoC-2). Wires the CodeMirror editor, the rendered preview, and
 * scroll-sync to the native host: debounced edits go out as `editor.changed`; `preview.html` and
 * `doc.loaded` events come back. All Markdown rendering is native — this stays thin.
 */

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
} from "./decoders.js";
import { Dialogs } from "./dialogs.js";
import { MarkdownEditor } from "./editor.js";
import { FormatToolbar } from "./format-toolbar.js";
import { FormattedEditor } from "./formatted.js";
import { HeightSync } from "./height-sync.js";
import { attachImageCapture } from "./image-capture.js";
import { ipc, postReady } from "./ipc.js";
import { LifecycleChrome } from "./lifecycle-chrome.js";
import { log } from "./log.js";
import { Preview } from "./preview.js";
import { isReviewState, Kinds } from "./protocol.js";
import { rafThrottle } from "./raf.js";
import { ReviewController } from "./review.js";
import { ReviewsPanel } from "./reviews-panel.js";
import { ScrollSync } from "./scroll-sync.js";
import { SegmentedControl, type SegmentedOption } from "./segmented-control.js";
import { SignInController } from "./signin.js";
import { isSplit, paneVisibility, type ViewMode } from "./view-mode.js";

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
  const formatBar = document.querySelector<HTMLElement>("#format-bar");
  const formatButtons = Array.from(
    document.querySelectorAll<HTMLButtonElement>("#format-bar button[data-format]"),
  );
  if (!editorEl || !previewEl || !formattedEl) {
    return;
  }

  // The native Markdig render. No longer a visible pane (Split now pairs the source editor with the
  // editable WYSIWYG); kept updated, hidden, as the canonical render for the future diff/comments.
  const preview = new Preview(previewEl);
  // A web link clicked in the preview opens in the OS browser (the host re-validates the scheme).
  preview.setOnOpenLink((url) => ipc.send(Kinds.linkOpen, { url }));
  // Whether the document is currently editable (a draft is in progress). Drives the read-only
  // "start typing → offer to begin a draft" behaviour in both editors.
  let editing = false;
  // Whether the document is under review (In review / Changes requested / Approved). While it is, a
  // window-focus refreshes the review status from GitHub — a reviewer may have acted out of band.
  let underReview = false;
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

  // Block-level scroll-sync between the two editable panes in Split (they couple by source line, with
  // no shared native render to height-equalise against). A driver lock keeps the actively-scrolled pane
  // authoritative while the other's programmatic echo is ignored; see scroll-sync.ts.
  const scrollSync = new ScrollSync();

  // Forward-declared so the cross-sync callbacks below can reference both editors; assigned just after.
  let editor: MarkdownEditor;
  let formatted: FormattedEditor;

  // The review/compare overlay (PoC-6): "Show changes" diffs the working copy against the last saved
  // version and washes the changed lines/blocks in BOTH editors (see review.ts). It is a snapshot taken
  // at the docVersion of the click, so any genuine edit invalidates it — review.clear() drops the
  // overlay; the author clicks Show changes again to recompute. A silent Split text-mirror keeps it (the
  // editors re-apply their stored marks in setText), so only genuine edits clear it. Constructed once
  // both editors exist (below); referenced earlier only inside callbacks that fire after that.
  let review: ReviewController;

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

  // Cross-pane highlight sync: a single active source line (the caret line) and a single hovered
  // source line, shown in BOTH panes — the source editor highlights the line, the formatted view
  // highlights the block containing it. Whichever pane the user interacts with reports its position
  // (rAF-throttled), and both panes are updated together, so the highlights stay in step in Split.
  // `reveal` is the pane the user just navigated the caret in (null = a text edit or a programmatic
  // set — highlight only, no reveal). After a deliberate caret move in Split, bring the synced
  // highlight into view on the OTHER (passive) pane: accumulated block-height drift can place it
  // outside that pane's viewport, where it would be invisible. Reveal is minimal (nearest) and only
  // touches the passive pane — never the one the user is reading. scrollSync.drive keeps the active pane
  // authoritative so the passive reveal scroll doesn't echo back, while the active pane's own
  // scroll-sync keeps working (scrollSync.suppress would have muted that too).
  const setActive = (line: number | null, reveal: "editor" | "formatted" | null): void => {
    editor.setActiveLine(line);
    formatted.setActiveLine(line);
    // Skip the reveal while scroll-sync is actively positioning the passive pane (a scroll drove it
    // within the last window): the two would otherwise fight over its scrollTop and judder. A discrete
    // caret move with no recent scroll (a click / single arrow) still reveals normally.
    if (reveal !== null && line !== null && isSplit(mode) && !scrollSync.syncedRecently()) {
      scrollSync.drive(reveal);
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

  // Formatting toolbar (PoC-12): routes a format command to the right pane by mode + last focus and
  // reflects the formatted pane's active formats on the buttons (see format-toolbar.ts). Constructed
  // once both editors exist (below); referenced earlier only inside callbacks that fire after that.
  let formatToolbar: FormatToolbar;

  // The view-switch radiogroup; assigned below once the mode buttons are gathered, referenced earlier
  // only inside applyMode, which runs after the control is wired (on a click / arrow key).
  let viewModeControl: SegmentedControl<ViewMode>;

  // Height-sync: pad the source editor with spacers so each source block's top lines up with its
  // rendered block in the formatted pane (the formatted view is the fixed reference, never padded).
  // Only meaningful in Split; rAF-throttled so a burst of edits/resizes reconciles once per frame.
  // scrollSync.suppress() guards against the spacer change nudging the editor's scroll into a false sync.
  let heightSync: HeightSync;
  const reconcileHeights = rafThrottle(() => {
    if (isSplit(mode)) {
      scrollSync.suppress();
      heightSync.reconcile();
    }
  });

  // Live content sync. An edit in one editor goes to the native pipeline (sendDoc) AND is mirrored
  // into the other — guarded by content equality so the mirror never echoes back, with a silent
  // setText so the mirror can't re-fire as an edit. After mirroring, the other pane's scroll is
  // re-aligned so it doesn't jump.
  const onEditorChange = (text: string): void => {
    // A genuine edit invalidates the compare snapshot — drop the overlay (the mirror below is silent
    // and re-applies nothing once cleared).
    review.clear();
    sendDoc(text);
    if (formatted.getText() !== text) {
      formatted.setText(text);
      if (isSplit(mode)) {
        scrollSync.suppress();
        formatted.scrollToSourceLine(editor.topVisibleLineExact());
      }
    }
    reconcileHeights();
  };
  const onFormattedChange = (text: string): void => {
    review.clear();
    sendDoc(text);
    if (editor.getText() !== text) {
      editor.setText(text, true);
      if (isSplit(mode)) {
        scrollSync.suppress();
        editor.scrollToSourceLine(formatted.topVisibleSourceLine());
      }
    }
    reconcileHeights();
  };

  const offerDraft = (): void => {
    if (!editing) {
      void dialogs.openBranchName();
    }
  };

  editor = new MarkdownEditor(editorEl, {
    onChange: onEditorChange,
    onScroll: () => {
      if (isSplit(mode) && scrollSync.claim("editor")) {
        formatted.scrollToSourceLine(editor.topVisibleLineExact());
        scrollSync.markSynced();
      }
    },
    onScrollSettle: () => {},
    onCursor: (line, navigated) => setActive(line, navigated ? "editor" : null),
    onHover: (line) => setHover(line),
    onGeometryChange: () => reconcileHeights(),
    onEditAttempt: offerDraft,
    onFocus: () => formatToolbar.setFocused("editor"),
    // A web link Ctrl/Cmd-clicked in the source opens in the OS browser (the host re-validates it).
    onOpenLink: (url) => ipc.send(Kinds.linkOpen, { url }),
  });

  // The formatted (WYSIWYG) editor — a sibling view of the same Markdown. Edits serialize back via
  // block-splice and go out through the SAME `editor.changed` channel as source edits.
  formatted = new FormattedEditor(formattedEl, {
    onChange: onFormattedChange,
    onEditAttempt: offerDraft,
    onScroll: () => {
      if (isSplit(mode) && scrollSync.claim("formatted")) {
        editor.scrollToSourceLine(formatted.topVisibleSourceLine());
        scrollSync.markSynced();
      }
    },
    onCursor: (line, navigated) => setActive(line, navigated ? "formatted" : null),
    onHover: (line) => setHover(line),
    onContentResize: () => reconcileHeights(),
    onFocus: () => formatToolbar.setFocused("formatted"),
    onActiveChange: () => formatToolbar.refresh(),
    // A web link clicked in the WYSIWYG view opens in the OS browser (the host re-validates the scheme).
    onOpenLink: (url) => ipc.send(Kinds.linkOpen, { url }),
  });

  // The source editor is padded to match the formatted view's block heights (formatted is the fixed
  // reference). Assigned now that both panes exist; reconcileHeights() drives it.
  heightSync = new HeightSync(editor, formatted);

  // The review/compare overlay state machine, now that both surfaces exist. It washes changes in both
  // editors (the first is the canonical head it diffs against); the integrator keeps the ipc knowledge,
  // stamping the live docVersion on the compare request and feeding the parsed `diff.result` back in.
  review = new ReviewController({
    surfaces: [editor, formatted],
    setPressed: (on) => compareBtn?.setAttribute("aria-pressed", String(on)),
    requestCompare: () => ipc.send(Kinds.diffRequest, undefined, { version: docVersion }),
    docVersion: () => docVersion,
    onEmptyState: (showing) => {
      if (reviewEmptyEl) {
        reviewEmptyEl.hidden = !showing;
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
    activeFormats: () => formatted.activeFormats(),
    mode: () => mode,
  });

  // The lifecycle action buttons + the format bar. index.ts owns the wire kinds (so the actions are
  // passed as callbacks) and the pane-editable coordination; the chrome owns the show/hide policy.
  const lifecycleChrome = new LifecycleChrome({
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
    const line = prevVis.editor ? editor.topVisibleLineExact() : formatted.topVisibleSourceLine();
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

    // The visible pane(s) changed width / were un-hidden, so re-measure before restoring scroll.
    // CodeMirror's re-measure is asynchronous, so restore on the SECOND frame (the PoC-11 timing).
    scrollSync.suppress();
    editor.refresh();
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        scrollSync.suppress();
        if (nextVis.preview) {
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
        if (nextVis.preview) {
          formatted.scrollToSourceLine(Math.floor(line));
        }
        if (nextVis.editor) {
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
  ipc.on(Kinds.diffResult, (message) => {
    const payload = parseDiffResult(message.payload);
    review.applyResult(message.version ?? 0, payload?.entries ?? []);
  });

  ipc.on(Kinds.docLoaded, (message) => {
    const payload = parseDocLoaded(message.payload);
    if (payload) {
      // Drop any review overlay BEFORE re-hydrating: the marks belong to the old document, and the
      // setText calls below would otherwise re-apply them (clamped) against the new one for a frame.
      review.clear();
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
    signIn: () => ipc.send(Kinds.githubSignIn),
    cancelSignIn: () => ipc.send(Kinds.githubSignInCancel),
    signOut: () => ipc.send(Kinds.githubSignOut),
    openUrl: (url) => ipc.send(Kinds.linkOpen, { url }),
  });

  // "My reviews" browse panel (PoC-5): lists the user's open reviews and opens any by link, on GitHub.
  const reviewsPanel = new ReviewsPanel({
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

  // "Show changes" toggles the review overlay. Entering asks the host to diff against the last saved
  // version (version-stamped so a stale reply is dropped); the marks arrive via `diff.result` and are
  // applied in the handler above. Exiting clears the overlay in both editors.
  compareBtn?.addEventListener("click", () => review.toggle());

  ipc.start();
  postReady();
  log.info("Webview ready");
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", wire);
} else {
  wire();
}
