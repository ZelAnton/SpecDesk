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

/// Collect the `key = value` pairs (raw, trimmed) from a single named table.
let readTable (tableName: string) (text: string) : Dictionary<string, string> =
    let table = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    let mutable inTable = false

    for rawLine in text.Replace("\r\n", "\n").Split('\n') do
        let line = rawLine.Trim()

        if line.Length = 0 || line.StartsWith("#") then
            ()
        elif line.StartsWith("[") && line.EndsWith("]") then
            inTable <- line.Trim('[', ']').Trim().Equals(tableName, StringComparison.OrdinalIgnoreCase)
        elif inTable then
            let eq = line.IndexOf('=')

            if eq > 0 then
                let key = line.Substring(0, eq).Trim()
                let value = (stripInlineComment (line.Substring(eq + 1))).Trim()
                table.[key] <- value

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
