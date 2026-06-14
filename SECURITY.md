# Security Policy

## Supported versions

Security fixes are applied to the latest released version of **SpecDesk**.
Older versions are not maintained — upgrade to the latest release to receive
fixes.

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Report privately through GitHub's
[private vulnerability reporting](https://github.com/ZelAnton/SpecDesk/security/advisories/new)
(repository **Security → Advisories → Report a vulnerability**). If that is
unavailable, contact the maintainer listed on the
[ZelAnton](https://github.com/ZelAnton) profile.

Please include:

- a description of the vulnerability and its impact;
- steps to reproduce (a minimal proof of concept is ideal);
- affected version(s).

You can expect an initial acknowledgement within a few days. Once a fix is
ready, a patched release is published and the advisory is disclosed.

## Automated scanning

This repository runs [GitHub CodeQL](.github/workflows/codeql.yml)
(`security-and-quality` suite) on every pull request, every push to `main`, and
weekly. Dependencies are audited against the NuGet advisory database on every
restore (`NuGetAudit`), and [Dependabot](.github/dependabot.yml) keeps actions
and packages current.
