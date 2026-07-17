# Release process

SpecDesk ships as a single self-contained Windows executable. Cutting a release is a
manual version bump followed by pushing a version tag; the
[`release`](../.github/workflows/release.yml) workflow then does the rest — it builds and
tests the tagged commit, publishes the `win-x64` single-file exe, and creates the GitHub
Release with notes and the exe attached.

Everything below the tag push is automated. Your job is the version cut and the tag.

## Versioning

- Versions follow [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`.
- The git tag is the version with a `v` prefix: version `0.2.0` ships as tag `v0.2.0`.
- A pre-release adds a suffix: `v0.2.0-rc1`. The workflow marks such tags as a GitHub
  pre-release automatically (it is not flagged "Latest").

## Cut a release

1. **Start from a green `main`.** Make sure CI and CodeQL are passing on the commit you
   intend to release. The release build runs the full `dotnet build` + `dotnet test`
   again on the tagged commit, so a red tree will simply fail the release without
   publishing anything.

2. **Pick the new version** `X.Y.Z` per semver, based on what changed since the last
   release.

3. **Update `CHANGELOG.md`.** Rename the `## [Unreleased]` heading to the new version
   with today's date, and open a fresh, empty `## [Unreleased]` above it:

   ```md
   ## [Unreleased]

   ## [X.Y.Z] - YYYY-MM-DD

   ### Added
   - ... (the entries that were under Unreleased)
   ```

   Also update the link references at the bottom of the file: point `[Unreleased]` at the
   `vX.Y.Z...HEAD` compare range and add an `[X.Y.Z]` entry.

   > The `## [X.Y.Z]` section is the source of truth for the release notes. If it is
   > missing or empty when the tag is pushed, the workflow falls back to generating notes
   > from the commit history since the previous tag using `git-cliff` (config:
   > [`cliff.toml`](../cliff.toml)). Prefer the curated CHANGELOG section.

4. **Bump the product version.** Set `<Version>` in
   [`Directory.Build.props`](../Directory.Build.props) to `X.Y.Z` — **without** the `v`
   prefix. This must match the tag: the workflow's integrity check refuses to release if
   the compiled product version and the tag disagree, so a mismatch fails the release
   instead of shipping a mislabelled exe.

5. **Commit** the CHANGELOG and version bump to `main`.

6. **Tag and push.** Create the annotated tag on that commit and push it:

   ```sh
   git tag -a vX.Y.Z -m "Release vX.Y.Z"
   git push origin vX.Y.Z
   ```

   Pushing a `v*` tag is what triggers the release workflow. (Pushing commits alone never
   publishes a release.)

## What the workflow does

The [`release`](../.github/workflows/release.yml) workflow, on a pushed `v*` tag, runs on
a Windows runner and, in order:

1. Validates the tag is a clean `vX.Y.Z[-prerelease]` version and derives the version.
2. Verifies `Directory.Build.props`'s `<Version>` equals the tag version.
3. Restores, builds, and tests the whole solution in `Release` (warnings-as-errors),
   with the real webview bundle (no `SkipWebview`).
4. Publishes the single-file self-contained `win-x64` exe (the same `win-x64.pubxml`
   profile [`release.cmd`](../release.cmd) uses) and verifies the exe exists and is a
   plausible self-contained size before going further.
5. Builds the release notes: the `## [X.Y.Z]` CHANGELOG section if present, otherwise the
   `git-cliff` fallback. It refuses to continue if the notes come out empty.
6. Creates the GitHub Release for the tag with those notes and the exe attached as
   `SpecDesk-vX.Y.Z-win-x64.exe`.

Every step gates the next, and the Release is created only in the final step — so a failed
or partial run stops before anything is published. If the workflow fails, fix the cause
and re-run; nothing is released until it completes green.

## Verify

After the workflow finishes, open the repository's Releases page and confirm the new
release shows the expected notes and the `SpecDesk-vX.Y.Z-win-x64.exe` asset. Download the
exe and smoke-test it on a clean Windows machine (it needs the Microsoft Edge WebView2
runtime, pre-installed on Windows 11 and most up-to-date Windows 10).
