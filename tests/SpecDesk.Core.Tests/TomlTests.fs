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
