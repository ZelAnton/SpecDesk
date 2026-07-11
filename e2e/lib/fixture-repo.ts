import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

/** The welcome doc the host auto-loads on first `ready` — a heading, paragraphs of different heights,
 *  and a list, so the formatted pane genuinely outgrows the source and height-sync applies real spacers
 *  (which the startup geometry probe checks). */
const WELCOME_DOC = [
  "# Welcome to SpecDesk",
  "",
  "This is a disposable fixture spec that the full-app E2E drives the real host against.",
  "",
  "A second, much longer paragraph so the formatted pane is meaningfully taller than the source here.",
  "",
  "- first item",
  "- second item",
  "- third item",
  "",
].join("\n");

const SPEC_A_DOC = ["# Spec A", "", "A second document in the fixture repo.", ""].join("\n");

// Mirrors src/SpecDesk.Host/samples/.spectool.toml — the branch pattern drives the host's suggested
// draft name, which a later Edit/Save-version scenario asserts.
const SPECTOOL_TOML = [
  "[repo]",
  'default-base = "main"',
  "",
  "[branch]",
  'pattern = "spec/{docSlug}-{date:yyyyMMdd}"',
  "",
  "[images]",
  'folder = "images/{docSlug}"',
  "",
].join("\n");

export interface FixtureRepo {
  /** `<runDir>/data-root` — passed to the host as SPECDESK_DATA_ROOT. */
  dataRoot: string;
  /** `<dataRoot>/sample-repo` — the versioned repo the host auto-loads welcome.md from. */
  repo: string;
  welcome: string;
}

/**
 * Seed a disposable git repo under `<runDir>/data-root/sample-repo` with the plain `git` CLI. The host
 * points SPECDESK_DATA_ROOT here; because the repo is ALREADY versioned, `SampleRepo.EnsureSeeded`
 * short-circuits (it only seeds an unversioned dir), so this fixture — not the bundled sample — is what
 * the app resolves its lifecycle against and auto-loads on first `ready`.
 */
export function createFixtureRepo(runDir: string): FixtureRepo {
  const dataRoot = resolve(runDir, "data-root");
  const repo = resolve(dataRoot, "sample-repo");
  mkdirSync(repo, { recursive: true });

  const git = (...args: string[]): void => {
    execFileSync("git", args, { cwd: repo, stdio: "pipe" });
  };
  // `-b main` aligns HEAD with the .spectool.toml default-base, independent of the machine's git default.
  git("init", "-b", "main");
  git("config", "user.name", "SpecDesk E2E");
  git("config", "user.email", "e2e@specdesk.test");

  writeFileSync(resolve(repo, "welcome.md"), WELCOME_DOC);
  writeFileSync(resolve(repo, "spec-a.md"), SPEC_A_DOC);
  writeFileSync(resolve(repo, ".spectool.toml"), SPECTOOL_TOML);

  git("add", "-A");
  // Neutralise machine-global git config so the fixture commit is deterministic and never blocks: a
  // global commit.gpgsign=true would prompt for a gpg passphrase (hang), and a global hooksPath could
  // run a pre-commit hook that fails.
  git("-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "Seed fixture spec repo");

  return { dataRoot, repo, welcome: resolve(repo, "welcome.md") };
}
