/// A minimal hand-rolled reader for the small, known tables of `.spectool.toml`
/// (docs/design/10-repo-config.md). We deliberately avoid a full TOML dependency: every section we
/// read is a flat table of string / bool / int / string-array values, and an invalid file degrades
/// to defaults rather than breaking the app. Shared by the `[images]`, `[repo]`, `[branch]`, and
/// `[commit]` readers.
module SpecDesk.Core.Toml

open System
open System.Collections.Generic
open System.Text.RegularExpressions

/// Drop a `#` comment that is not inside a quoted string.
let private stripInlineComment (value: string) : string =
    let mutable inQuote = false
    let mutable cut = -1
    let mutable i = 0

    while i < value.Length && cut < 0 do
        match value.[i] with
        | '"' -> inQuote <- not inQuote
        | '#' when not inQuote -> cut <- i
        | _ -> ()

        i <- i + 1

    if cut >= 0 then value.Substring(0, cut) else value

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
                // An array that opens without closing on this line continues on the following lines.
                if value.StartsWith("[") && not (value.Contains "]") then
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
            // A continuation line: strip its comment BEFORE looking for the closing ']', so a ']' in a
            // trailing comment can't close the array early.
            let stripped = stripInlineComment line
            pending.Append(' ').Append(stripped) |> ignore

            if stripped.Contains "]" then
                closeOpenArray ()

    table

let private unquote (value: string) : string =
    if value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\"") then
        value.Substring(1, value.Length - 2)
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

let getList (table: Dictionary<string, string>) (key: string) (fallback: string list) : string list =
    match table.TryGetValue key with
    | true, value ->
        let items = [ for m in Regex.Matches(value, "\"([^\"]*)\"") -> m.Groups.[1].Value ]
        if List.isEmpty items then fallback else items
    | _ -> fallback
