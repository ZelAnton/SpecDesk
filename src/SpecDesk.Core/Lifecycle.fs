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

/// An author action (or a reviewer event) that may advance the lifecycle. `SaveVersion` is the
/// explicit commit ("Save a version") — the only thing that creates a commit; plain autosave to
/// disk is a side effect the host performs and is not modelled here.
type Command =
    | Edit
    | SaveVersion
    | SendForReview
    | RequestChanges
    | Approve
    | Publish
    | Discard

/// The legal transitions. Anything not listed is rejected with a plain-language reason.
let next (state: State) (command: Command) : Result<State, string> =
    match state, command with
    | Published, Edit -> Ok Draft
    | Draft, SaveVersion -> Ok Draft
    | Draft, SendForReview -> Ok InReview
    | Draft, Discard -> Ok Published
    | InReview, SaveVersion -> Ok InReview
    | InReview, RequestChanges -> Ok ChangesRequested
    | InReview, Approve -> Ok Approved
    | ChangesRequested, SaveVersion -> Ok InReview
    | ChangesRequested, Approve -> Ok Approved
    | Approved, SaveVersion -> Ok InReview
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
    | "requestChanges" -> Some RequestChanges
    | "approve" -> Some Approve
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
