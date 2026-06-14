# Changelog

All notable changes to **SpecDesk** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial multi-language scaffold: .NET solution (`SpecDesk.slnx`) with C# and F# projects under `src/`/`tests/`, plus a TypeScript `webview/` bundle.
- `SECURITY.md`, `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md`, and `.github/CODEOWNERS` community-health files.
- NuGet dependency auditing (`NuGetAudit`/`NuGetAuditMode=all`) in `Directory.Build.props`; vulnerability findings are warnings, not build-breaking errors.
- CI now caches NuGet packages, uploads test results (`.trx`) as artifacts, builds/tests the `webview/` TypeScript bundle, and supports manual `workflow_dispatch` runs.
- `.yamllint.yml` config (tuned for GitHub Actions) and a CI `yaml-lint` job that lints workflow YAML.

### Changed
- All GitHub Actions are pinned to a commit SHA (with a version comment) instead of a moving tag; Dependabot now groups action bumps into a single weekly PR.

### Fixed
-

[Unreleased]: https://github.com/ZelAnton/SpecDesk/commits/main
