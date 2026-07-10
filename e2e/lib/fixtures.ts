import { test as base, expect } from "@playwright/test";
import { dumpArtifacts } from "./artifacts";
import { installMockHost } from "./mock-host";
import { serveBundle } from "./serve-bundle";

interface HarnessFixtures {
  /** Page console + uncaught-error lines captured for this test, dumped on failure. */
  consoleLog: string[];
}

/**
 * The base test every geometry scenario builds on. An AUTO fixture wires the harness for every test:
 * it serves the bundle and installs the mock host on the context, captures console/pageerror BEFORE
 * the body's `goto`, and — on a failing test — dumps the evidence bundle (see {@link dumpArtifacts}).
 * Scenarios never repeat this plumbing.
 */
export const test = base.extend<HarnessFixtures>({
  consoleLog: [
    async ({ page, context }, use, testInfo) => {
      const lines: string[] = [];
      page.on("console", (message) => lines.push(`[${message.type()}] ${message.text()}`));
      page.on("pageerror", (error) => lines.push(`[pageerror] ${error.message}\n${error.stack ?? ""}`));

      await serveBundle(context);
      await installMockHost(context);

      await use(lines);

      if (testInfo.status !== testInfo.expectedStatus) {
        await dumpArtifacts(page, testInfo, lines);
      } else {
        // Green-run evidence: the agent must always have pixels to Read (the verification-ladder rule
        // is "run the scenario, then Read the screenshot"), not only when something failed.
        await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true }).catch(() => {
          // Page/context already closed in teardown — no screenshot to take; not worth failing over.
        });
      }
    },
    { auto: true },
  ],
});

export { expect };
