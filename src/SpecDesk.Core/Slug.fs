/// Slugification for image folder/file names: deterministic, filesystem-safe names in the repo's
/// chosen case, with truncation that preserves a trailing uniqueness suffix (docs/design/06-images.md).
module SpecDesk.Core.Slug

open System
open System.Globalization
open System.Text

/// The naming case from `.spectool.toml [images].case`.
type Case =
    | Kebab
    | Snake
    | Lower

let parseCase (value: string) : Case =
    match value.Trim().ToLowerInvariant() with
    | "snake" -> Snake
    | "lower" -> Lower
    | _ -> Kebab

let private separator (case: Case) : string =
    match case with
    | Kebab -> "-"
    | Snake -> "_"
    | Lower -> ""

/// Drop diacritics by decomposing and removing non-spacing marks (é → e).
let private removeDiacritics (text: string) : string =
    let decomposed = text.Normalize(NormalizationForm.FormD)
    let sb = StringBuilder(decomposed.Length)

    for ch in decomposed do
        if CharUnicodeInfo.GetUnicodeCategory(ch) <> UnicodeCategory.NonSpacingMark then
            sb.Append(ch) |> ignore

    sb.ToString().Normalize(NormalizationForm.FormC)

/// Lowercase, strip diacritics, and collapse every run of non-alphanumeric characters into the
/// case separator (none for <c>Lower</c>), with no leading/trailing separator.
let slugify (case: Case) (text: string) : string =
    let cleaned = removeDiacritics text
    let sep = separator case
    let sb = StringBuilder(cleaned.Length)
    let mutable pendingSeparator = false

    for ch in cleaned do
        if Char.IsLetterOrDigit ch then
            if pendingSeparator && sb.Length > 0 && sep.Length > 0 then
                sb.Append(sep) |> ignore

            pendingSeparator <- false
            sb.Append(Char.ToLowerInvariant ch) |> ignore
        else
            pendingSeparator <- true

    sb.ToString()

/// Truncate to <paramref name="maxLen"/> while keeping <paramref name="suffix"/> (the hash/seq
/// token) intact at the end, so uniqueness survives truncation. Assumes the name already ends
/// with the suffix.
let truncatePreservingSuffix (maxLen: int) (suffix: string) (name: string) : string =
    if maxLen <= 0 || name.Length <= maxLen then
        name
    elif suffix.Length >= maxLen then
        suffix.Substring(0, maxLen)
    else
        let headLen = maxLen - suffix.Length
        let head = name.Substring(0, headLen)
        head + suffix
