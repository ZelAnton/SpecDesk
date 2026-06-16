/// The `[images]` section of `.spectool.toml` (docs/design/10-repo-config.md). Maintainer-owned
/// policy; every field optional with a sensible default, and an invalid file falls back to
/// defaults rather than breaking the app.
///
/// We parse only the small, known `[images]` table (string / bool / int / string-array values)
/// via the shared hand-rolled <see cref="SpecDesk.Core.Toml"/> reader, avoiding a full TOML
/// dependency.
module SpecDesk.Core.ImagesConfig

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

/// Parse the `[images]` table, taking each field from the file or the default. Any error returns
/// the full defaults (design 10: invalid config must never break the app).
let parse (tomlText: string option) : ImagesConfig =
    match tomlText with
    | None -> defaults
    | Some text ->
        try
            let table = Toml.readTable "images" text

            { Folder = Toml.getString table "folder" defaults.Folder
              Naming = Toml.getString table "naming" defaults.Naming
              Allowed = Toml.getList table "allowed" defaults.Allowed
              Preferred = Toml.getString table "preferred" defaults.Preferred
              Case = Slug.parseCase (Toml.getString table "case" "kebab")
              MaxNameLength = Toml.getInt table "max-name-length" defaults.MaxNameLength
              StripMetadata = Toml.getBool table "strip-metadata" defaults.StripMetadata
              MaxWidth = Toml.getInt table "max-width" defaults.MaxWidth
              ReencodePaste = Toml.getBool table "reencode-paste" defaults.ReencodePaste }
        with _ ->
            // Malformed config is the maintainer's problem to fix; the app degrades to defaults
            // rather than failing to open the repo (design 10).
            defaults
