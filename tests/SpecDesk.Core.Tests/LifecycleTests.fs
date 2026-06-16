module SpecDesk.Core.Tests.LifecycleTests

open NUnit.Framework
open SpecDesk.Core
open SpecDesk.Core.Lifecycle

// Type the expected value's error case as string so it matches `next`'s Result<State, string>
// (a bare `Ok Draft` infers Result<State, obj>, which NUnit treats as a different type).
let private ok (state: State) : Result<State, string> = Ok state

[<Test>]
let ``edit forks a draft from published`` () =
    Assert.That(next Published Edit, Is.EqualTo(ok Draft))

[<Test>]
let ``saving a draft keeps it a draft`` () =
    Assert.That(next Draft SaveVersion, Is.EqualTo(ok Draft))

[<Test>]
let ``discarding a draft returns to published`` () =
    Assert.That(next Draft Discard, Is.EqualTo(ok Published))

[<Test>]
let ``sending a draft for review opens review`` () =
    Assert.That(next Draft SendForReview, Is.EqualTo(ok InReview))

[<Test>]
let ``a reviewer requesting changes moves to changes-requested`` () =
    Assert.That(next InReview RequestChanges, Is.EqualTo(ok ChangesRequested))

[<Test>]
let ``saving after changes requested re-opens review`` () =
    Assert.That(next ChangesRequested SaveVersion, Is.EqualTo(ok InReview))

[<Test>]
let ``approval then publish returns to published`` () =
    Assert.That(next InReview Approve, Is.EqualTo(ok Approved))
    Assert.That(next Approved Publish, Is.EqualTo(ok Published))

[<Test>]
let ``editing again after approval re-opens review`` () =
    Assert.That(next Approved SaveVersion, Is.EqualTo(ok InReview))

[<Test>]
let ``illegal transitions are rejected with a reason`` () =
    match next Published SaveVersion with
    | Ok _ -> Assert.Fail "Saving a version while Published should be rejected"
    | Error message -> Assert.That(message, Does.Contain "Published")

[<Test>]
let ``publishing is only allowed from approved`` () =
    Assert.That(Result.isError (next Draft Publish))
    Assert.That(Result.isError (next InReview Publish))

[<Test>]
let ``tryStep facade returns the next state name`` () =
    Assert.That(tryStep "published" "edit", Is.EqualTo "draft")
    Assert.That(tryStep "draft" "discard", Is.EqualTo "published")

[<Test>]
let ``tryStep facade returns empty on an illegal or unknown transition`` () =
    Assert.That(tryStep "published" "saveVersion", Is.EqualTo "")
    Assert.That(tryStep "published" "bogus", Is.EqualTo "")
    Assert.That(tryStep "nonsense" "edit", Is.EqualTo "")

[<Test>]
let ``labelOf gives author-facing text and empty for unknown`` () =
    Assert.That(labelOf "draft", Does.Contain "Draft")
    Assert.That(labelOf "published", Is.EqualTo "Published")
    Assert.That(labelOf "nonsense", Is.EqualTo "")
