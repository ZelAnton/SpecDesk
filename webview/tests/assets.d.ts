/**
 * Vite/vitest raw-text imports (`import html from "./x.html?raw"`). Used by the button↔registry sync
 * test (format-registry.test.ts) to read the REAL index.html markup and assert it matches the registry,
 * rather than a hand-copied fixture that could itself drift.
 */
declare module "*.html?raw" {
  const content: string;
  export default content;
}
