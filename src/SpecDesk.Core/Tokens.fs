/// Token expansion for image folder and name patterns (docs/design/06-images.md, 10-repo-config.md).
/// Pure — date/seq/hash are supplied by the caller so this is unit-testable without I/O or a clock.
module SpecDesk.Core.Tokens

open System
open System.Globalization
open System.Text.RegularExpressions

type TokenContext =
    {
        DocSlug: string
        /// Document directory relative to the repo root, forward slashes, "" at the root.
        DocDir: string
        Date: DateTimeOffset
        Seq: int
        Hash8: string
        OriginalName: string option
    }

let private tokenPattern = Regex(@"\{(\w+)(?::([^}]*))?\}", RegexOptions.Compiled)

/// Expand `{docDir} {docSlug} {date:FMT} {seq} {hash8} {originalName}` in a pattern. `{slug:DESC}`
/// expands to empty (the description prompt is deferred); unknown tokens are left verbatim.
let expand (ctx: TokenContext) (pattern: string) : string =
    tokenPattern.Replace(
        pattern,
        fun (m: Match) ->
            let name = m.Groups.[1].Value.ToLowerInvariant()

            let arg =
                if m.Groups.[2].Success then
                    Some m.Groups.[2].Value
                else
                    None

            match name, arg with
            | "docslug", _ -> ctx.DocSlug
            | "docdir", _ -> ctx.DocDir
            | "date", Some fmt -> ctx.Date.ToString(fmt, CultureInfo.InvariantCulture)
            | "date", None -> ctx.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            | "seq", _ -> ctx.Seq.ToString("D3", CultureInfo.InvariantCulture)
            | "hash8", _ -> ctx.Hash8
            | "originalname", _ -> defaultArg ctx.OriginalName ""
            | "slug", _ -> ""
            | _ -> m.Value
    )
