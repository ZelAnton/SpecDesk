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
    Assert.That(
        WorkflowConfig.branchNameForHost null "billing" date,
        Is.EqualTo "spec/billing-20260614")

[<Test>]
let ``commitMessageForHost expands the configured template`` () =
    let toml = "[commit]\ntemplate = \"Edit {docSlug} on {date:yyyy-MM-dd}\"\n"

    Assert.That(
        WorkflowConfig.commitMessageForHost toml "billing" date,
        Is.EqualTo "Edit billing on 2026-06-14")

[<Test>]
let ``an unexpandable template token falls back to the default`` () =
    // `{summary}` is reserved for the AI agent (PoC-8); it must not leak a literal brace into a
    // commit message before then.
    let toml = "[commit]\ntemplate = \"{summary}\"\n"
    Assert.That(WorkflowConfig.commitMessageForHost toml "billing" date, Is.EqualTo "Update billing")
