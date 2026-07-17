using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

/// <summary>
/// Locks the dispatch-table mechanism that replaced <c>HostController</c>'s central OnMessage switch
/// (T-070). The refactor's risk is a silently dropped or double-claimed kind, so these guard the two
/// invariants the switch gave for free: a kind routes to exactly the handler registered for it, and a
/// second registration of the same kind is rejected rather than shadowing the first.
/// </summary>
[TestFixture]
public sealed class IpcHandlerRegistryTests
{
	private static IpcMessage Frame(string kind) => new(kind);

	[Test]
	public void Register_ThenTryGetHandler_RoutesToTheRegisteredHandlerWithTheEnvelope()
	{
		IpcHandlerRegistry registry = new();
		IpcMessage? seen = null;
		registry.Register("domain.a", message => seen = message);

		Assert.That(registry.TryGetHandler("domain.a", out Action<IpcMessage> handler), Is.True);
		IpcMessage frame = Frame("domain.a");
		handler(frame);
		Assert.That(seen, Is.SameAs(frame), "the handler must receive the dispatched envelope");
	}

	[Test]
	public void Register_PayloadAgnosticOverload_InvokesTheZeroArgHandler()
	{
		IpcHandlerRegistry registry = new();
		int calls = 0;
		registry.Register("domain.ping", () => calls++);

		Assert.That(registry.TryGetHandler("domain.ping", out Action<IpcMessage> handler), Is.True);
		handler(Frame("domain.ping"));
		Assert.That(calls, Is.EqualTo(1));
	}

	[Test]
	public void Register_DuplicateKind_ThrowsSoNoKindCanBeSilentlyShadowed()
	{
		IpcHandlerRegistry registry = new();
		registry.Register("domain.dup", _ => { });

		Assert.Multiple(() =>
		{
			Assert.Throws<InvalidOperationException>(() => registry.Register("domain.dup", _ => { }));
			// The no-arg overload funnels through the same guard, so it is rejected just the same.
			Assert.Throws<InvalidOperationException>(() => registry.Register("domain.dup", () => { }));
		});
	}

	[Test]
	public void TryGetHandler_UnknownKind_ReturnsFalseSoTheRouterCanIgnoreIt()
	{
		IpcHandlerRegistry registry = new();
		registry.Register("domain.known", _ => { });

		Assert.That(registry.TryGetHandler("domain.unknown", out Action<IpcMessage> handler), Is.False);
		Assert.That(handler, Is.Null);
	}

	[Test]
	public void TryGetHandler_MatchesKindOrdinally_LikeTheSwitchItReplaced()
	{
		IpcHandlerRegistry registry = new();
		registry.Register("domain.Case", _ => { });

		Assert.Multiple(() =>
		{
			Assert.That(registry.TryGetHandler("domain.Case", out _), Is.True);
			// A case-only variant is a different kind, exactly as a switch-on-string case label would be.
			Assert.That(registry.TryGetHandler("domain.case", out _), Is.False);
		});
	}
}
