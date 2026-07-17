/**
 * Whether authoring surfaces (the formatted/source editors, comment composers, and the version-note /
 * send-for-review prompts) ask the browser/OS spellchecker to check their text, plus the language hint
 * that goes with it. WebView2/Chromium carries a built-in spellchecker, but it only activates on an
 * element that explicitly opts in via `spellcheck`/`lang` — nothing in this app did, so authors never saw
 * red squiggles under typos anywhere they write prose.
 *
 * TODO(T-077): once persistent preferences land, move this toggle into that store (so it survives
 * restarts and the author can turn it off) instead of this local constant.
 */
export const SPELLCHECK_ENABLED = true;

/** BCP-47 language tag passed alongside `spellcheck` so the OS/Chromium spellchecker knows which
 *  dictionary to check against. SpecDesk's UI and specs are English-only today. */
export const SPELLCHECK_LANG = "en";
