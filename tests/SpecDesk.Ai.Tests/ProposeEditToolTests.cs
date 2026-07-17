using SpecDesk.Ai;

namespace SpecDesk.Ai.Tests;

// The assistant's single gated mutating tool (proposeEdit): it can ONLY stage a proposal on its sink and
// never applies an edit itself — the structural half of docs/design/08-ai-agent.md's hard safety rule.
[TestFixture]
public sealed class ProposeEditToolTests
{
	private sealed class RecordingSink : IEditProposalSink
	{
		public List<EditProposal> Staged { get; } = [];

		public EditProposalStatus Result { get; set; } = EditProposalStatus.Staged;

		public EditProposalStatus Stage(EditProposal proposal)
		{
			Staged.Add(proposal);
			return Result;
		}
	}

	[Test]
	public void Propose_StagesTheProposalOnTheSink_AndReturnsItsStatus()
	{
		RecordingSink sink = new();
		ProposeEditTool tool = new(sink);

		EditProposalStatus status = tool.Propose("# New body\n", "Rewrite the intro");

		Assert.Multiple(() =>
		{
			Assert.That(status, Is.EqualTo(EditProposalStatus.Staged));
			Assert.That(sink.Staged, Has.Count.EqualTo(1));
			Assert.That(sink.Staged[0].ProposedText, Is.EqualTo("# New body\n"));
			Assert.That(sink.Staged[0].Summary, Is.EqualTo("Rewrite the intro"));
		});
	}

	[Test]
	public void Propose_RelaysAnUnavailableStagingResult()
	{
		RecordingSink sink = new() { Result = EditProposalStatus.Unavailable };
		ProposeEditTool tool = new(sink);

		Assert.That(tool.Propose("# Body\n"), Is.EqualTo(EditProposalStatus.Unavailable));
	}

	[TestCase("")]
	[TestCase(null)]
	public void Propose_WithEmptyText_IsUnavailableAndStagesNothing(string? proposedText)
	{
		RecordingSink sink = new();
		ProposeEditTool tool = new(sink);

		EditProposalStatus status = tool.Propose(proposedText!);

		Assert.Multiple(() =>
		{
			Assert.That(status, Is.EqualTo(EditProposalStatus.Unavailable));
			Assert.That(sink.Staged, Is.Empty);
		});
	}

	[TestCase("   ", null)]
	[TestCase("  trim me  ", "trim me")]
	public void Propose_NormalizesABlankOrPaddedSummaryToNullOrTrimmed(string summary, string? expected)
	{
		RecordingSink sink = new();
		ProposeEditTool tool = new(sink);

		tool.Propose("# Body\n", summary);

		Assert.That(sink.Staged[0].Summary, Is.EqualTo(expected));
	}

	[Test]
	public void Constructor_RejectsANullSink()
	{
		Assert.That(() => new ProposeEditTool(null!), Throws.ArgumentNullException);
	}

	[Test]
	public void ProposeEditTool_ExposesNoMutatingSurface_OnlyPropose()
	{
		// The proposeEdit name is deliberately NOT part of the read-only allowlist (it is the gated mutating
		// tool), and the tool's only public verb is Propose — there is no apply/commit/push affordance on it.
		Assert.Multiple(() =>
		{
			Assert.That(ProposeEditTool.Name, Is.EqualTo("proposeEdit"));
			Assert.That(AiReadOnlyTools.Allowlist, Does.Not.Contain(ProposeEditTool.Name));
			string[] publicMethods = typeof(ProposeEditTool)
				.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
				.Where(method => method.DeclaringType == typeof(ProposeEditTool))
				.Select(method => method.Name)
				.ToArray();
			Assert.That(publicMethods, Has.Length.EqualTo(1));
			Assert.That(publicMethods, Has.Member("Propose"));
		});
	}
}
