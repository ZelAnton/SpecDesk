/// The `[images]` section of `.spectool.toml` (docs/design/10-repo-config.md). Maintainer-owned
/// policy; every field optional with a sensible default, and an invalid file falls back to
/// defaults rather than breaking the app.
///
/// We parse only the small, known `[images]` table (string / bool / int / string-array values),
/// so a minimal hand-rolled reader avoids pulling in a full TOML dependency. Later PoCs that need
/// other sections can adopt a vetted parser then.
module SpecDesk.Core.ImagesConfig

open System
open System.Collections.Generic
open System.Text.RegularExpressions

type ImagesConfig =
    { Folder: string
      Naming: string
      Allowed: string list
      Preferred: string
      Case: Slug.Case
      MaxNameLength: int
      StripMetadata: bool
      MaxWidth: int
      ReencodePaste: bool }

let defaults: ImagesConfig =
    { Folder = "images/{docSlug}"
      Naming = "{docSlug}-{date:yyyyMMdd}-{seq}-{hash8}"
      Allowed = [ "png"; "jpg"; "jpeg"; "gif"; "webp"; "svg" ]
      Preferred = "png"
      Case = Slug.Kebab
      MaxNameLength = 80
      StripMetadata = true
      MaxWidth = 2000
      ReencodePaste = true }

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

/// Collect the `key = value` pairs (raw, trimmed) from the `[images]` table only.
let private readImagesTable (text: string) : Dictionary<string, string> =
    let table = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    let mutable inImages = false

    for rawLine in text.Replace("\r\n", "\n").Split('\n') do
        let line = rawLine.Trim()

        if line.Length = 0 || line.StartsWith("#") then
            ()
        elif line.StartsWith("[") && line.EndsWith("]") then
            inImages <- line.Trim('[', ']').Trim().Equals("images", StringComparison.OrdinalIgnoreCase)
        elif inImages then
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

let private getString (table: Dictionary<string, string>) (key: string) (fallback: string) : string =
    match table.TryGetValue key with
    | true, value -> unquote value
    | _ -> fallback

let private getBool (table: Dictionary<string, string>) (key: string) (fallback: bool) : bool =
    match table.TryGetValue key with
    | true, value ->
        match value.Trim().ToLowerInvariant() with
        | "true" -> true
        | "false" -> false
        | _ -> fallback
    | _ -> fallback

let private getInt (table: Dictionary<string, string>) (key: string) (fallback: int) : int =
    match table.TryGetValue key with
    | true, value ->
        match Int32.TryParse value with
        | true, n -> n
        | _ -> fallback
    | _ -> fallback

let private getList (table: Dictionary<string, string>) (key: string) (fallback: string list) : string list =
    match table.TryGetValue key with
    | true, value ->
        let items = [ for m in Regex.Matches(value, "\"([^\"]*)\"") -> m.Groups.[1].Value ]
        if List.isEmpty items then fallback else items
    | _ -> fallback

/// Parse the `[images]` table, taking each field from the file or the default. Any error returns
/// the full defaults (design 10: invalid config must never break the app).
let parse (tomlText: string option) : ImagesConfig =
    match tomlText with
    | None -> defaults
    | Some text ->
        try
            let table = readImagesTable text

            { Folder = getString table "folder" defaults.Folder
              Naming = getString table "naming" defaults.Naming
              Allowed = getList table "allowed" defaults.Allowed
              Preferred = getString table "preferred" defaults.Preferred
              Case = Slug.parseCase (getString table "case" "kebab")
              MaxNameLength = getInt table "max-name-length" defaults.MaxNameLength
              StripMetadata = getBool table "strip-metadata" defaults.StripMetadata
              MaxWidth = getInt table "max-width" defaults.MaxWidth
              ReencodePaste = getBool table "reencode-paste" defaults.ReencodePaste }
        with _ ->
            // Malformed config is the maintainer's problem to fix; the app degrades to defaults
            // rather than failing to open the repo (design 10).
            defaults
