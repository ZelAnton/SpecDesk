/// The git-workflow sections of `.spectool.toml` — `[repo]`, `[branch]`, `[commit]`
/// (docs/design/10-repo-config.md). Maintainer-owned policy used by the local git layer (PoC-4):
/// the base branch to fork from, the working-branch naming pattern, and the template that seeds the
/// editable "Save a version" note (the commit message). Every field is optional with a sensible
/// default, and an invalid file degrades to defaults rather than breaking the app.
module SpecDesk.Core.WorkflowConfig

open System

type WorkflowConfig =
    {
        /// The published branch a working branch forks from (e.g. "main").
        DefaultBase: string
        /// Working-branch name pattern; expanded with the document tokens (e.g. "spec/{docSlug}-{date:yyyyMMdd}").
        BranchPattern: string
        /// Version-note (commit message) template; expanded with the document tokens. Seeds the
        /// editable note shown when the author saves a version.
        CommitTemplate: string
        /// `[review] reviewers` — the @users/@teams to request on a pull request, or the literal
        /// "codeowners" to defer to the repo's CODEOWNERS (GitHub's own auto-request). Empty by default.
        Reviewers: string list
    }

let defaults: WorkflowConfig =
    { DefaultBase = "main"
      BranchPattern = "spec/{docSlug}-{date:yyyyMMdd}"
      CommitTemplate = "Update {docSlug}"
      Reviewers = [] }

/// Parse `[repo] default-base`, `[branch] pattern`, `[commit] template`, and `[review] reviewers`. Any
/// error returns the full defaults (design 10: invalid config must never break the app).
let parse (tomlText: string option) : WorkflowConfig =
    match tomlText with
    | None -> defaults
    | Some text ->
        try
            let repo = Toml.readTable "repo" text
            let branch = Toml.readTable "branch" text
            let commit = Toml.readTable "commit" text
            let review = Toml.readTable "review" text

            { DefaultBase = Toml.getString repo "default-base" defaults.DefaultBase
              BranchPattern = Toml.getString branch "pattern" defaults.BranchPattern
              CommitTemplate = Toml.getString commit "template" defaults.CommitTemplate
              Reviewers = Toml.getList review "reviewers" defaults.Reviewers }
        with _ ->
            // Malformed config is the maintainer's problem to fix; naming degrades to the built-in
            // defaults rather than breaking the workflow (design 10: invalid config must never break).
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

/// Whether a string is a syntactically valid git ref name (a conservative subset of
/// git-check-ref-format — enough to catch what a bad `{date:FMT}` can actually produce, not a full
/// reimplementation): no whitespace/control characters or `~^:?*[\`, no `..` run or `@{`, and no
/// leading/trailing/doubled `/`. `{date:}` (an empty format) expands via `DateTimeOffset.ToString("")`
/// to the general format (e.g. "07/04/2026 09:30:00 +00:00") — spaces and colons a bare "still has a
/// brace?" check does not catch, but this does.
let private isValidGitRef (value: string) : bool =
    if value.Length = 0 then
        false
    elif value.StartsWith "/" || value.EndsWith "/" || value.Contains "//" then
        false
    elif value.Contains ".." || value.EndsWith "." || value.Contains "@{" then
        false
    else
        value
        |> Seq.forall (fun c -> not (Char.IsWhiteSpace c) && not (Char.IsControl c) && "~^:?*[\\".IndexOf(c) < 0)

/// No constraint — used for the commit-message template, which (unlike a branch name) is free text
/// and has no git-ref character restrictions.
let private anyText (_: string) : bool = true

/// Expand `pattern`, treating it as unusable (falling through to try the fallback, see
/// `expandOrDefault`) when: expansion throws (e.g. `{date:q}` — "q" is not a recognized .NET date
/// format specifier, so `DateTimeOffset.ToString` raises `FormatException`; a crafted `.spectool.toml`
/// pattern must never crash the caller — see `ImageEngine.insertForHost` for the matching guard on the
/// image side), the result still contains an unexpanded `{token}` (e.g. the `{summary}` placeholder
/// reserved for the AI agent), the result is blank, or `isValid` rejects it (the branch-name case).
let private tryExpand (isValid: string -> bool) (ctx: Tokens.TokenContext) (pattern: string) : string option =
    try
        let expanded = Tokens.expand ctx pattern

        if expanded.Contains "{" || expanded.Trim().Length = 0 || not (isValid expanded) then
            None
        else
            Some expanded
    with _ ->
        None

/// Expand `pattern`, falling back to expanding `fallback` if that is unusable per `tryExpand`, and to
/// the raw (unexpanded) `fallback` string as a last resort if even that fails validation or throws —
/// invalid config must never break the workflow (design 10), for the pattern OR the built-in default.
let private expandOrDefault
    (isValid: string -> bool)
    (pattern: string)
    (fallback: string)
    (ctx: Tokens.TokenContext)
    : string =
    match tryExpand isValid ctx pattern with
    | Some expanded -> expanded
    | None ->
        match tryExpand isValid ctx fallback with
        | Some expanded -> expanded
        | None -> fallback

/// C#-facing facade: the working-branch name for a document, from the repo config (TOML text or
/// null) and the document slug / current date.
let branchNameForHost (tomlText: string | null) (docSlug: string) (date: DateTimeOffset) : string =
    let config = parse (Option.ofObj tomlText)
    let ctx = docContext docSlug date
    expandOrDefault isValidGitRef config.BranchPattern defaults.BranchPattern ctx

/// C#-facing facade: the suggested version note (commit message) for a document — the editable
/// seed shown when the author saves a version.
let commitMessageForHost (tomlText: string | null) (docSlug: string) (date: DateTimeOffset) : string =
    let config = parse (Option.ofObj tomlText)
    let ctx = docContext docSlug date
    expandOrDefault anyText config.CommitTemplate defaults.CommitTemplate ctx

/// C#-facing facade: the published base branch a draft forks from. A present-but-blank
/// `default-base = ""` degrades to the built-in default rather than an empty ref (which would make
/// "Edit" throw and silently no-op) — invalid config must never break the workflow (design 10).
let defaultBaseForHost (tomlText: string | null) : string =
    let configured = (parse (Option.ofObj tomlText)).DefaultBase

    if String.IsNullOrWhiteSpace configured then
        defaults.DefaultBase
    else
        configured

/// Whether a reviewer entry is the "codeowners" sentinel (optionally written "@codeowners"), which
/// defers to the repo's CODEOWNERS via GitHub's own auto-request rather than an explicit ask.
let private isCodeowners (entry: string) : bool =
    entry.Trim().TrimStart('@').Equals("codeowners", StringComparison.OrdinalIgnoreCase)

/// C#-facing facade: the explicit @user/@team reviewers to request on a pull request, from
/// `[review] reviewers`. Only the "codeowners" sentinel is dropped here — those are left to GitHub's
/// own CODEOWNERS auto-request; handle normalization (trimming, stripping '@', dropping blanks, and
/// the user/team split) is the GitHub layer's job, so it isn't duplicated on this side. Empty when
/// there is nothing explicit to request.
let reviewersForHost (tomlText: string | null) : string[] =
    (parse (Option.ofObj tomlText)).Reviewers
    |> List.filter (fun entry -> not (isCodeowners entry))
    |> List.toArray
