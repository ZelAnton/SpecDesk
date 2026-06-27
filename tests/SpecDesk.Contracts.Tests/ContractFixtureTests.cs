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
/// CI in that language. Regenerate after an intentional contract change with
/// <c>UPDATE_CONTRACT_FIXTURE=1</c> and update the decoders/protocol to match.
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
		(MessageKinds.DiffResult, new DiffResultPayload(
		[
			// A changed plain block carries its base rendered text and base raw source for inline word-diff.
			new DiffEntryPayload("changed", 2, 2, -1, "", [], "The refund window is 14 days.",
				"The refund window is 14 days."),
			// A changed container (list/table) carries per-child entries: a changed item and a removed item.
			new DiffEntryPayload("changed", 4, 6, -1, "",
			[
				new ChildDiffPayload("changed", 1, -1, "", "Net 30"),
				new ChildDiffPayload("removed", -1, 2, "Legacy clause", ""),
			], "", ""),
			// A removed top-level block: not in the head, so it anchors before a head line and carries its source.
			new DiffEntryPayload("removed", 0, 0, 8, "Deprecated section", [], "", ""),
		])),
	];

	private static string FixturePath()
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
		return Path.Combine(dir.FullName, "webview", "tests", "contract", "native-payloads.json");
	}

	[Test]
	public void NativePayloads_MatchTheCommittedContractFixture()
	{
		JsonObject actual = [];
		foreach ((string kind, object payload) in NativePayloads)
		{
			actual[kind] = JsonSerializer.SerializeToNode(payload, payload.GetType(), IpcSerializer.Options);
		}

		string path = FixturePath();
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
}
