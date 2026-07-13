using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using SpecDesk.Ai;

namespace SpecDesk.Ai.Tests;

[TestFixture]
public sealed class CopilotChatAgentTests
{
	[Test]
	public async Task SessionConfig_IsStreamingChatOnly_AndRejectsEveryPermissionRequest()
	{
		SessionConfig config = CopilotChatAgent.CreateSessionConfig("gpt-test");
		#pragma warning disable GHCP001 // Assert the SDK 1.0.6 experimental permission result directly.
		PermissionDecision decision = await config.OnPermissionRequest!(null!, null!);
		#pragma warning restore GHCP001

		Assert.Multiple(() =>
		{
			Assert.That(config.Model, Is.EqualTo("gpt-test"));
			Assert.That(config.Streaming, Is.True);
			Assert.That(config.AvailableTools, Is.Empty);
			Assert.That(config.OnPermissionRequest, Is.Not.Null);
			Assert.That(decision, Is.TypeOf<PermissionDecisionReject>());
			Assert.That(config.InfiniteSessions?.Enabled, Is.False);
			Assert.That(config.EnableConfigDiscovery, Is.False);
			Assert.That(config.EnableFileHooks, Is.False);
			Assert.That(config.EnableHostGitOperations, Is.False);
			Assert.That(config.EnableSessionStore, Is.False);
			Assert.That(config.EnableSkills, Is.False);
			Assert.That(config.ManageScheduleEnabled, Is.False);
		});
	}

	[Test]
	public async Task CancelledTurn_WaitsForAbortBeforeTheNextTurnStarts()
	{
		DelayedAbortSession session = new();
		await using CopilotChatAgent agent = new(() => session);
		using CancellationTokenSource firstCancellation = new();

		Task first = ConsumeAsync(agent.StreamAsync("first", firstCancellation.Token));
		await session.FirstSendStarted.Task;
		firstCancellation.Cancel();
		await session.AbortStarted.Task;

		Task second = ConsumeAsync(agent.StreamAsync("second"));
		await Task.Delay(50);
		Assert.That(session.SendCount, Is.EqualTo(1), "the next send must stay behind the unfinished abort");

		session.AllowAbortToFinish.SetResult();
		Assert.CatchAsync<OperationCanceledException>(async () => await first);
		await second;
		Assert.That(session.SendCount, Is.EqualTo(2));
	}

	[Test]
	public async Task NeverCompletingAbort_DetachesTheSession_AndDoesNotBlockTheNextTurnOrDispose()
	{
		NeverCompletingAbortSession firstSession = new();
		IdleSession secondSession = new();
		Queue<ICopilotSessionTransport> sessions = new([firstSession, secondSession]);
		CopilotChatAgent agent = new(
			() => sessions.Dequeue(),
			abortTimeout: TimeSpan.FromMilliseconds(30),
			disposeTimeout: TimeSpan.FromMilliseconds(30));
		using CancellationTokenSource cancellation = new();

		Task first = ConsumeAsync(agent.StreamAsync("first", cancellation.Token));
		await firstSession.SendStarted.Task;
		cancellation.Cancel();
		Assert.CatchAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(1)));

		await ConsumeAsync(agent.StreamAsync("second")).WaitAsync(TimeSpan.FromSeconds(1));
		Assert.That(secondSession.SendCount, Is.EqualTo(1), "the timed-out session must not be reused");
		await agent.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
	}

	private static async Task ConsumeAsync(IAsyncEnumerable<string> stream)
	{
		await foreach (string _ in stream)
		{
		}
	}

	private sealed class DelayedAbortSession : ICopilotSessionTransport
	{
		private Action<SessionEvent>? _handler;
		public TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AbortStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AllowAbortToFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int SendCount { get; private set; }

		public IDisposable On(Action<SessionEvent> handler)
		{
			_handler = handler;
			return new Subscription(() => _handler = null);
		}

		public async Task SendAsync(string message, CancellationToken cancellationToken)
		{
			SendCount++;
			if (SendCount == 1)
			{
				FirstSendStarted.SetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return;
			}

			_handler?.Invoke(new SessionIdleEvent { Data = new SessionIdleData() });
		}

		public async Task AbortAsync(CancellationToken cancellationToken)
		{
			AbortStarted.SetResult();
			await AllowAbortToFinish.Task.WaitAsync(cancellationToken);
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;

		private sealed class Subscription(Action dispose) : IDisposable
		{
			public void Dispose() => dispose();
		}
	}

	private sealed class NeverCompletingAbortSession : ICopilotSessionTransport
	{
		public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IDisposable On(Action<SessionEvent> handler) => new Subscription(static () => { });

		public async Task SendAsync(string message, CancellationToken cancellationToken)
		{
			SendStarted.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		}

		public Task AbortAsync(CancellationToken cancellationToken) => new TaskCompletionSource().Task;

		public ValueTask DisposeAsync() => new(new TaskCompletionSource().Task);
	}

	private sealed class IdleSession : ICopilotSessionTransport
	{
		private Action<SessionEvent>? _handler;
		public int SendCount { get; private set; }

		public IDisposable On(Action<SessionEvent> handler)
		{
			_handler = handler;
			return new Subscription(() => _handler = null);
		}

		public Task SendAsync(string message, CancellationToken cancellationToken)
		{
			SendCount++;
			_handler?.Invoke(new SessionIdleEvent { Data = new SessionIdleData() });
			return Task.CompletedTask;
		}

		public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
