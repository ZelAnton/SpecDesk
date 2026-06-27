/// Native image processing for the paste/drop pipeline (docs/design/06-images.md): sniff the real
/// format, optionally re-encode to the preferred format, strip metadata, and downscale. The
/// content hash of the *processed* bytes drives both `{hash8}` and de-duplication.
///
/// Backed by SkiaSharp (a permissively-licensed, cross-platform Skia binding). Skia DECODES every
/// raster format we accept but only ENCODES PNG/JPEG/WebP, so a GIF target is passed through unchanged
/// (which also keeps any animation), and any other un-encodable target falls back to PNG. A decode →
/// re-encode round-trip inherently drops EXIF/XMP/ICC metadata, so re-encoded output is always
/// metadata-free (this is what sheds EXIF/GPS from pasted screenshots — there is no separate strip
/// step or toggle); the pass-through formats (SVG, GIF) keep their bytes verbatim.
module SpecDesk.Core.ImageProcessing

open System
open System.Security.Cryptography
open System.Text
open SkiaSharp

type Processed = { Bytes: byte[]; Ext: string; Hash: string }

let private sha256Hex (bytes: byte[]) : string =
    SHA256.HashData(bytes) |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""

/// Sniff the raster format from the leading magic bytes — enough to choose the re-encode target. The
/// real decode below is what ultimately validates the pixels, so an unrecognised header just defaults
/// to "png" and is caught there if it is not actually decodable.
let private sniffExt (bytes: byte[]) : string =
    let hasPrefix (prefix: byte[]) =
        bytes.Length >= prefix.Length && Array.forall2 (=) prefix bytes[0 .. prefix.Length - 1]

    if hasPrefix [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |] then
        "png"
    elif hasPrefix [| 0xFFuy; 0xD8uy; 0xFFuy |] then
        "jpg"
    elif hasPrefix [| 0x47uy; 0x49uy; 0x46uy |] then // "GIF"
        "gif"
    elif
        bytes.Length >= 12
        && hasPrefix [| 0x52uy; 0x49uy; 0x46uy; 0x46uy |] // "RIFF"
        && bytes[8] = 0x57uy
        && bytes[9] = 0x45uy
        && bytes[10] = 0x42uy
        && bytes[11] = 0x50uy // "WEBP"
    then
        "webp"
    elif hasPrefix [| 0x42uy; 0x4Duy |] then // "BM"
        "bmp"
    else
        "png"

/// The Skia encoder for a target extension, or None when Skia cannot encode it (e.g. GIF, BMP).
let private encoderFor (ext: string) : SKEncodedImageFormat option =
    match ext with
    | "jpg"
    | "jpeg" -> Some SKEncodedImageFormat.Jpeg
    | "webp" -> Some SKEncodedImageFormat.Webp
    | "png" -> Some SKEncodedImageFormat.Png
    | _ -> None

// Re-encode quality for the lossy formats (JPEG/WebP); ignored by the lossless PNG encoder.
let private encodeQuality = 90

let private looksLikeSvg (bytes: byte[]) : bool =
    if bytes.Length = 0 then
        false
    else
        let head = Encoding.UTF8.GetString(bytes, 0, min bytes.Length 256).TrimStart()

        head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
        || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))

/// Decode the bytes, downscale if wider than the cap, and re-encode to `fmt`. Returns an error when the
/// bytes are not a decodable image, or when Skia declines to encode the (already filtered) format.
let private reencode
    (config: ImagesConfig.ImagesConfig)
    (bytes: byte[])
    (fmt: SKEncodedImageFormat)
    (ext: string)
    : Result<Processed, string> =
    match Option.ofObj (SKBitmap.Decode bytes) with
    | None -> Error "Could not read the image."
    | Some bitmap ->
        use bitmap = bitmap

        let scaled =
            if config.MaxWidth > 0 && bitmap.Width > config.MaxWidth then
                let ratio = float config.MaxWidth / float bitmap.Width
                let height = max 1 (int (float bitmap.Height * ratio))
                let info = SKImageInfo(config.MaxWidth, height)
                Option.ofObj (bitmap.Resize(info, SKSamplingOptions(SKCubicResampler.Mitchell)))
            else
                None

        use image = SKImage.FromBitmap(defaultArg scaled bitmap)
        let encoded = Option.ofObj (image.Encode(fmt, encodeQuality))
        scaled |> Option.iter (fun b -> b.Dispose())

        match encoded with
        | None -> Error "Could not encode the image."
        | Some data ->
            use data = data
            let output = data.ToArray()
            Ok { Bytes = output; Ext = ext; Hash = sha256Hex output }

/// Process raw image bytes per the repo config. SVG is passed through unchanged (Skia cannot rasterize
/// it); raster formats are decoded, optionally downscaled, and re-encoded. Returns an error string for
/// unsupported or corrupt input.
let processImage (config: ImagesConfig.ImagesConfig) (bytes: byte[]) : Result<Processed, string> =
    let allowedLower = config.Allowed |> List.map (fun a -> a.ToLowerInvariant())

    if looksLikeSvg bytes then
        if List.contains "svg" allowedLower then
            Ok { Bytes = bytes; Ext = "svg"; Hash = sha256Hex bytes }
        else
            Error "SVG images are not allowed in this repository."
    else
        try
            let sourceExt = sniffExt bytes

            let targetExt =
                if config.ReencodePaste || not (List.contains sourceExt allowedLower) then
                    config.Preferred.ToLowerInvariant()
                else
                    sourceExt

            match encoderFor targetExt with
            | Some fmt -> reencode config bytes fmt targetExt
            | None ->
                // Skia cannot encode this format (e.g. GIF). If the source already IS that format, keep
                // the original bytes verbatim (preserving any animation, like the SVG passthrough);
                // otherwise honour the request as best we can with a PNG re-encode. Validate the bytes
                // really decode before trusting a passthrough so corrupt input is still rejected.
                if targetExt = sourceExt then
                    match Option.ofObj (SKBitmap.Decode bytes) with
                    | None -> Error "Could not read the image."
                    | Some bitmap ->
                        bitmap.Dispose()
                        Ok { Bytes = bytes; Ext = targetExt; Hash = sha256Hex bytes }
                else
                    reencode config bytes SKEncodedImageFormat.Png "png"
        with ex ->
            Error $"Could not read the image: {ex.Message}"
