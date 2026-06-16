/// The git-workflow sections of `.spectool.toml` — `[repo]`, `[branch]`, `[commit]`
/// (docs/design/10-repo-config.md). Maintainer-owned policy used by the local git layer (PoC-4):
/// the base branch to fork from, the working-branch naming pattern, and the template that seeds the
/// editable "Save a version" note (the commit message). Every field is optional with a sensible
/// default, and an invalid file degrades to defaults rather than breaking the app.
module SpecDesk.Core.WorkflowConfig

open System

type WorkflowConfig =
    { /// The published branch a working branch forks from (e.g. "main").
      DefaultBase: string
      /// Working-branch name pattern; expanded with the document tokens (e.g. "spec/{docSlug}-{date:yyyyMMdd}").
      BranchPattern: string
      /// Version-note (commit message) template; expanded with the document tokens. Seeds the
      /// editable note shown when the author saves a version.
      CommitTemplate: string }

let defaults: WorkflowConfig =
    { DefaultBase = "main"
      BranchPattern = "spec/{docSlug}-{date:yyyyMMdd}"
      CommitTemplate = "Update {docSlug}" }

/// Parse `[repo] default-base`, `[branch] pattern`, and `[commit] template`. Any error returns the
/// full defaults (design 10: invalid config must never break the app).
let parse (tomlText: string option) : WorkflowConfig =
    match tomlText with
    | None -> defaults
    | Some text ->
        try
            let repo = Toml.readTable "repo" text
            let branch = Toml.readTable "branch" text
            let commit = Toml.readTable "commit" text

            { DefaultBase = Toml.getString repo "default-base" defaults.DefaultBase
              BranchPattern = Toml.getString branch "pattern" defaults.BranchPattern
              CommitTemplate = Toml.getString commit "template" defaults.CommitTemplate }
        with _ ->
            defaults

/// Build the token context for a document. Seq/Hash8/OriginalName are not meaningful for branch
/// or commit naming, so they are supplied empty.
let private docContext (docSlug: string) (date: DateTimeOffset) : Tokens.TokenContext =
    { DocSlug = docSlug
      DocDir = ""
      Date = date
      Seq = 0
      Hash8 = ""
      OriginalName = None }

/// A token-expanded result still containing an unexpanded `{token}` means the pattern referenced
/// something we cannot supply here (e.g. the `{summary}` placeholder reserved for the AI agent).
/// Fall back to the default in that case so the author never gets a literal brace in a branch name
/// or commit message.
let private expandOrDefault (pattern: string) (fallback: string) (ctx: Tokens.TokenContext) : string =
    let expanded = Tokens.expand ctx pattern
    if expanded.Contains "{" || expanded.Trim().Length = 0 then
        Tokens.expand ctx fallback
    else
        expanded

/// C#-facing facade: the working-branch name for a document, from the repo config (TOML text or
/// null) and the document slug / current date.
let branchNameForHost (tomlText: string | null) (docSlug: string) (date: DateTimeOffset) : string =
    let config = parse (Option.ofObj tomlText)
    let ctx = docContext docSlug date
    expandOrDefault config.BranchPattern defaults.BranchPattern ctx

/// C#-facing facade: the suggested version note (commit message) for a document — the editable
/// seed shown when the author saves a version.
let commitMessageForHost (tomlText: string | null) (docSlug: string) (date: DateTimeOffset) : string =
    let config = parse (Option.ofObj tomlText)
    let ctx = docContext docSlug date
    expandOrDefault config.CommitTemplate defaults.CommitTemplate ctx

/// C#-facing facade: the published base branch a draft forks from.
let defaultBaseForHost (tomlText: string | null) : string =
    (parse (Option.ofObj tomlText)).DefaultBase
