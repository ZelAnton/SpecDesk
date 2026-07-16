/// A minimal hand-rolled reader for the small, known tables of `.spectool.toml`
/// (docs/design/10-repo-config.md). We deliberately avoid a full TOML dependency: every section we
/// read is a flat table of string / bool / int / string-array values, and an invalid file degrades
/// to defaults rather than breaking the app. Shared by the `[images]`, `[repo]`, `[branch]`, and
/// `[commit]` readers.
module SpecDesk.Core.Toml

open System
open System.Collections.Generic

/// Drop a `#` comment that is not inside a quoted string. Quote tracking is escape-aware: a `\"` inside
/// a quoted value (e.g. `template = "Say \"hi\" #1"`) does not toggle the tracker, so the `#` right
/// after it still counts as inside the string rather than ending it early — the naive "every `"`
/// toggles" version treated an escaped quote as a real close, so a `#` (or a `]`, for
/// {@link containsUnquoted}) sitting between the true open and close was wrongly read as bare.
let private stripInlineComment (value: string) : string =
    let mutable inQuote = false
    let mutable escaped = false
    let mutable cut = -1
    let mutable i = 0

    while i < value.Length && cut < 0 do
        let c = value.[i]

        if inQuote && escaped then
            escaped <- false
        elif inQuote && c = '\\' then
            escaped <- true
        elif c = '"' then
            inQuote <- not inQuote
        elif c = '#' && not inQuote then
            cut <- i

        i <- i + 1

    if cut >= 0 then value.Substring(0, cut) else value

/// Whether `ch` appears in `value` outside any double-quoted string (same escape-aware tracking as
/// {@link stripInlineComment}) — used to find an array's real closing ']' without tripping on a ']'
/// inside a quoted entry, e.g. a glob character class like "docs/[a-z].md", or one that itself contains
/// an escaped quote.
let private containsUnquoted (ch: char) (value: string) : bool =
    let mutable inQuote = false
    let mutable escaped = false
    let mutable found = false
    let mutable i = 0

    while i < value.Length && not found do
        let c = value.[i]

        if inQuote && escaped then
            escaped <- false
        elif inQuote && c = '\\' then
            escaped <- true
        elif c = '"' then
            inQuote <- not inQuote
        elif c = ch && not inQuote then
            found <- true

        i <- i + 1

    found

/// Collect the `key = value` pairs (raw, trimmed) from a single named table. An array value that opens
/// with `[` but does not close on its line (the common multi-line TOML array form) is accumulated across
/// the following lines until the closing `]`, so a reviewer / glob list written one entry per line parses
/// the same as its single-line equivalent rather than silently yielding nothing. A malformed (never
/// closed) array degrades only its own key: a following section header or `key = …` line ends it, so
/// unrelated later keys are still read.
let readTable (tableName: string) (text: string) : Dictionary<string, string> =
    let table = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    let mutable inTable = false
    // Non-empty while a `key = [` array in the target table is still open; `pending` gathers its text.
    let mutable openArrayKey = ""
    let pending = System.Text.StringBuilder()

    // A section header or a `bareKey = …` assignment — either means a still-open array was never closed
    // (malformed); we stop swallowing lines into it so the keys that follow aren't lost to defaults.
    let looksLikeNewEntry (line: string) : bool =
        if line.StartsWith("[") && line.EndsWith("]") then
            true
        else
            let eq = line.IndexOf('=')

            eq > 0
            && line.Substring(0, eq).Trim()
               |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.')

    let closeOpenArray () =
        if openArrayKey.Length > 0 then
            table.[openArrayKey] <- pending.ToString().Trim()
            openArrayKey <- ""

    let processLine (line: string) =
        if line.Length = 0 || line.StartsWith("#") then
            ()
        elif line.StartsWith("[") && line.EndsWith("]") then
            inTable <- line.Trim('[', ']').Trim().Equals(tableName, StringComparison.OrdinalIgnoreCase)
        elif inTable then
            let eq = line.IndexOf('=')

            if eq > 0 then
                let key = line.Substring(0, eq).Trim()
                let value = (stripInlineComment (line.Substring(eq + 1))).Trim()
                // An array that opens without closing on this line continues on the following lines. The
                // close test is quote-aware so a ']' inside a quoted entry (a glob class) doesn't count.
                if value.StartsWith("[") && not (containsUnquoted ']' value) then
                    openArrayKey <- key
                    pending.Clear().Append(value) |> ignore
                else
                    table.[key] <- value

    for rawLine in text.Replace("\r\n", "\n").Split('\n') do
        let line = rawLine.Trim()

        if openArrayKey.Length = 0 then
            processLine line
        elif looksLikeNewEntry line then
            // The array never closed — store what we have (best-effort) and process this line normally.
            closeOpenArray ()
            processLine line
        else
            // A continuation line: strip its comment BEFORE looking for the closing ']' (so a ']' in a
            // trailing comment can't close early), and match only an unquoted ']' (so a ']' inside a quoted
            // glob entry doesn't either).
            let stripped = stripInlineComment line
            pending.Append(' ').Append(stripped) |> ignore

            if containsUnquoted ']' stripped then
                closeOpenArray ()

    // Flush an array still open at end of input (a malformed, never-closed list) so it degrades to its own
    // best-effort partial value — consistent with how a mid-file unclosed array is handled — rather than
    // vanishing entirely.
    closeOpenArray ()

    table

/// Un-escape the common TOML basic-string escapes (`\"`, `\\`, `\n`, `\t`, `\r`) so a quoted value like
/// `"Say \"hi\""` round-trips to the literal text `Say "hi"` instead of keeping its backslashes. Any
/// other backslash sequence is left verbatim (backslash + the following character) rather than raising —
/// malformed/unsupported escapes degrade gracefully, consistent with the rest of this reader.
let private unescape (value: string) : string =
    let sb = Text.StringBuilder(value.Length)
    let mutable i = 0

    while i < value.Length do
        if value.[i] = '\\' && i + 1 < value.Length then
            match value.[i + 1] with
            | '"' ->
                sb.Append('"') |> ignore
                i <- i + 2
            | '\\' ->
                sb.Append('\\') |> ignore
                i <- i + 2
            | 'n' ->
                sb.Append('\n') |> ignore
                i <- i + 2
            | 't' ->
                sb.Append('\t') |> ignore
                i <- i + 2
            | 'r' ->
                sb.Append('\r') |> ignore
                i <- i + 2
            | _ ->
                sb.Append(value.[i]) |> ignore
                i <- i + 1
        else
            sb.Append(value.[i]) |> ignore
            i <- i + 1

    sb.ToString()

let private unquote (value: string) : string =
    if value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\"") then
        unescape (value.Substring(1, value.Length - 2))
    else
        value

let getString (table: Dictionary<string, string>) (key: string) (fallback: string) : string =
    match table.TryGetValue key with
    | true, value -> unquote value
    | _ -> fallback

let getBool (table: Dictionary<string, string>) (key: string) (fallback: bool) : bool =
    match table.TryGetValue key with
    | true, value ->
        match value.Trim().ToLowerInvariant() with
        | "true" -> true
        | "false" -> false
        | _ -> fallback
    | _ -> fallback

let getInt (table: Dictionary<string, string>) (key: string) (fallback: int) : int =
    match table.TryGetValue key with
    | true, value ->
        match Int32.TryParse value with
        | true, n -> n
        | _ -> fallback
    | _ -> fallback

/// Split a `[ "a", "\"b\"" ]` array's raw text into its quoted elements' raw (still-escaped) contents,
/// using the same escape-aware `inQuote`/`escaped` tracking as {@link stripInlineComment} /
/// {@link containsUnquoted}: a `\"` inside an entry does not end it, so `["Say \"hi\""]` yields the
/// single raw element `Say \"hi\"` rather than being cut into pieces at the escaped quote (the naive
/// `"([^"]*)"` regex this replaces knew nothing about `\"`). Callers still owe each element a pass
/// through `unescape` — the same one `getString`/`unquote` use — to get the literal value.
let private splitQuotedElements (value: string) : string list =
    let items = ResizeArray<string>()
    let current = Text.StringBuilder()
    let mutable inQuote = false
    let mutable escaped = false

    for c in value do
        if inQuote then
            if escaped then
                current.Append(c) |> ignore
                escaped <- false
            elif c = '\\' then
                current.Append(c) |> ignore
                escaped <- true
            elif c = '"' then
                items.Add(current.ToString())
                current.Clear() |> ignore
                inQuote <- false
            else
                current.Append(c) |> ignore
        elif c = '"' then
            inQuote <- true

    List.ofSeq items

let getList (table: Dictionary<string, string>) (key: string) (fallback: string list) : string list =
    match table.TryGetValue key with
    | true, value ->
        let items = splitQuotedElements value |> List.map unescape
        if List.isEmpty items then fallback else items
    | _ -> fallback
