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
				Organizations: ["acme", "octo-labs"])),
		(MessageKinds.GitHubRepositories, new GitHubRepositoriesPayload(
		[
			new GitHubRepositoryOptionPayload("acme/specs", "Product specifications"),
			new GitHubRepositoryOptionPayload("octocat/notes", null),
		])),
		(MessageKinds.RepoCloneDestination, new RepoCloneDestinationPayload(
			"acme/specs", 7, @"C:\SpecDesk\repos\acme_specs")),
		(MessageKinds.RepoDescription, new RepoDescriptionPayload(
			"acme/specs", 8, RepoDescriptionStates.Found, "Product specifications")),
		// AI assistant (PoC-8): a streamed reply chunk, a turn-complete marker, and the prompt library.
		(MessageKinds.ChatDelta, new ChatDeltaPayload("7", "Here is a summary of the change: ")),
		(MessageKinds.ChatDone, new ChatDonePayload("7")),
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
		// The workspace file tree: a folder with a Markdown child plus a top-level file — exercises the
		// recursive node shape (a directory with children, and a leaf file with an empty children list).
		(MessageKinds.Tree, new TreePayload(@"C:\specs\billing-repo",
		[
			new TreeNode("specs", @"C:\specs\billing-repo\specs", IsDirectory: true,
			[
				new TreeNode("billing.md", @"C:\specs\billing-repo\specs\billing.md", IsDirectory: false, []),
			]),
			new TreeNode("README.md", @"C:\specs\billing-repo\README.md", IsDirectory: false, []),
		])),
		(MessageKinds.WorkspaceContext, new WorkspaceContextPayload(
			"billing-repo", @"C:\specs\billing-repo", "spec/billing-refunds", "named", "main", "specs/billing.md")),
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
				[new RegisteredClone("octo-specs", "C:\\specs\\octo-specs", ["review-copy"])]),
		])),
	];

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
		// Branch is the only nullable native→webview field; WhenWritingNull drops it so the webview's
		// optional-branch decoder path stays valid. Pin that here — the decoders never see this null.
		JsonNode? node = JsonSerializer.SerializeToNode(
			new StatusPayload("published", "Published", null), IpcSerializer.Options);
		Assert.That(node, Is.Not.Null);
		Assert.That(node!.AsObject().ContainsKey("branch"), Is.False,
			"A null Branch must be omitted from the wire, not serialized as null.");
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
