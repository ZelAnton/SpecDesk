module SpecDesk.Core.Tests.PropertyTests

open System
open NUnit.Framework
open FSharp.Reflection
open FsCheck
open FsCheck.FSharp
open SpecDesk.Core
open SpecDesk.Core.Lifecycle

// Property-based tests (FsCheck) for the pure F# domain. Where the hand-written example tests pin known
// cases, these assert invariants over generated input — cheaply catching the edge cases manual examples
// miss. Each property is driven with Check.QuickThrowOnFailure so a falsifying case throws and NUnit
// reports it (no FsCheck.NUnit adapter needed). Generators are explicit (Prop.forAll + Arb.fromGen) so no
// global Arbitrary registration leaks across tests, and so the interesting characters (quotes, escapes,
// separators, accents) are actually exercised rather than left to a default string generator's luck.

let private pair (a: Gen<'a>) (b: Gen<'b>) : Gen<'a * 'b> = Gen.map2 (fun x y -> (x, y)) a b

let private stringOf (chars: char list) : Gen<string> =
    Gen.elements chars |> Gen.listOf |> Gen.map (List.toArray >> String)

// ------------------------------------------------------------------------------------------------
// Stage 1 smoke: prove the FsCheck generator pipeline and runner work inside this NUnit F# project.
// ------------------------------------------------------------------------------------------------

[<Test>]
let ``FsCheck smoke: integer addition is commutative`` () =
    Check.QuickThrowOnFailure(fun (a: int) (b: int) -> a + b = b + a)

// ------------------------------------------------------------------------------------------------
// Stage 2a: Toml quote/unquote/escape round-trips.
// ------------------------------------------------------------------------------------------------

// The characters that make the reader's escape-aware paths interesting: the five escape-significant ones
// (quote, backslash, newline, tab, CR) plus the tokens that drive comment/array/table parsing (hash,
// brackets, equals, comma) mixed with ordinary letters, digits, spaces and a few Unicode letters.
let private tomlChars: char list =
    [ 'a'
      'B'
      'z'
      '0'
      '9'
      ' '
      '-'
      '_'
      '.'
      '/'
      '!'
      '"'
      '\\'
      '\n'
      '\t'
      '\r'
      '#'
      '['
      ']'
      '='
      ','
      'é'
      'Ä'
      'ß'
      'Ω' ]

let private tomlString: Gen<string> = stringOf tomlChars

/// The inverse of Toml's private `unescape`: emit the five TOML basic-string escapes so an arbitrary value
/// survives being wrapped in double quotes and read back verbatim by `getString` / `getList`.
let private escapeBasic (s: string) : string =
    let sb = Text.StringBuilder(s.Length)

    for ch in s do
        match ch with
        | '\\' -> sb.Append "\\\\" |> ignore
        | '"' -> sb.Append "\\\"" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | c -> sb.Append c |> ignore

    sb.ToString()

[<Test>]
let ``a quoted string round-trips through escape -> readTable -> getString`` () =
    // For any value, escaping it, wrapping it in quotes, and reading it back yields the original — the
    // escape/unescape and quote-stripping paths are inverses, including for embedded quotes, backslashes,
    // hashes (must stay inside the string, not start a comment) and newlines (escaped, so the value stays
    // on one line).
    let prop (s: string) =
        let text = sprintf "[t]\nk = \"%s\"\n" (escapeBasic s)
        Toml.getString (Toml.readTable "t" text) "k" "missing-sentinel" = s

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen tomlString) prop)

[<Test>]
let ``a quoted string array round-trips through escape -> readTable -> getList`` () =
    // Each element is escaped, quoted, and joined into a single-line array; getList must recover exactly the
    // original list (splitQuotedElements is escape-aware, and each element is un-escaped). Non-empty by
    // construction so getList never falls back.
    let nonEmpty = Gen.map2 (fun h t -> h :: t) tomlString (Gen.listOf tomlString)

    let prop (items: string list) =
        let body =
            items |> List.map (fun s -> "\"" + escapeBasic s + "\"") |> String.concat ", "

        let text = sprintf "[t]\nk = [%s]\n" body
        Toml.getList (Toml.readTable "t" text) "k" [ "missing-sentinel" ] = items

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen nonEmpty) prop)

// ------------------------------------------------------------------------------------------------
// Stage 2b: Slug idempotence and invariants.
// ------------------------------------------------------------------------------------------------

// A well-behaved character set for slugs: ASCII letters/digits, common separators/punctuation, and a few
// Latin/Greek letters whose only decomposition is a non-spacing mark (stripped cleanly by removeDiacritics)
// — so the idempotence claim is about the slug logic, not .NET's Unicode normalisation of exotic scripts.
let private slugChars: char list =
    [ 'a' .. 'z' ]
    @ [ 'A' .. 'Z' ]
    @ [ '0' .. '9' ]
    @ [ ' '; '-'; '_'; '.'; '/'; '!'; '?'; ','; ':'; '#'; '('; ')'; '\t'; '\n' ]
    @ [ 'é'; 'à'; 'ü'; 'ö'; 'ä'; 'ñ'; 'ç'; 'ß'; 'É'; 'Ä'; 'Ω' ]

let private slugString: Gen<string> = stringOf slugChars

let private slugCases = [ Slug.Kebab; Slug.Snake; Slug.Lower ]
let private caseGen: Gen<Slug.Case> = Gen.elements slugCases

let private separatorOf (case: Slug.Case) : string =
    match case with
    | Slug.Kebab -> "-"
    | Slug.Snake -> "_"
    | Slug.Lower -> ""

[<Test>]
let ``slugify is idempotent`` () =
    // Slugifying an already-slugified string changes nothing: the output is a fixed point of slugify.
    let prop (case: Slug.Case, s: string) =
        let once = Slug.slugify case s
        Slug.slugify case once = once

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen (pair caseGen slugString)) prop)

[<Test>]
let ``a slug is lowercase, un-padded, and free of doubled separators`` () =
    // Structural invariants of every slug: no leading/trailing separator, no run of two separators, and
    // every character is either a lowercase alphanumeric or the case's single separator.
    let prop (case: Slug.Case, s: string) =
        let sep = separatorOf case
        let slug = Slug.slugify case s

        let noLead = sep = "" || not (slug.StartsWith sep)
        let noTrail = sep = "" || not (slug.EndsWith sep)
        let noDouble = sep = "" || not (slug.Contains(sep + sep))

        let onlyAllowed =
            slug
            |> Seq.forall (fun c ->
                (Char.IsLetterOrDigit c && Char.ToLowerInvariant c = c)
                || (sep <> "" && string c = sep))

        noLead && noTrail && noDouble && onlyAllowed

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen (pair caseGen slugString)) prop)

[<Test>]
let ``truncatePreservingSuffix stays within the limit and keeps the suffix`` () =
    // Given a name that ends with the suffix, and a limit larger than the suffix, the result never exceeds
    // the limit and still ends with the suffix (uniqueness survives truncation); a name already within the
    // limit is returned unchanged.
    let gen =
        Gen.map2
            (fun (head: string) (suffix: string) -> (head, suffix))
            (stringOf [ 'a' .. 'z' ])
            (stringOf [ '0' .. '9' ])
        |> Gen.map (fun (head, suffix) -> (head + suffix, suffix))
        |> fun g -> Gen.map2 (fun (name, suffix) maxLen -> (maxLen, suffix, name)) g (Gen.choose (1, 40))

    let prop (maxLen: int, suffix: string, name: string) =
        let result = Slug.truncatePreservingSuffix maxLen suffix name

        let withinLimit = result.Length <= maxLen
        let keepsSuffix = suffix.Length >= maxLen || result.EndsWith suffix
        let noopWhenShort = name.Length > maxLen || result = name

        withinLimit && keepsSuffix && noopWhenShort

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen gen) prop)

// ------------------------------------------------------------------------------------------------
// Stage 3: Lifecycle transition totality.
// ------------------------------------------------------------------------------------------------

let private allStates = [ Published; Draft; InReview; ChangesRequested; Approved ]

let private allCommands =
    [ Edit; SaveVersion; SendForReview; UpdateReview; Publish; Discard ]

let private stateGen: Gen<State> = Gen.elements allStates
let private commandGen: Gen<Command> = Gen.elements allCommands

/// The wire command names, mirroring Lifecycle's private `parseCommand`, so the C#-facing `tryStep` facade
/// can be exercised against the same (state, command) pairs the core `next` sees.
let private commandWire (command: Command) : string =
    match command with
    | Edit -> "edit"
    | SaveVersion -> "saveVersion"
    | SendForReview -> "sendForReview"
    | UpdateReview -> "updateReview"
    | Publish -> "publish"
    | Discard -> "discard"

[<Test>]
let ``the generators enumerate every State and Command case`` () =
    // Guard the hand-written generator lists against a case being added to either union: if State or Command
    // grows, this fails until `allStates` / `allCommands` (and so the totality property below) covers it.
    Assert.That(List.length allStates, Is.EqualTo(FSharpType.GetUnionCases(typeof<State>).Length))
    Assert.That(List.length allCommands, Is.EqualTo(FSharpType.GetUnionCases(typeof<Command>).Length))

[<Test>]
let ``every state x command transition is total, and the facade agrees with the core`` () =
    // Totality: `next` returns a well-formed Ok/Error for EVERY pair, never throwing. An Ok always names a
    // real state (its wire name round-trips through the facade); an Error names the offending state and the
    // facade collapses to its empty sentinel. This pins the C# `tryStep` facade to the core `next` across
    // the whole 5x6 transition space at once.
    let prop (state: State, command: Command) =
        match next state command with
        | Ok target -> tryStep (stateName state) (commandWire command) = stateName target
        | Error message ->
            message.Contains(sprintf "%A" state)
            && tryStep (stateName state) (commandWire command) = ""

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen (pair stateGen commandGen)) prop)

[<Test>]
let ``a state wire name round-trips through the facade's label`` () =
    // stateName is total over the union, and labelOf recovers a non-empty author-facing label for every
    // real wire name (only an unknown name yields "").
    let prop (state: State) =
        let name = stateName state
        name <> "" && labelOf name <> ""

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen stateGen) prop)
