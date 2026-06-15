/// Native image processing for the paste/drop pipeline (docs/design/06-images.md): sniff the real
/// format, optionally re-encode to the preferred format, strip metadata, and downscale. The
/// content hash of the *processed* bytes drives both `{hash8}` and de-duplication.
module SpecDesk.Core.ImageProcessing

open System
open System.IO
open System.Security.Cryptography
open System.Text
open SixLabors.ImageSharp
open SixLabors.ImageSharp.Formats
open SixLabors.ImageSharp.Formats.Gif
open SixLabors.ImageSharp.Formats.Jpeg
open SixLabors.ImageSharp.Formats.Png
open SixLabors.ImageSharp.Formats.Webp
open SixLabors.ImageSharp.Processing

type Processed = { Bytes: byte[]; Ext: string; Hash: string }

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData(bytes) |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""

let private encoderFor (ext: string) : IImageEncoder =
    match ext.ToLowerInvariant() with
    | "jpg"
    | "jpeg" -> JpegEncoder()
    | "gif" -> GifEncoder()
    | "webp" -> WebpEncoder()
    | _ -> PngEncoder()

let private looksLikeSvg (bytes: byte[]) : bool =
    if bytes.Length = 0 then
        false
    else
        let head = Encoding.UTF8.GetString(bytes, 0, min bytes.Length 256).TrimStart()

        head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
        || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))

/// Process raw image bytes per the repo config. SVG is passed through unchanged (ImageSharp cannot
/// rasterize it); raster formats are decoded, optionally normalized, and re-encoded. Returns an
/// error string for unsupported or corrupt input.
let processImage (config: ImagesConfig.ImagesConfig) (bytes: byte[]) : Result<Processed, string> =
    let allowedLower = config.Allowed |> List.map (fun a -> a.ToLowerInvariant())

    if looksLikeSvg bytes then
        if List.contains "svg" allowedLower then
            Ok { Bytes = bytes; Ext = "svg"; Hash = sha256Hex bytes }
        else
            Error "SVG images are not allowed in this repository."
    else
        try
            use image = Image.Load(bytes)

            let sourceExt =
                match image.Metadata.DecodedImageFormat with
                | null -> "png"
                | format -> Seq.tryHead format.FileExtensions |> Option.defaultValue "png"

            let targetExt =
                if config.ReencodePaste || not (List.contains (sourceExt.ToLowerInvariant()) allowedLower) then
                    config.Preferred.ToLowerInvariant()
                else
                    sourceExt.ToLowerInvariant()

            if config.StripMetadata then
                image.Metadata.ExifProfile <- null
                image.Metadata.IccProfile <- null
                image.Metadata.XmpProfile <- null

            if config.MaxWidth > 0 && image.Width > config.MaxWidth then
                let ratio = float config.MaxWidth / float image.Width
                let height = max 1 (int (float image.Height * ratio))
                image.Mutate(fun ctx -> ctx.Resize(config.MaxWidth, height) |> ignore)

            use stream = new MemoryStream()
            image.Save(stream, encoderFor targetExt)
            let output = stream.ToArray()
            Ok { Bytes = output; Ext = targetExt; Hash = sha256Hex output }
        with ex ->
            Error $"Could not read the image: {ex.Message}"
