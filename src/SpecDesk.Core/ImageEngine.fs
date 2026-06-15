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

/// Containment guard (same rule as the PoC-1 app:// resolver): the target must stay inside root.
let private isInside (rootFull: string) (candidate: string) : bool =
    let candidateFull = Path.GetFullPath candidate
    let prefix = rootFull + string Path.DirectorySeparatorChar

    let comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    candidateFull.StartsWith(prefix, comparison)
    || String.Equals(candidateFull, rootFull, comparison)

let private buildResult (docDirAbs: string) (filePath: string) (alt: string) (reused: bool) : InsertResult =
    let relative = toForwardSlashes (Path.GetRelativePath(docDirAbs, filePath))
    { Markdown = $"![{alt}]({relative})"
      RelativePath = relative
      Reused = reused }

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
        let docDirAbs = Path.GetDirectoryName(Path.GetFullPath docPath) |> Option.ofObj |> Option.defaultValue rootFull
        let docSlug = Slug.slugify config.Case (nameStem docPath)

        let docDirRel =
            let rel = Path.GetRelativePath(rootFull, docDirAbs)
            if rel = "." then "" else toForwardSlashes rel

        let hash8 = processed.Hash.Substring(0, 8)

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
        else
            Directory.CreateDirectory(folderAbs) |> ignore

            let existing =
                if Directory.Exists folderAbs then
                    Directory.EnumerateFiles(folderAbs, $"*{hash8}.{processed.Ext}") |> Seq.tryHead
                else
                    None

            match existing with
            | Some path -> Ok(buildResult docDirAbs path altText true)
            | None ->
                let seq = (Directory.EnumerateFiles folderAbs |> Seq.length) + 1
                let context = { baseContext with Seq = seq }
                let stem = Slug.slugify config.Case (Tokens.expand context config.Naming)
                let stem = Slug.truncatePreservingSuffix config.MaxNameLength hash8 stem

                let mutable target = Path.Combine(folderAbs, $"{stem}.{processed.Ext}")
                let mutable disambiguator = 1

                while File.Exists target do
                    target <- Path.Combine(folderAbs, $"{stem}-{disambiguator}.{processed.Ext}")
                    disambiguator <- disambiguator + 1

                File.WriteAllBytes(target, processed.Bytes)
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

    match insertImage repoRoot docPath config capture with
    | Ok result ->
        { Markdown = result.Markdown
          Error = null
          Reused = result.Reused }
    | Error e -> { Markdown = null; Error = e; Reused = false }
