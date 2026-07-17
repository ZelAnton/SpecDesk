module SpecDesk.Core.Tests.WorkflowConfigTests

open System
open NUnit.Framework
open SpecDesk.Core

let private date = DateTimeOffset(2026, 6, 14, 9, 30, 0, TimeSpan.Zero)

[<Test>]
let ``defaults apply when there is no config`` () =
    let config = WorkflowConfig.parse None
    Assert.That(config.DefaultBase, Is.EqualTo "main")
    Assert.That(config.BranchPattern, Is.EqualTo "spec/{docSlug}-{date:yyyyMMdd}")
    Assert.That(config.CommitTemplate, Is.EqualTo "Update {docSlug}")

[<Test>]
let ``the repo, branch, and commit tables are parsed`` () =
    let toml =
        "[repo]\ndefault-base = \"trunk\"\n\n[branch]\npattern = \"draft/{docSlug}\"\n\n[commit]\ntemplate = \"Edit {docSlug}\"\n"

    let config = WorkflowConfig.parse (Some toml)
    Assert.That(config.DefaultBase, Is.EqualTo "trunk")
    Assert.That(config.BranchPattern, Is.EqualTo "draft/{docSlug}")
    Assert.That(config.CommitTemplate, Is.EqualTo "Edit {docSlug}")

[<Test>]
let ``branchNameForHost expands the slug and date`` () =
    Assert.That(WorkflowConfig.branchNameForHost null "billing" date, Is.EqualTo "spec/billing-20260614")

[<Test>]
let ``commitMessageForHost expands the configured template`` () =
    let toml = "[commit]\ntemplate = \"Edit {docSlug} on {date:yyyy-MM-dd}\"\n"

    Assert.That(WorkflowConfig.commitMessageForHost toml "billing" date, Is.EqualTo "Edit billing on 2026-06-14")

[<Test>]
let ``an unexpandable template token falls back to the default`` () =
    // `{summary}` is reserved for the AI agent (PoC-8); it must not leak a literal brace into a
    // commit message before then.
    let toml = "[commit]\ntemplate = \"{summary}\"\n"
    Assert.That(WorkflowConfig.commitMessageForHost toml "billing" date, Is.EqualTo "Update billing")

[<Test>]
let ``reviewersForHost returns the explicit user and team entries`` () =
    let toml = "[review]\nreviewers = [\"@alice\", \"@octo/reviewers\"]\n"

    Assert.That(WorkflowConfig.reviewersForHost toml |> Array.toList, Is.EqualTo(box [ "@alice"; "@octo/reviewers" ]))

[<Test>]
let ``reviewersForHost drops the codeowners sentinel but keeps explicit entries`` () =
    // "codeowners" is deferred to GitHub's own auto-request; explicit entries override it.
    let toml = "[review]\nreviewers = [\"codeowners\", \"@alice\"]\n"
    Assert.That(WorkflowConfig.reviewersForHost toml |> Array.toList, Is.EqualTo(box [ "@alice" ]))

[<Test>]
let ``defaultBaseForHost falls back to the default for a blank value`` () =
    // A present-but-empty (or whitespace) default-base must degrade to the default, not an empty ref that
    // makes Edit throw and silently no-op.
    Assert.That(WorkflowConfig.defaultBaseForHost "[repo]\ndefault-base = \"\"\n", Is.EqualTo "main")
    Assert.That(WorkflowConfig.defaultBaseForHost "[repo]\ndefault-base = \"   \"\n", Is.EqualTo "main")
    Assert.That(WorkflowConfig.defaultBaseForHost "[repo]\ndefault-base = \"trunk\"\n", Is.EqualTo "trunk")

[<Test>]
let ``reviewersForHost is empty for a codeowners-only or absent config`` () =
    Assert.That(WorkflowConfig.reviewersForHost null, Is.Empty)
    Assert.That(WorkflowConfig.reviewersForHost "[review]\nreviewers = [\"codeowners\"]\n", Is.Empty)

[<Test>]
let ``allowAuthorPublishForHost is off by default and honours an explicit true`` () =
    // Fail-closed: an absent config, an absent key, and an explicit false all deny; only an explicit
    // true permits an author to publish their own approved document.
    Assert.That(WorkflowConfig.allowAuthorPublishForHost null, Is.False)
    Assert.That(WorkflowConfig.allowAuthorPublishForHost "[review]\nreviewers = [\"@alice\"]\n", Is.False)
    Assert.That(WorkflowConfig.allowAuthorPublishForHost "[review]\nallow-author-publish = false\n", Is.False)
    Assert.That(WorkflowConfig.allowAuthorPublishForHost "[review]\nallow-author-publish = true\n", Is.True)

// S-09 regressions: a `{date:FMT}` with an unrecognized .NET format specifier used to throw
// FormatException straight out of expandOrDefault (only TOML *parsing* was guarded), and `{date:}`
// (empty format) used to expand via the general format ("07/04/2026 09:30:00 +00:00" — spaces and a
// colon) and pass through unchecked. Either one used to reach BeginEdit as a git-illegal ref, so
// "Edit" silently no-op'd (OnEdit's catch doesn't filter FormatException) and OnSuggestBranchName never
// replied (the webview's request timed out at 30s) — invalid config must never break the workflow.

[<Test>]
let ``an unrecognized date format specifier falls back to the default instead of throwing`` () =
    // "q" is not a recognized .NET standard or custom date/time format specifier.
    let toml = "[branch]\npattern = \"spec/{docSlug}-{date:q}\"\n"
    Assert.That(WorkflowConfig.branchNameForHost toml "billing" date, Is.EqualTo "spec/billing-20260614")

[<Test>]
let ``an empty date format falls back to the default instead of producing a space/colon-laden ref`` () =
    let toml = "[branch]\npattern = \"spec/{docSlug}-{date:}\"\n"
    Assert.That(WorkflowConfig.branchNameForHost toml "billing" date, Is.EqualTo "spec/billing-20260614")

[<Test>]
let ``a branch pattern producing spaces or colons falls back to the default`` () =
    // Even without a bad {date:FMT}, a maintainer-authored pattern can smuggle in ref-illegal
    // characters directly; the branch case must reject them the same way.
    let toml = "[branch]\npattern = \"spec: {docSlug}\"\n"
    Assert.That(WorkflowConfig.branchNameForHost toml "billing" date, Is.EqualTo "spec/billing-20260614")

[<Test>]
let ``the commit-message template is not subject to the git-ref character check`` () =
    // A commit message legitimately contains spaces/colons — only the branch-name path validates refs.
    let toml = "[commit]\ntemplate = \"Update: {docSlug} notes\"\n"
    Assert.That(WorkflowConfig.commitMessageForHost toml "billing" date, Is.EqualTo "Update: billing notes")
