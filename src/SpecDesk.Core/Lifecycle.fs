/// The document lifecycle state machine (docs/design/04-git-workflow.md). Pure and total: it knows
/// only the legal transitions between author-facing states, never touching git or I/O. The host
/// performs the side effects (branch, commit, push) when a transition is allowed.
///
/// PoC-4 exercises only the local states — Published ⇄ Draft (Edit / explicit Save a version /
/// Discard). Committing is the author's deliberate `SaveVersion`, never an autosave: autosave only
/// writes the working copy to disk and is not a lifecycle transition. The review states (InReview /
/// ChangesRequested / Approved) are defined and tested here now so the machine is complete; they
/// are wired to GitHub in PoC-5.
module SpecDesk.Core.Lifecycle

/// What the author sees, mapped onto git/GitHub reality by the host.
type State =
    | Published
    | Draft
    | InReview
    | ChangesRequested
    | Approved

/// An author action that may advance the lifecycle. `SaveVersion` is the explicit commit ("Save a
/// version") — the only thing that creates a commit; plain autosave to disk is a side effect the host
/// performs and is not modelled here. `UpdateReview` pushes the newly-saved versions to the open pull
/// request; it re-opens review from `Approved` (new versions need re-approval) but is a self-transition
/// from `InReview` / `ChangesRequested` (a change request stands until the reviewer re-reviews). The
/// reviewer's verdict (approve / request changes) is a GitHub event, not an author action: the host
/// reflects it onto the review state directly from a status refresh, so it is not a `Command` here.
type Command =
    | Edit
    | SaveVersion
    | SendForReview
    | UpdateReview
    | Publish
    | Discard

/// The legal transitions. Anything not listed is rejected with a plain-language reason.
let next (state: State) (command: Command) : Result<State, string> =
    match state, command with
    | Published, Edit -> Ok Draft
    | Draft, SaveVersion -> Ok Draft
    | Draft, SendForReview -> Ok InReview
    | Draft, Discard -> Ok Published
    // SaveVersion under review is a purely local commit — never a status change. UpdateReview (the push that
    // shares saved versions to the PR) is where the two verdicts diverge, matching how GitHub ages a review:
    //   • From In review / Changes requested it is a self-transition — a change request is a block that
    //     stands until the reviewer re-reviews; pushing fixes doesn't clear it (GitHub keeps it too).
    //   • From Approved it returns to In review — the approval was of the versions that were reviewed, so new
    //     versions need re-approval. Done locally (not left to the refresh) so an author can't publish unseen
    //     content if the follow-up GitHub read happens to fail; the refresh agrees (the stale approval no
    //     longer targets the new head commit).
    // The reviewer's verdict moves between InReview / ChangesRequested / Approved, but that is driven by
    // GitHub (the host writes the state on a status refresh), not by a Command — so those transitions are
    // deliberately absent here.
    | InReview, SaveVersion -> Ok InReview
    | InReview, UpdateReview -> Ok InReview
    | ChangesRequested, SaveVersion -> Ok ChangesRequested
    | ChangesRequested, UpdateReview -> Ok ChangesRequested
    | Approved, SaveVersion -> Ok Approved
    | Approved, UpdateReview -> Ok InReview
    | Approved, Publish -> Ok Published
    | _ -> Error(sprintf "Cannot %A while %A" command state)

/// Stable wire name for a state (matches the TypeScript `StatusState` union).
let stateName (state: State) : string =
    match state with
    | Published -> "published"
    | Draft -> "draft"
    | InReview -> "inReview"
    | ChangesRequested -> "changesRequested"
    | Approved -> "approved"

/// The author-facing label shown in the status area for a (settled) state.
let label (state: State) : string =
    match state with
    | Published -> "Published"
    | Draft -> "Draft — only you can see this"
    | InReview -> "In review"
    | ChangesRequested -> "Changes requested"
    | Approved -> "Approved"

let private parseState (name: string) : State option =
    match name with
    | "published" -> Some Published
    | "draft" -> Some Draft
    | "inReview" -> Some InReview
    | "changesRequested" -> Some ChangesRequested
    | "approved" -> Some Approved
    | _ -> None

let private parseCommand (name: string) : Command option =
    match name with
    | "edit" -> Some Edit
    | "saveVersion" -> Some SaveVersion
    | "sendForReview" -> Some SendForReview
    | "updateReview" -> Some UpdateReview
    | "publish" -> Some Publish
    | "discard" -> Some Discard
    | _ -> None

/// C#-facing facade: given the current state name and a command name, return the next state's
/// wire name, or the empty string if the transition (or either name) is invalid. Strings keep the
/// interop trivial (no FSharpResult / FSharpOption leaking into the host).
let tryStep (currentState: string) (commandName: string) : string =
    match parseState currentState, parseCommand commandName with
    | Some state, Some command ->
        match next state command with
        | Ok target -> stateName target
        | Error _ -> ""
    | _ -> ""

/// C#-facing facade: the author-facing label for a state wire name (empty if unknown).
let labelOf (currentState: string) : string =
    match parseState currentState with
    | Some state -> label state
    | None -> ""
