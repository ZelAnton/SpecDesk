using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerActivityTests
{
	private sealed class RemoteCatalog : IGitHubRepositoryCatalog
	{
		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("main"));

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitHubRepositoryEntry>>([]);

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => Task.FromResult("# Remote");
	}

	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	[Test]
	public void ActivityRequest_ReturnsSelectedDocumentVersionsDistinctHistoryAndHonestEmptyComments()
	{
		string root = Path.Combine(Path.GetTempPath(), $"specdesk-activity-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		string document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		try
		{
			FakeVersioning versioning = new()
			{
				DocumentVersions =
				[
					new DocumentVersion(
						"abc", "Clarify refunds", "Alex", DateTimeOffset.UnixEpoch, "Document updated"),
				],
			};
			List<string> sent = [];
			void Send(string json)
			{
				lock (sent)
				{
					sent.Add(json);
				}
			}
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				Send,
				new NoDialogs(),
				(_, _, _, _, _) => null,
				versioning,
				NullLogger<HostController>.Instance,
				initialDocPath: document);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
				MessageKinds.DocumentActivityRequest, Id: "activity-1")));

			IpcMessage? reply = WaitFor(sent, MessageKinds.DocumentActivity);
			DocumentActivityPayload? payload = reply?.GetPayload<DocumentActivityPayload>();
			Assert.Multiple(() =>
			{
				Assert.That(reply?.Id, Is.EqualTo("activity-1"));
				Assert.That(payload?.Document, Is.EqualTo("billing.md"));
				Assert.That(payload?.Versions.Single().Note, Is.EqualTo("Clarify refunds"));
				Assert.That(payload?.History.Single().Label, Is.EqualTo("Document updated"));
				Assert.That(payload?.History.Single().Note, Is.EqualTo("Clarify refunds"));
				Assert.That(payload?.HistoryState, Is.EqualTo("loaded"));
				Assert.That(payload?.Comments, Is.Empty);
				Assert.That(payload?.CommentsState, Is.EqualTo("loaded"));
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void ActivityRequest_WhenSignedOut_ReportsCommentsAsNotConnected()
	{
		DocumentActivityPayload payload = LoadCommentsActivity(
			new FakeGitHubAuth(signedIn: false), new FakeGitHubReview());
		Assert.Multiple(() =>
		{
			Assert.That(payload.CommentsState, Is.EqualTo("notConnected"));
			Assert.That(payload.CommentsMessage, Does.Contain("Connect to GitHub"));
		});
	}

	[Test]
	public void ActivityRequest_WhenUnversionedRepositoryHasNoGitHubRemote_ReportsVerifiedEmptyComments()
	{
		FakeVersioning? configuredVersioning = null;
		DocumentActivityPayload payload = LoadCommentsActivity(
			new FakeGitHubAuth(signedIn: false),
			new FakeGitHubReview(),
			versioning =>
			{
				configuredVersioning = versioning;
				versioning.Versioned = false;
				versioning.RemoteUrlValue = null;
			});
		Assert.Multiple(() =>
		{
			Assert.That(payload.HistoryState, Is.EqualTo("notVersioned"));
			Assert.That(payload.HistoryMessage, Does.Contain("repository documents"));
			Assert.That(configuredVersioning?.GetDocumentVersionsCalls, Is.Zero);
			Assert.That(payload.CommentsState, Is.EqualTo("loaded"));
			Assert.That(payload.CommentsMessage, Is.Null);
			Assert.That(payload.Comments, Is.Empty);
		});
	}

	[Test]
	public void ActivityRequest_WhenLocalHistoryFails_StillLoadsGitHubComments()
	{
		FakeGitHubReview review = new()
		{
			ReviewStatusValue = new ReviewStatus(ReviewDecision.InReview, 42, PullRequestState.Open),
			CommentsValue =
			[
				new ReviewComment(
					"1", "billing.md", "reviewer", "Please clarify", DateTimeOffset.UnixEpoch),
			],
		};
		DocumentActivityPayload payload = LoadCommentsActivity(
			new FakeGitHubAuth(signedIn: true),
			review,
			versioning => versioning.ThrowOnGetDocumentVersions = true);
		Assert.Multiple(() =>
		{
			Assert.That(payload.HistoryState, Is.EqualTo("unavailable"));
			Assert.That(payload.HistoryMessage, Does.Contain("Could not load saved history"));
			Assert.That(payload.Versions, Is.Empty);
			Assert.That(payload.History, Is.Empty);
			Assert.That(payload.CommentsState, Is.EqualTo("loaded"));
			Assert.That(payload.Comments.Single().Body, Is.EqualTo("Please clarify"));
		});
	}

	[Test]
	public void ActivityRequest_WhenBranchHasNoReview_ReportsVerifiedEmptyComments()
	{
		DocumentActivityPayload payload = LoadCommentsActivity(
			new FakeGitHubAuth(signedIn: true), new FakeGitHubReview { ReviewStatusValue = null });
		Assert.Multiple(() =>
		{
			Assert.That(payload.CommentsState, Is.EqualTo("loaded"));
			Assert.That(payload.Comments, Is.Empty);
		});
	}

	[Test]
	public void ActivityRequest_WhenCommentsApiFails_PreservesVersionsAndReportsUnavailableComments()
	{
		FakeGitHubReview review = new()
		{
			ReviewStatusValue = new ReviewStatus(ReviewDecision.InReview, 42, PullRequestState.Open),
			ThrowOnListReviewComments = true,
		};
		DocumentActivityPayload payload = LoadCommentsActivity(
			new FakeGitHubAuth(signedIn: true), review);
		Assert.Multiple(() =>
		{
			Assert.That(payload.CommentsState, Is.EqualTo("unavailable"));
			Assert.That(payload.CommentsMessage, Does.Contain("Could not load"));
			Assert.That(payload.Versions, Has.Count.EqualTo(1));
		});
	}

	[Test]
	public void ActivityRead_WaitsForAnInFlightRepositoryMutation()
	{
		string root = Path.Combine(Path.GetTempPath(), $"specdesk-activity-gate-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		string document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		try
		{
			using ManualResetEventSlim saveGate = new(false);
			FakeVersioning versioning = new() { SaveGate = saveGate };
			List<string> sent = [];
			void Send(string json)
			{
				lock (sent)
				{
					sent.Add(json);
				}
			}
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []), Send, new NoDialogs(),
				(_, _, _, _, _) => null, versioning, NullLogger<HostController>.Instance,
				initialDocPath: document);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocEdit, new EditPayload("spec/billing")));

			Task save = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocSaveVersion, new SaveVersionPayload("Save"))));
			Assert.That(SpinWait.SpinUntil(() => versioning.SaveVersionCalls == 1, 2_000), Is.True);
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
				MessageKinds.DocumentActivityRequest, Id: "during-save")));
			Thread.Sleep(100);
			Assert.That(versioning.GetDocumentVersionsCalls, Is.Zero,
				"activity read entered versioning while SaveVersion held the repository gate");

			saveGate.Set();
			Assert.That(save.Wait(2_000), Is.True);
			Assert.That(WaitFor(sent, MessageKinds.DocumentActivity), Is.Not.Null);
			Assert.That(versioning.GetDocumentVersionsCalls, Is.EqualTo(1));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void ActivityRequest_LoadsBoundedInlineCommentsForTheExactSelectedGitPath()
	{
		string root = Path.Combine(Path.GetTempPath(), $"specdesk-comments-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		string document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		try
		{
			FakeVersioning versioning = new() { Branch = "spec/billing" };
			FakeGitHubReview review = new()
			{
				ReviewStatusValue = new ReviewStatus(ReviewDecision.InReview, 42, PullRequestState.Open),
				CommentsValue =
				[
					new ReviewComment("1", "billing.md", "reviewer", "Please clarify", DateTimeOffset.UnixEpoch),
					new ReviewComment("2", "Billing.md", "other", "Wrong case", DateTimeOffset.UnixEpoch),
				],
			};
			List<string> sent = [];
			void Send(string json)
			{
				lock (sent)
				{
					sent.Add(json);
				}
			}
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []), Send, new NoDialogs(),
				(_, _, _, _, _) => null, versioning, NullLogger<HostController>.Instance,
				initialDocPath: document,
				auth: new FakeGitHubAuth(signedIn: true),
				publishing: versioning,
				review: review);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
				MessageKinds.DocumentActivityRequest, Id: "comments")));

			DocumentActivityPayload? payload = WaitFor(sent, MessageKinds.DocumentActivity)
				?.GetPayload<DocumentActivityPayload>();
			Assert.Multiple(() =>
			{
				Assert.That(review.ListReviewCommentsCalls, Is.EqualTo(1));
				Assert.That(payload?.Comments, Has.Count.EqualTo(1));
				Assert.That(payload?.Comments[0].Body, Is.EqualTo("Please clarify"));
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemotePreview_PublishesContextAndLoadsActivityForTheExactOnlineDocument()
	{
		string root = Path.Combine(Path.GetTempPath(), $"specdesk-remote-activity-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			FakeGitHubReview review = new()
			{
				ReviewStatusValue = new ReviewStatus(ReviewDecision.InReview, 42, PullRequestState.Open),
				CommentsValue =
				[
					new ReviewComment(
						"1", "Docs/Guide.md", "reviewer", "Please clarify", DateTimeOffset.UnixEpoch),
				],
			};
			List<string> sent = [];
			void Send(string json)
			{
				lock (sent)
				{
					sent.Add(json);
				}
			}
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				Send,
				new NoDialogs(),
				(_, _, _, _, _) => null,
				new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(signedIn: true),
				review: review,
				workspace: store,
				repositoryCatalog: new RemoteCatalog());

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen,
				new DocOpenPayload("github://octo/specs/feature%2Fdocs/Docs%2FGuide.md")));
			Assert.That(WaitFor(sent, MessageKinds.DocLoaded), Is.Not.Null);
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
				MessageKinds.DocumentActivityRequest, Id: "remote-activity")));

			WorkspaceContextPayload? context = WaitFor(sent, MessageKinds.WorkspaceContext)
				?.GetPayload<WorkspaceContextPayload>();
			DocumentActivityPayload? activity = WaitFor(sent, MessageKinds.DocumentActivity)
				?.GetPayload<DocumentActivityPayload>();
			Assert.Multiple(() =>
			{
				Assert.That(context?.Repository, Is.EqualTo("octo/specs"));
				Assert.That(context?.RepositoryRoot, Is.Null);
				Assert.That(context?.Branch, Is.EqualTo("feature/docs"));
				Assert.That(context?.BranchState, Is.EqualTo("named"));
				Assert.That(context?.DefaultBranch, Is.EqualTo("main"));
				Assert.That(context?.Path, Is.EqualTo("Docs/Guide.md"));
				Assert.That(activity?.Document, Is.EqualTo("Guide.md"));
				Assert.That(activity?.HistoryState, Is.EqualTo("notVersioned"));
				Assert.That(activity?.Comments.Single().Body, Is.EqualTo("Please clarify"));
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static IpcMessage? WaitFor(List<string> sent, string kind)
	{
		for (int i = 0; i < 100; i++)
		{
			lock (sent)
			{
				IpcMessage? found = sent.Select(IpcSerializer.TryDeserialize)
					.LastOrDefault(message => message?.Kind == kind);
				if (found is not null) return found;
			}
			Thread.Sleep(10);
		}
		return null;
	}

	private static DocumentActivityPayload LoadCommentsActivity(
		IGitHubAuth auth, FakeGitHubReview review, Action<FakeVersioning>? configure = null)
	{
		string root = Path.Combine(Path.GetTempPath(), $"specdesk-comment-state-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		string document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		try
		{
			FakeVersioning versioning = new()
			{
				Branch = "spec/billing",
				DocumentVersions =
				[
					new DocumentVersion("abc", "Saved", "Alex", DateTimeOffset.UnixEpoch),
				],
			};
			configure?.Invoke(versioning);
			List<string> sent = [];
			void Send(string json)
			{
				lock (sent)
				{
					sent.Add(json);
				}
			}
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []), Send, new NoDialogs(),
				(_, _, _, _, _) => null, versioning, NullLogger<HostController>.Instance,
				initialDocPath: document, auth: auth, publishing: versioning, review: review);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
				MessageKinds.DocumentActivityRequest, Id: "state")));
			return WaitFor(sent, MessageKinds.DocumentActivity)!.GetPayload<DocumentActivityPayload>()!;
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
