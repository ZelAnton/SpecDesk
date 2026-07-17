/// The image rule engine: turn captured bytes into a processed, named, de-duplicated file inside
/// the repo working tree, and return the document-relative Markdown link (docs/design/06-images.md).
/// This is the only module here that touches the filesystem. Git staging is deferred to PoC-4.
module SpecDesk.Core.ImageEngine

open System
open System.IO

type ImageCapture =
    { Bytes: byte[]
      OriginalName: string option
      Mime: string option }

type InsertResult =
    { Markdown: string
      RelativePath: string
      Reused: bool }

/// C#-friendly result for the host adapter: <c>Markdown</c> is null when <c>Error</c> is set.
[<CLIMutable>]
type InsertOutcome =
    { Markdown: string | null
      Error: string | null
      Reused: bool }

let private toForwardSlashes (path: string) : string = path.Replace('\\', '/')

/// File name without extension, never null (the BCL annotates the return as nullable).
let private nameStem (path: string) : string =
    Path.GetFileNameWithoutExtension path |> Option.ofObj |> Option.defaultValue ""

/// Reduce a file extension to lowercase ASCII alphanumerics (fallback "png"). The `Preferred` format
/// comes from an UNTRUSTED .spectool.toml and is interpolated straight into a file path; a real
/// extension never contains separators or dots, so stripping them defeats a `preferred = "png/../.."`
/// path-traversal write outside the repo.
let internal sanitizeExt (ext: string) : string =
    let isAsciiAlnum c =
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')

    let cleaned =
        String(ext.ToLowerInvariant() |> Seq.filter isAsciiAlnum |> Seq.toArray)

    if cleaned = "" then "png" else cleaned

/// Containment guard (same rule as the PoC-1 app:// resolver): the target must stay inside root.
let internal isInside (rootFull: string) (candidate: string) : bool =
    let candidateFull = Path.GetFullPath candidate
    let prefix = rootFull + string Path.DirectorySeparatorChar

    let comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    candidateFull.StartsWith(prefix, comparison)
    || String.Equals(candidateFull, rootFull, comparison)

/// True when any existing path component below the root is a reparse point (symlink/junction). The
/// opened repo is UNTRUSTED and Path.GetFullPath does not resolve reparse points, so a committed
/// junction could pass the textual isInside check yet make a write land outside the repo. Reject it
/// (a spec repo has no reason to use links under it), mirroring the app:// read-path guard.
let private traversesReparsePoint (rootFull: string) (candidate: string) : bool =
    let relative = Path.GetRelativePath(rootFull, Path.GetFullPath candidate)

    let segments =
        relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])

    let mutable current = rootFull
    let mutable found = false

    for segment in segments do
        if not found && segment <> "" && segment <> "." then
            current <- Path.Combine(current, segment)

            let info: FileSystemInfo option =
                if Directory.Exists current then Some(DirectoryInfo current)
                elif File.Exists current then Some(FileInfo current)
                else None

            match info with
            | Some i when i.Attributes.HasFlag FileAttributes.ReparsePoint -> found <- true
            | _ -> ()

    found

/// Percent-encode the characters that make a relative path unsafe as a BARE CommonMark link
/// destination, or that a plain URL resolver would otherwise mis-handle: ASCII whitespace and `(`/`)`
/// can end a bare CommonMark destination early (unescaped/unbalanced parens, or any whitespace), and
/// `#` would be read as a URL fragment separator rather than a literal path character. `%` itself is
/// escaped FIRST — any percent-encoding scheme must, or a literal `%` already in the path becomes
/// ambiguous with the escapes this introduces. The native/webview readers already expect this: they
/// pass the link's URL straight through into `app://repo/<path>`, and `AppAssetResolver.ResolveRelative`
/// un-escapes it (`Uri.UnescapeDataString`) before touching the filesystem.
let internal percentEncodeForLink (path: string) : string =
    path
    |> Seq.map (fun c ->
        match c with
        | '%' -> "%25"
        | '(' -> "%28"
        | ')' -> "%29"
        | '#' -> "%23"
        | c when Char.IsWhiteSpace c -> Uri.EscapeDataString(string c)
        | c -> string c)
    |> String.concat ""

let private buildResult (docDirAbs: string) (filePath: string) (alt: string) (reused: bool) : InsertResult =
    let relative = toForwardSlashes (Path.GetRelativePath(docDirAbs, filePath))

    { Markdown = $"![{alt}]({percentEncodeForLink relative})"
      RelativePath = relative
      Reused = reused }

/// Write `bytes` to `target` atomically: write to a same-directory temp file first, then rename it
/// into place. `File.Move` (same volume, the common case here) is atomic, so `target` only ever exists
/// in a complete state — a crash or power loss mid-write leaves an orphaned temp file behind, never a
/// truncated file under `target`'s own hash8-suffixed name that a later insert's dedup lookup (which
/// only matches on that suffix, never re-verifies content) would otherwise mistake for a genuine,
/// complete previous write and "reuse" forever.
let internal writeFileAtomically (target: string) (bytes: byte[]) : unit =
    let tempPath = target + "." + Guid.NewGuid().ToString("N") + ".tmp"

    try
        File.WriteAllBytes(tempPath, bytes)
        File.Move(tempPath, target)
    finally
        if File.Exists tempPath then
            File.Delete tempPath

/// Process, name, de-duplicate, and write the image; return its document-relative link.
let insertImage
    (repoRoot: string)
    (docPath: string)
    (config: ImagesConfig.ImagesConfig)
    (capture: ImageCapture)
    : Result<InsertResult, string> =
    match ImageProcessing.processImage config capture.Bytes with
    | Error e -> Error e
    | Ok processed ->
        let rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath repoRoot)

        let docDirAbs =
            Path.GetDirectoryName(Path.GetFullPath docPath)
            |> Option.ofObj
            |> Option.defaultValue rootFull

        let docSlug = Slug.slugify config.Case (nameStem docPath)

        let docDirRel =
            let rel = Path.GetRelativePath(rootFull, docDirAbs)
            if rel = "." then "" else toForwardSlashes rel

        let hash8 = processed.Hash.Substring(0, 8)
        let ext = sanitizeExt processed.Ext

        let altText =
            match capture.OriginalName with
            | Some name ->
                match Slug.slugify config.Case (nameStem name) with
                | "" -> "image"
                | slug -> slug
            | None -> "image"

        let baseContext: Tokens.TokenContext =
            { DocSlug = docSlug
              DocDir = docDirRel
              Date = DateTimeOffset.Now
              Seq = 0
              Hash8 = hash8
              OriginalName =
                capture.OriginalName
                |> Option.map (fun n -> Slug.slugify config.Case (nameStem n)) }

        let folderRel = Tokens.expand baseContext config.Folder
        let folderAbs = Path.GetFullPath(Path.Combine(rootFull, folderRel))

        if not (isInside rootFull folderAbs) then
            Error "The configured image folder is outside the repository."
        elif traversesReparsePoint rootFull folderAbs then
            Error "The image folder path leaves the repository through a link."
        else
            Directory.CreateDirectory(folderAbs) |> ignore

            let existing =
                if Directory.Exists folderAbs then
                    Directory.EnumerateFiles(folderAbs, $"*{hash8}.{ext}") |> Seq.tryHead
                else
                    None

            match existing with
            | Some path -> Ok(buildResult docDirAbs path altText true)
            | None ->
                let seq = (Directory.EnumerateFiles folderAbs |> Seq.length) + 1
                let context = { baseContext with Seq = seq }
                let stem = Slug.slugify config.Case (Tokens.expand context config.Naming)
                let stem = Slug.truncatePreservingSuffix config.MaxNameLength hash8 stem
                // A naming pattern can expand to nothing (all symbols stripped, or an empty token); never
                // write a bare ".ext" hidden file — fall back to the content hash, which is also unique.
                let stem = if stem = "" then hash8 else stem

                let mutable target = Path.Combine(folderAbs, $"{stem}.{ext}")
                let mutable disambiguator = 1

                while File.Exists target do
                    target <- Path.Combine(folderAbs, $"{stem}-{disambiguator}.{ext}")
                    disambiguator <- disambiguator + 1

                // Durable defence (in addition to sanitizeExt and the slugified stem): the final path
                // must still resolve inside the repo before we write.
                if not (isInside rootFull (Path.GetFullPath target)) then
                    Error "The resolved image path is outside the repository."
                else
                    writeFileAtomically target processed.Bytes
                    Ok(buildResult docDirAbs target altText false)

/// C#-friendly entry for the host: plain inputs (nulls allowed), config parsed from raw TOML text.
let insertForHost
    (repoRoot: string)
    (docPath: string)
    (tomlText: string | null)
    (bytes: byte[])
    (originalName: string | null)
    (mime: string | null)
    : InsertOutcome =
    let config = ImagesConfig.parse (Option.ofObj tomlText)

    let capture =
        { Bytes = bytes
          OriginalName = Option.ofObj originalName
          Mime = Option.ofObj mime }

    // insertImage does filesystem I/O (CreateDirectory / WriteAllBytes) and path math that can throw on
    // a crafted config/path (illegal chars, reserved device name, permission denied). The host runs this
    // on a background task and awaits a reply, so a thrown exception would silently drop the paste and
    // hang the webview — map every failure to a plain Error instead.
    try
        match insertImage repoRoot docPath config capture with
        | Ok result ->
            { Markdown = result.Markdown
              Error = null
              Reused = result.Reused }
        | Error e ->
            { Markdown = null
              Error = e
              Reused = false }
    with ex ->
        { Markdown = null
          Error = $"Could not save the image: {ex.Message}"
          Reused = false }
