module SpecDesk.Core.Tests.TomlTests

open NUnit.Framework
open SpecDesk.Core

// Toml is the hand-rolled reader behind every .spectool.toml table ([images]/[repo]/[branch]/[commit]).
// The config-level tests cover the happy path through WorkflowConfig/ImagesConfig; these pin the reader's
// own edge cases — table scoping, inline-comment stripping that respects quotes, CRLF, case-insensitive
// names/keys, last-value-wins, and each typed getter's fallback — so a regression surfaces here, not as a
// silently mis-parsed user config.

// --- readTable: scoping, comments, normalization ---

[<Test>]
let ``reads a key/value from the named table`` () =
    let t = Toml.readTable "images" "[images]\nmax-width = 800\n"
    Assert.That(Toml.getInt t "max-width" 0, Is.EqualTo 800)

[<Test>]
let ``keys outside the named table are ignored`` () =
    let t = Toml.readTable "images" "[repo]\nmax-width = 5\n[images]\nmax-width = 800\n"
    Assert.That(Toml.getInt t "max-width" 0, Is.EqualTo 800)

[<Test>]
let ``the table name is matched case-insensitively`` () =
    let t = Toml.readTable "images" "[IMAGES]\nquality = 1\n"
    Assert.That(Toml.getInt t "quality" 0, Is.EqualTo 1)

[<Test>]
let ``blank lines and full-line comments are skipped`` () =
    let t = Toml.readTable "images" "[images]\n\n# a comment\nquality = 2\n"
    Assert.That(Toml.getInt t "quality" 0, Is.EqualTo 2)

[<Test>]
let ``an inline comment is stripped from a value`` () =
    let t = Toml.readTable "images" "[images]\nquality = 80 # best effort\n"
    Assert.That(Toml.getInt t "quality" 0, Is.EqualTo 80)

[<Test>]
let ``a hash inside a quoted value is not treated as a comment`` () =
    let t = Toml.readTable "repo" "[repo]\ntag = \"a#b\"\n"
    Assert.That(Toml.getString t "tag" "", Is.EqualTo "a#b")

// M-05 regression guards: a naive "every `"` toggles the quote tracker" reading treats an escaped `\"`
// as a real close, so a `#` sitting between the true open and close of a value like
// `template = "Say \"hi\"" # note` was wrongly read as a comment start, truncating the value.

[<Test>]
let ``a hash right after a single escaped quote is still inside the string (odd-parity case)`` () =
    // The naive tracker flips on EVERY literal '"' — after exactly ONE escaped quote its state is
    // wrong (it thinks the string just closed), which is the precise parity that reproduces the
    // finding's truncation. An EVEN number of escaped quotes before the '#' happens to come out right
    // by coincidence even under the naive tracker, so this specific shape is what actually pins the bug.
    let t = Toml.readTable "repo" "[repo]\ntemplate = \"a\\\"#b\"\n"
    Assert.That(Toml.getString t "template" "", Is.EqualTo "a\"#b")

[<Test>]
let ``a real inline comment after a value with escaped quotes is still stripped`` () =
    let t = Toml.readTable "repo" "[repo]\ntemplate = \"Say \\\"hi\\\"\" # a real comment\n"
    Assert.That(Toml.getString t "template" "", Is.EqualTo "Say \"hi\"")

[<Test>]
let ``CRLF line endings are handled`` () =
    let t = Toml.readTable "images" "[images]\r\nquality = 3\r\n"
    Assert.That(Toml.getInt t "quality" 0, Is.EqualTo 3)

[<Test>]
let ``a later duplicate key wins`` () =
    let t = Toml.readTable "images" "[images]\nquality = 1\nquality = 2\n"
    Assert.That(Toml.getInt t "quality" 0, Is.EqualTo 2)

[<Test>]
let ``keys are looked up case-insensitively`` () =
    let t = Toml.readTable "images" "[images]\nMax-Width = 800\n"
    Assert.That(Toml.getInt t "max-width" 0, Is.EqualTo 800)

[<Test>]
let ``a line without an equals sign is ignored`` () =
    let t = Toml.readTable "images" "[images]\nnonsense\nquality = 4\n"
    Assert.That(Toml.getInt t "quality" 0, Is.EqualTo 4)

// --- getString ---

[<Test>]
let ``getString unquotes a quoted value`` () =
    let t = Toml.readTable "repo" "[repo]\nname = \"hello\"\n"
    Assert.That(Toml.getString t "name" "", Is.EqualTo "hello")

[<Test>]
let ``getString returns an unquoted value as-is`` () =
    let t = Toml.readTable "repo" "[repo]\nname = hello\n"
    Assert.That(Toml.getString t "name" "", Is.EqualTo "hello")

[<Test>]
let ``getString falls back when the key is absent`` () =
    let t = Toml.readTable "repo" "[repo]\n"
    Assert.That(Toml.getString t "name" "fallback", Is.EqualTo "fallback")

// M-05 acceptance case: `template = "Say \"hi\""` round-trips to the literal text `Say "hi"` — no
// leftover backslashes, no truncation at the escaped/real quote boundary.
[<Test>]
let ``getString unescapes an escaped quote instead of keeping the backslash`` () =
    let t = Toml.readTable "commit" "[commit]\ntemplate = \"Say \\\"hi\\\"\"\n"
    Assert.That(Toml.getString t "template" "", Is.EqualTo "Say \"hi\"")

[<Test>]
let ``getString unescapes a literal backslash and common escapes`` () =
    let t = Toml.readTable "commit" "[commit]\ntemplate = \"a\\\\b\\nc\\td\"\n"
    Assert.That(Toml.getString t "template" "", Is.EqualTo "a\\b\nc\td")

[<Test>]
let ``getString leaves an unrecognized escape sequence verbatim`` () =
    let t = Toml.readTable "commit" "[commit]\ntemplate = \"a\\qb\"\n"
    Assert.That(Toml.getString t "template" "", Is.EqualTo "a\\qb")

// --- getBool ---

[<Test>]
let ``getBool parses true and false case-insensitively`` () =
    let t = Toml.readTable "x" "[x]\na = true\nb = FALSE\n"
    Assert.That(Toml.getBool t "a" false, Is.True)
    Assert.That(Toml.getBool t "b" true, Is.False)

[<Test>]
let ``getBool returns the fallback for a non-boolean value`` () =
    let t = Toml.readTable "x" "[x]\na = yes\n"
    Assert.That(Toml.getBool t "a" true, Is.True)
    Assert.That(Toml.getBool t "a" false, Is.False)

[<Test>]
let ``getBool falls back when the key is absent`` () =
    let t = Toml.readTable "x" "[x]\n"
    Assert.That(Toml.getBool t "missing" true, Is.True)

// --- getInt ---

[<Test>]
let ``getInt returns the fallback for a non-integer value`` () =
    let t = Toml.readTable "x" "[x]\nn = abc\n"
    Assert.That(Toml.getInt t "n" 42, Is.EqualTo 42)

[<Test>]
let ``getInt falls back when the key is absent`` () =
    let t = Toml.readTable "x" "[x]\n"
    Assert.That(Toml.getInt t "n" 7, Is.EqualTo 7)

// --- getList ---

[<Test>]
let ``getList extracts the quoted items`` () =
    let t = Toml.readTable "x" "[x]\nexts = [\"png\", \"jpg\"]\n"
    Assert.That(Toml.getList t "exts" [] = [ "png"; "jpg" ], Is.True)

[<Test>]
let ``getList falls back when there are no quoted items`` () =
    let t = Toml.readTable "x" "[x]\nexts = []\n"
    Assert.That(Toml.getList t "exts" [ "default" ] = [ "default" ], Is.True)

[<Test>]
let ``getList falls back when the key is absent`` () =
    let t = Toml.readTable "x" "[x]\n"
    Assert.That(Toml.getList t "exts" [ "d" ] = [ "d" ], Is.True)

[<Test>]
let ``getList reads a multi-line array`` () =
    // The common one-entry-per-line TOML form must parse the same as its single-line equivalent.
    let t = Toml.readTable "review" "[review]\nreviewers = [\n  \"@alice\",\n  \"@bob\",\n]\n"
    Assert.That(Toml.getList t "reviewers" [] = [ "@alice"; "@bob" ], Is.True)

[<Test>]
let ``a multi-line array does not swallow the following keys`` () =
    // Once the array closes, later keys in the same table are still read.
    let t = Toml.readTable "review" "[review]\nreviewers = [\n  \"@alice\"\n]\ndraft-first = true\n"
    Assert.That(Toml.getList t "reviewers" [] = [ "@alice" ], Is.True)
    Assert.That(Toml.getBool t "draft-first" false, Is.True)

[<Test>]
let ``a multi-line array in another table is not read into the target`` () =
    let t = Toml.readTable "review" "[repo]\nspec-globs = [\n  \"**/*.md\"\n]\n[review]\nreviewers = [\"@alice\"]\n"
    Assert.That(Toml.getList t "reviewers" [] = [ "@alice" ], Is.True)

[<Test>]
let ``a bracket in a trailing comment does not close a multi-line array early`` () =
    let t = Toml.readTable "review" "[review]\nreviewers = [\n  \"@alice\", # lead [temp]\n  \"@bob\",\n]\n"
    Assert.That(Toml.getList t "reviewers" [] = [ "@alice"; "@bob" ], Is.True)

[<Test>]
let ``an unclosed array degrades only its own key`` () =
    // A missing ']' must not swallow later keys back to their defaults (regression guard).
    let t = Toml.readTable "repo" "[repo]\nspec-globs = [\n  \"**/*.md\"\ndefault-base = \"develop\"\n"
    Assert.That(Toml.getString t "default-base" "main", Is.EqualTo "develop")

[<Test>]
let ``a bracket inside a quoted entry does not close a multi-line array early`` () =
    // A glob character class contains ']'; the close test must be quote-aware, not a raw substring match.
    let t = Toml.readTable "repo" "[repo]\nspec-globs = [\n  \"docs/[a-z].md\",\n  \"specs/x.md\"\n]\n"
    Assert.That(Toml.getList t "spec-globs" [] = [ "docs/[a-z].md"; "specs/x.md" ], Is.True)

[<Test>]
let ``an array left open at end of input keeps its partial value`` () =
    // A never-closed array at EOF flushes its best-effort partial value (not nothing), matching the
    // mid-file unclosed case.
    let t = Toml.readTable "review" "[review]\nreviewers = [\n  \"@alice\"\n"
    Assert.That(Toml.getList t "reviewers" [] = [ "@alice" ], Is.True)
