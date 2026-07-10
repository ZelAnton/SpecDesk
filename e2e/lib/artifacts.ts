import { writeFileSync } from "node:fs";
import type { Page, TestInfo } from "@playwright/test";
import { collectGeometry } from "./geometry";

/**
 * On a failed scenario, write the evidence bundle the agent reads to diagnose it:
 * `error.txt` (which assertion failed, with the measured-vs-expected numbers), `failure.png` (what the
 * user would see), `geometry.json` (the numeric layout the eye can't measure), `console.log` (page
 * console + uncaught errors), and — when the B1 diagnostic hook is present — `trace-ring.json` (the
 * webview's causal trace of WHY). Everything lands in the per-test artifact directory. Each write is
 * guarded so dumping evidence can never itself fail the run.
 */
export async function dumpArtifacts(
  page: Page,
  testInfo: TestInfo,
  consoleLines: string[],
): Promise<void> {
  // The failing assertion itself (delta vs epsilon, which anchor) — the fastest signal, otherwise
  // only in the reporter stdout, not the per-test dir the agent reads.
  const error = testInfo.error;
  if (error) {
    writeArtifact(
      testInfo.outputPath("error.txt"),
      `${error.message ?? ""}\n\n${error.stack ?? ""}`,
    );
  }

  await page
    .screenshot({ path: testInfo.outputPath("failure.png"), fullPage: true })
    .catch(() => {
      // The page/context may already be closed by the time teardown runs; nothing to capture then.
    });

  writeArtifact(testInfo.outputPath("console.log"), consoleLines.join("\n"));

  const geometry = await collectGeometry(page).catch(() => null);
  if (geometry) {
    writeArtifact(testInfo.outputPath("geometry.json"), JSON.stringify(geometry, null, 2));
  }

  const trace = await page
    .evaluate(() => {
      const hook = (window as unknown as { __specdeskTrace?: { snapshot: () => unknown } })
        .__specdeskTrace;
      return hook ? hook.snapshot() : null;
    })
    .catch(() => null);
  if (trace) {
    writeArtifact(testInfo.outputPath("trace-ring.json"), JSON.stringify(trace, null, 2));
  }
}

function writeArtifact(path: string, body: string): void {
  try {
    writeFileSync(path, body);
  } catch {
    // A failed artifact write must not mask the test failure that triggered the dump; skip it.
  }
}
