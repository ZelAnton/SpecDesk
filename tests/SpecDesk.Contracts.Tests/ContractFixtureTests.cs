using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpecDesk.Contracts.Tests;

/// <summary>
/// Cross-language contract guard for the native→webview boundary. The wire shape of every payload is
/// hand-mirrored in two places — the C# records here (<see cref="DocLoadedPayload"/> et al., serialized
/// with <see cref="IpcSerializer.Options"/>) and the webview's TypeScript decoders — so a rename or a
/// changed field type drifts silently until it crashes at runtime. This fixture is the shared anchor:
/// the C# side pins it (this test), and the webview side validates its decoders against the same file
/// (<c>webview/tests/contract.test.ts</c>). A change on either side that the other doesn't follow breaks
/// CI in that language. A sibling guard in <c>SpecDesk.Core.Tests</c> (LifecycleContractTests) pins the
/// lifecycle state names the same way, from the F# source of truth. Both regenerate under the
/// <c>UPDATE_CONTRACT_FIXTURE=1</c> opt-in, but they live in different test projects — run it over the
/// whole solution (<c>UPDATE_CONTRACT_FIXTURE=1 dotnet test SpecDesk.slnx</c>) so every fixture is
/// rewritten, not a single <c>--filter</c>, then update the decoders/protocol to match.
/// </summary>
[TestFixture]
public sealed class ContractFixtureTests
{
	/// <summary>One representative instance of every native→webview payload, keyed by its wire kind.</summary>
	private static readonly (string Kind, object Payload)[] NativePayloads =
	[
		(MessageKinds.DocLoaded,
			new DocLoadedPayload("specs/billing.md", "# Billing\n\nThe refund window is 30 days.\n", "specs")),
		(MessageKinds.DocOpenCompleted, new DocOpenCompletedPayload(17, Succeeded: true)),
		(MessageKinds.DocDiscardCompleted, new DocDiscardCompletedPayload(18, Succeeded: false)),
		(MessageKinds.PreviewHtml,
			new PreviewPayload("<h1>Billing</h1>", [new LineSpan(0, 0), new LineSpan(2, 2)])),
		(MessageKinds.Status,
			new StatusPayload("draft", "Unsaved changes", "spec/billing-refunds")),
		(MessageKinds.Error,
			new ErrorPayload("Could not reach GitHub. Check your connection and try again.")),
		(MessageKinds.ImageInserted,
			new ImageInsertedPayload("![pasted image](images/diagram.png)")),
		(MessageKinds.BranchNameSuggested,
			new BranchNameSuggestedPayload("spec/refund-window")),
		(MessageKinds.VersionNoteSuggested,
			new VersionNoteSuggestedPayload("Clarify the refund window is 30 days")),
		// Ready to send (Blocked absent): the optional field is exercised by both decoders.
		(MessageKinds.PrSuggested,
			new PrSuggestedPayload("Clarify the refund window", "Review requested for billing.md via SpecDesk.", null)),
		// One authored + one to-review item (Error absent — the optional field is exercised by both decoders).
		(MessageKinds.PrList, new PrListPayload(
		[
			new PrListItemPayload(42, "Clarify the refund window", "https://github.com/octo/spec-repo/pull/42",
				"octo/spec-repo", "author", "changesRequested", "Changes requested"),
			new PrListItemPayload(7, "Payment terms", "https://github.com/octo/other/pull/7",
				"octo/other", "reviewer", "inReview", "In review"),
		], null)),
		(MessageKinds.PrDetails, new PrDetailsPayload(
			42, "octo/spec-repo", "Clarify refunds", "Explain the refund window.",
			"https://github.com/octo/spec-repo/pull/42", "open", false, "alex", "https://img/alex",
			"main", "spec/refunds",
			[new PrParticipantPayload("sam", "https://img/sam", "user")],
			[new PrCommentPayload(9, "conversation", "", "sam", "https://img/sam", "Please clarify.",
				DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false)],
			[new PrCommitPayload("abcdef", "abcdef0", "Clarify the window", DateTimeOffset.UnixEpoch, "success")],
			false,
			false,
			null)),
		(MessageKinds.PrMutationCompleted, new PrMutationCompletedPayload(true, null)),
		// Inline review-comment sync (PoC-8): the open PR's commentable head lines and one existing inline
		// comment projected onto the document; Error absent (the optional field is exercised by both decoders).
		(MessageKinds.ReviewCommentSync, new ReviewCommentSyncPayload(
			"octo/spec-repo spec/refunds specs/billing.md", 42, "abcdef0123456789", "specs/billing.md",
			[3, 4, 5],
			[new ReviewCommentAnchorPayload(
				1001, 4, "RIGHT", "abcdef0123456789", 0, "sam", "Please clarify the window here.",
				DateTimeOffset.UnixEpoch)],
			null)),
		// A successful post-to-review acknowledgement (Error absent).
		(MessageKinds.ReviewCommentPublished,
			new ReviewCommentPublishedPayload("selection-comment-3", 2002, Succeeded: true, null)),
		(MessageKinds.DiffResult, new DiffResultPayload(
		[
			// A changed plain block carries its base rendered text and base raw source for inline word-diff.
			new ChangedDiffEntry(2, 2, [], "The refund window is 14 days.", "The refund window is 14 days."),
			// A changed container (list/table) carries per-child entries: a changed row (with its base
			// flattened text AND base raw source slice for the two panes' inline word-diff) and a removed item.
			new ChangedDiffEntry(4, 6,
			[
				new ChangedChildDiff(1, "Net 30", "| Terms | Net 30 |"),
				new RemovedChildDiff(2, "Legacy clause"),
			], "", ""),
			// A removed top-level block: not in the head, so it anchors before a head line and carries its source.
			new RemovedDiffEntry(8, "Deprecated section"),
		])),
		(MessageKinds.GitHubCode,
			new GitHubCodePayload("WXYZ-1234", "https://github.com/login/device")),
		// Signed in: Login present, Message absent (the optional fields are exercised by both decoders).
		(MessageKinds.GitHubAccount,
			new GitHubAccountPayload(
				Available: true,
				SignedIn: true,
				Login: "octocat",
				Message: null,
				Organizations: ["acme", "octo-labs"],
				AvatarUrl: "https://avatars.githubusercontent.com/u/583231?v=4",
				PublicationId: "account-publication-1")),
		(MessageKinds.GitHubRepositories, new GitHubRepositoriesPayload(
		[
			new GitHubRepositoryOptionPayload("acme/specs", "Product specifications"),
			new GitHubRepositoryOptionPayload("octocat/notes", null),
		])),
		(MessageKinds.RepoCloneDestination, new RepoCloneDestinationPayload(
			"acme/specs", 7, @"C:\SpecDesk\repos\product-specs", "product-specs", false)),
		(MessageKinds.RepoCloneConflict, new RepoCloneConflictPayload(
			"acme/specs", "product-specs", @"C:\SpecDesk\repos\product-specs",
			"A local copy with that name already exists. Open it instead?")),
		(MessageKinds.RepoConfirmation, new RepoConfirmationPayload(
			"deleteBranch",
			"acme/specs",
			@"C:\SpecDesk\repos\product-specs",
			"review-copy",
			"Delete this local working line?",
			["There are unfinished local edits.", "There is one protected local work snapshot."],
			"DD42A087")),
		(MessageKinds.RepoOperationCompleted, new RepoOperationCompletedPayload(42)),
		(MessageKinds.RepoDescription, new RepoDescriptionPayload(
			"acme/specs", 8, RepoDescriptionStates.Found, "Product specifications")),
		// AI assistant (PoC-8): a streamed reply chunk, a turn-complete marker, and the prompt library.
		(MessageKinds.ChatDelta, new ChatDeltaPayload("7", "Here is a summary of the change: ")),
		(MessageKinds.ChatDone, new ChatDonePayload("7")),
		// The gated proposeEdit confirmation round-trip: the staged before/after (Summary present exercises
		// the optional field through both decoders) and the applied-text echo on a confirmed edit.
		(MessageKinds.ConfirmRequest, new ConfirmRequestPayload(
			"3",
			"The refund window is 14 days.\n",
			"The refund window is 30 days.\n",
			"Extend the refund window to 30 days.")),
		(MessageKinds.ConfirmApplied, new ConfirmAppliedPayload("3", "The refund window is 30 days.\n")),
		(MessageKinds.ChatAttachmentPicked,
			new ChatAttachmentPayload("file", "billing.md", @"C:\specs\billing.md")),
		(MessageKinds.DocumentActivity, new DocumentActivityPayload(
			"billing.md",
			[new DocumentVersionPayload("abc123", "Clarify refunds", "Alex", DateTimeOffset.UnixEpoch)],
			"loaded",
			null,
			[],
			"loaded",
			null,
			[new DocumentChangePayload(
				"abc123", "Document updated", "Clarify refunds", "Alex", DateTimeOffset.UnixEpoch)])),
		// One personal + one remote template (both lists exercised; the remote list may be empty at runtime).
		(MessageKinds.Templates, new TemplatesPayload(
		[
			new PromptTemplate("summarize-changes", "Summarize the changes",
				"Summarize what changed in this document since the last saved version."),
		],
		[
			new PromptTemplate("team-style", "Apply the team style", "Rewrite the selection to follow our style guide."),
		])),
		// One requested file-tree level: a lazy directory plus a leaf file and correlation id.
		(MessageKinds.Tree, new TreePayload(@"C:\specs\billing-repo",
		[
			new TreeNode("specs", @"C:\specs\billing-repo\specs", IsDirectory: true,
				[], HasChildren: true),
			new TreeNode("README.md", @"C:\specs\billing-repo\README.md", IsDirectory: false, []),
		], RequestId: 31)),
		(MessageKinds.FileDeleteCompleted, new FileDeleteCompletedPayload(
			@"C:\specs\billing-repo\README.md", @"C:\specs\billing-repo", 32, Succeeded: true)),
		// CanPublish true exercises the author-publish gate through both decoders (the repo permits publishing).
		(MessageKinds.WorkspaceContext, new WorkspaceContextPayload(
			"billing-repo", @"C:\specs\billing-repo", "spec/billing-refunds", "named", "main", "specs/billing.md",
			"billing-repo", CanPublish: true)),
		(MessageKinds.WindowState, new WindowStatePayload(Maximized: true)),
		(MessageKinds.WindowCloseRequested, new WindowCloseRequestedPayload(RequestId: 23)),
		(MessageKinds.WindowCloseCompleted, new WindowCloseCompletedPayload(RequestId: 23, Succeeded: false)),
		// A4 workspace store: one recent file, one favorited folder, and one registered GitHub repo -
		// exercises both WorkspaceItem shapes (file vs folder) and the RegisteredRepo record.
		(MessageKinds.WorkspaceState, new WorkspaceStatePayload(
		[
			new WorkspaceItem(@"C:\specs\billing-repo\specs\billing.md", "billing.md", IsFolder: false),
		],
		[
			new WorkspaceItem(@"C:\specs\billing-repo\specs", "specs", IsFolder: true),
		],
		[
			new RegisteredRepo(
				"octo/spec-repo", "octo/spec-repo", "https://github.com/octo/spec-repo", "main",
				[new RegisteredClone(
					"octo-specs",
					"C:\\specs\\octo-specs",
					"review-copy",
					[
						new RegisteredBranch(
							"main", RepositoryStatusPayload.Empty, CanDelete: false, CanRename: false),
						new RegisteredBranch(
							"review-copy",
							new RepositoryStatusPayload(2, 1, true, 1, false),
							CanDelete: true,
							CanRename: true),
					],
					new RepositoryStatusPayload(2, 1, true, 3, false))]),
		])),
	];

	[TestCase(0, true)]
	[TestCase(1, false)]
	public void RegisteredBranch_LegacyPayloadInfersRenameOnlyWhenTheLocalLineIsUnprotected(
		int stashCount,
		bool expectedCanRename)
	{
		string json = $$"""
			{"name":"draft","status":{"ahead":0,"behind":0,"hasUncommitted":false,"stashCount":{{stashCount}},"hasConflicts":false},"canDelete":true}
			""";

		RegisteredBranch? branch = JsonSerializer.Deserialize<RegisteredBranch>(json, IpcSerializer.Options);

		Assert.That(branch?.CanRename, Is.EqualTo(expectedCanRename));
	}

	private static string FixturePath(string fileName)
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpecDesk.slnx")))
		{
			dir = dir.Parent;
		}
		if (dir is null)
		{
			throw new InvalidOperationException("Could not locate the repo root (no SpecDesk.slnx above the test binary).");
		}
		return Path.Combine(dir.FullName, "webview", "tests", "contract", fileName);
	}

	[Test]
	public void NativePayloads_MatchTheCommittedContractFixture()
	{
		JsonObject actual = [];
		foreach ((string kind, object payload) in NativePayloads)
		{
			actual[kind] = JsonSerializer.SerializeToNode(payload, payload.GetType(), IpcSerializer.Options);
		}

		string path = FixturePath("native-payloads.json");
		// Regeneration is an explicit opt-in (like a snapshot --update): the single write path. A *missing*
		// fixture is a failure, never a silent regenerate — otherwise deleting it would quietly disable the
		// guard, and a normal `dotnet test` would write into the source tree.
		if (Environment.GetEnvironmentVariable("UPDATE_CONTRACT_FIXTURE") == "1")
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
			Assert.Pass($"Contract fixture (re)generated at {path}. Commit it and keep the webview decoders in sync.");
			return;
		}

		Assert.That(File.Exists(path), Is.True,
			$"The contract fixture is missing ({path}). Regenerate it with UPDATE_CONTRACT_FIXTURE=1 and commit it.");
		JsonNode? expected = JsonNode.Parse(File.ReadAllText(path));
		Assert.That(expected, Is.Not.Null, $"The contract fixture at {path} is empty or unparseable.");
		Assert.That(JsonNode.DeepEquals(actual, expected), Is.True,
			"Native payloads drifted from the committed contract fixture " +
			$"({path}). If this is an intentional contract change, regenerate the fixture with " +
			"UPDATE_CONTRACT_FIXTURE=1 and update webview/src/decoders.ts + protocol.ts to match.");
	}

	[Test]
	public void StatusPayload_WithNullBranch_OmitsBranchFromTheWire()
	{
		// WhenWritingNull drops nullable payload properties so the webview decoder receives an omitted key.
		JsonNode? node = JsonSerializer.SerializeToNode(
			new StatusPayload("published", "Published", null), IpcSerializer.Options);
		Assert.That(node, Is.Not.Null);
		Assert.That(node!.AsObject().ContainsKey("branch"), Is.False,
			"A null Branch must be omitted from the wire, not serialized as null.");
	}

	[Test]
	public void RepoRefreshAllPayload_IncludesThePositiveRequestIdOnTheWire()
	{
		JsonNode? node = JsonSerializer.SerializeToNode(
			new RepoRefreshAllPayload(42), IpcSerializer.Options);
		Assert.That(node, Is.Not.Null);
		Assert.That(node!["requestId"]?.GetValue<long>(), Is.EqualTo(42));
	}

	[Test]
	public void RepoConfirmationPayload_DeleteCloneOmitsItsNullBranchOnTheWire()
	{
		JsonNode? node = JsonSerializer.SerializeToNode(
			new RepoConfirmationPayload(
				"deleteClone",
				"acme/specs",
				@"C:\SpecDesk\repos\product-specs",
				Branch: null,
				"Delete this local copy from this computer?",
				["There are unfinished local edits."],
				"DD42A087"),
			IpcSerializer.Options);

		Assert.That(node, Is.Not.Null);
		Assert.That(node!["branch"], Is.Null, "A null branch is omitted from the native wire payload.");
	}

	[Test]
	public void WorkspaceContextPayload_WithNullFields_OmitsThemFromTheWire()
	{
		JsonNode? detached = JsonSerializer.SerializeToNode(
			new WorkspaceContextPayload("sample-repo", @"C:\repo", null, "detached", "main", ".spectool.toml"),
			IpcSerializer.Options);
		JsonNode? unavailable = JsonSerializer.SerializeToNode(
			new WorkspaceContextPayload(null, null, null, "unavailable", null, "outside.md"),
			IpcSerializer.Options);

		Assert.Multiple(() =>
		{
			Assert.That(detached, Is.Not.Null);
			Assert.That(detached!.AsObject().ContainsKey("branch"), Is.False);
			Assert.That(unavailable, Is.Not.Null);
			Assert.That(unavailable!.AsObject().ContainsKey("repository"), Is.False);
			Assert.That(unavailable.AsObject().ContainsKey("repositoryRoot"), Is.False);
			Assert.That(unavailable.AsObject().ContainsKey("branch"), Is.False);
			Assert.That(unavailable.AsObject().ContainsKey("defaultBranch"), Is.False);
		});
	}

	[Test]
	public void MessageKinds_MatchTheCommittedWireKindsFixture()
	{
		// The wire kind strings ARE the protocol's public surface, hand-mirrored in webview/src/protocol.ts
		// (the Kinds object). The payload-shape fixture above only pins the 8 kinds that carry a payload;
		// this pins the FULL set (incl. the no-payload webview→native actions) so a renamed / added /
		// removed kind on either side breaks CI rather than silently dropping a message. The webview half
		// asserts Object.values(Kinds) against this same file (webview/tests/contract.test.ts).
		JsonArray actual = [];
		foreach (string kind in typeof(MessageKinds)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
			.Select(f => (string)f.GetRawConstantValue()!)
			.OrderBy(s => s, StringComparer.Ordinal))
		{
			actual.Add(kind);
		}

		string path = FixturePath("wire-kinds.json");
		if (Environment.GetEnvironmentVariable("UPDATE_CONTRACT_FIXTURE") == "1")
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
			Assert.Pass($"Wire-kinds fixture (re)generated at {path}. Commit it and keep protocol.ts in sync.");
			return;
		}

		Assert.That(File.Exists(path), Is.True,
			$"The wire-kinds fixture is missing ({path}). Regenerate it with UPDATE_CONTRACT_FIXTURE=1 and commit it.");
		JsonNode? expected = JsonNode.Parse(File.ReadAllText(path));
		Assert.That(expected, Is.Not.Null, $"The wire-kinds fixture at {path} is empty or unparseable.");
		Assert.That(JsonNode.DeepEquals(actual, expected), Is.True,
			"The wire kinds drifted from the committed fixture " +
			$"({path}). If this is an intentional protocol change, regenerate with UPDATE_CONTRACT_FIXTURE=1 " +
			"and update webview/src/protocol.ts (the Kinds object) to match.");
	}
}
