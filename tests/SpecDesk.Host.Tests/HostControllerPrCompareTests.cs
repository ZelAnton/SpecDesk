using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

// PoC-7 Part C: the two IPC handlers behind "in-flight PR awareness & comparison" over the faked network —
//   • pr.forFile — the open PRs whose changed-file set includes the current document.
//   • pr.compare.request — a read-only comparison of a chosen PR's version against the working copy / main,
//     replied to as pr.compare.rendered.
[TestFixture]
public sealed class HostControllerPrCompareTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private string _tempDir = string.Empty;
	private string _docPath = string.Empty;
	private readonly List<string> _sent = [];
	private readonly object _gate = new();

	[SetUp]
	public void SetUp()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "specdesk-prcompare-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
		_docPath = Path.Combine(_tempDir, "billing.md");
		File.WriteAllText(_docPath, "# Billing");
		lock (_gate)
		{
			_sent.Clear();
		}
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_tempDir))
		{
			Directory.Delete(_tempDir, recursive: true);
		}
	}

	private HostController Build(FakeVersioning versioning, FakeGitHubReview review)
	{
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}

		// The real Markdig renderer (not a stub) so the rendered comparison exercises the data-line ↔ line-map
		// annotation path end to end.
		HostController controller = new(
			Renderer.render, Send, new NoDialogs(), (_, _, _, _, _) => null,
			versioning, NullLogger<HostController>.Instance, _docPath,
			auth: new FakeGitHubAuth(signedIn: true), publishing: versioning, review: review);
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		return controller;
	}

	[Test]
	public void PrForFile_replies_with_the_pull_requests_touching_the_current_file()
	{
		FakeVersioning versioning = new();
		FakeGitHubReview review = new()
		{
			PullRequestsForFileValue =
			[
				new PullRequestForFile(51, "Tighten the refund wording", "https://github.com/octo/spec-repo/pull/51"),
			],
		};
		using HostController controller = Build(versioning, review);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrForFile, new PrForFileRequestPayload("billing.md"), id: "q1"));

		PrForFilePayload? payload = WaitForKind(MessageKinds.PrForFile)?.GetPayload<PrForFilePayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(payload!.Error, Is.Null);
			Assert.That(payload.Path, Is.EqualTo("billing.md"));
			Assert.That(payload.Items, Has.Count.EqualTo(1));
			Assert.That(payload.Items[0].Number, Is.EqualTo(51));
			Assert.That(payload.Items[0].Repo, Is.EqualTo("octo/spec-repo"));
			// The host resolves the repository-relative path from its own state, not the payload hint (K-010).
			Assert.That(review.ForFilePath, Is.EqualTo("billing.md"));
		});
	}

	[Test]
	public void PrCompare_workingCopy_raw_marks_the_prs_added_paragraph()
	{
		FakeVersioning versioning = new();
		FakeGitHubReview review = new() { HeadFileContentValue = "# Billing\n\nNew paragraph from the PR.\n" };
		using HostController controller = Build(versioning, review);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrCompareRequest,
			new PrCompareRequestPayload(51, PrCompareBases.WorkingCopy, PrCompareModes.Raw),
			id: "c1"));

		PrComparePayload? payload = WaitForKind(MessageKinds.PrCompareRendered)?.GetPayload<PrComparePayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(payload!.Error, Is.Null);
			Assert.That(payload.Base, Is.EqualTo(PrCompareBases.WorkingCopy));
			Assert.That(payload.Mode, Is.EqualTo(PrCompareModes.Raw));
			Assert.That(payload.Html, Does.Contain("cmp-added"));
			Assert.That(payload.Html, Does.Contain("New paragraph from the PR."));
			Assert.That(review.ReadHeadFileQuery, Is.EqualTo((51, "billing.md")));
		});
	}

	[Test]
	public void PrCompare_main_rendered_uses_the_local_main_blob_as_the_base()
	{
		FakeVersioning versioning = new();
		versioning.BranchContent["main"] = "# Billing\n";
		FakeGitHubReview review = new() { HeadFileContentValue = "# Billing\n\nAdded against main.\n" };
		using HostController controller = Build(versioning, review);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrCompareRequest,
			new PrCompareRequestPayload(51, PrCompareBases.Main, PrCompareModes.Rendered),
			id: "c2"));

		PrComparePayload? payload = WaitForKind(MessageKinds.PrCompareRendered)?.GetPayload<PrComparePayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(payload!.Error, Is.Null);
			Assert.That(payload.Base, Is.EqualTo(PrCompareBases.Main));
			Assert.That(payload.Html, Does.Contain("data-diff=\"added\""));
			// The base came from the local main-branch blob, not HEAD.
			Assert.That(versioning.LastBranchContentQuery, Is.EqualTo(("main", "billing.md")));
		});
	}

	[Test]
	public void PrCompare_whenTheFileIsGoneAtHead_repliesWithAPlainReason()
	{
		FakeVersioning versioning = new();
		FakeGitHubReview review = new() { HeadFileContentValue = null };
		using HostController controller = Build(versioning, review);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrCompareRequest,
			new PrCompareRequestPayload(51, PrCompareBases.WorkingCopy, PrCompareModes.Rendered),
			id: "c3"));

		PrComparePayload? payload = WaitForKind(MessageKinds.PrCompareRendered)?.GetPayload<PrComparePayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(payload!.Html, Is.Empty);
			Assert.That(payload.Error, Is.Not.Null.And.Not.Empty);
		});
	}

	[Test]
	public void PrCompare_rejectsAnInvalidBaseWithoutHittingTheNetwork()
	{
		FakeVersioning versioning = new();
		FakeGitHubReview review = new();
		using HostController controller = Build(versioning, review);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrCompareRequest,
			new PrCompareRequestPayload(51, "sideways", PrCompareModes.Rendered),
			id: "c4"));

		PrComparePayload? payload = WaitForKind(MessageKinds.PrCompareRendered)?.GetPayload<PrComparePayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(payload!.Error, Is.Not.Null.And.Not.Empty);
			Assert.That(review.ReadHeadFileCalls, Is.EqualTo(0));
		});
	}

	private IpcMessage? WaitForKind(string kind)
	{
		for (int attempt = 0; attempt < 200; attempt++)
		{
			lock (_gate)
			{
				foreach (string json in _sent)
				{
					IpcMessage? message = IpcSerializer.TryDeserialize(json);
					if (message is not null && message.Kind == kind)
					{
						return message;
					}
				}
			}

			Thread.Sleep(20);
		}

		return null;
	}
}
