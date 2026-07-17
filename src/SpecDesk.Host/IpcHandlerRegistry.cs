using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>
/// The "kind → handler" dispatch table that replaces <see cref="HostController"/>'s former central
/// <c>OnMessage</c> switch. It is built once, at controller construction: each IPC domain (a
/// <c>HostController</c> partial slice) registers its own message kinds from its own file, so adding a
/// new kind to a domain is a local change to that slice and never edits the router.
/// <para>
/// Registration rejects a duplicate kind — the same guarantee the switch gave for free (a C# switch
/// cannot carry two <c>case</c> labels for one value). That keeps two slices from both claiming a kind
/// and silently shadowing one another, which is exactly how a refactor like this could otherwise drop
/// or misroute a message. Lookup is ordinal, matching switch-on-string semantics, so no kind's routing
/// shifts under a culture with surprising string comparison.
/// </para>
/// </summary>
internal sealed class IpcHandlerRegistry
{
	private readonly Dictionary<string, Action<IpcMessage>> _handlers = new(StringComparer.Ordinal);

	/// <summary>Registers the <paramref name="handler"/> for <paramref name="kind"/>.</summary>
	/// <exception cref="InvalidOperationException">The kind already has a handler.</exception>
	public void Register(string kind, Action<IpcMessage> handler)
	{
		ArgumentException.ThrowIfNullOrEmpty(kind);
		ArgumentNullException.ThrowIfNull(handler);
		if (!_handlers.TryAdd(kind, handler))
		{
			throw new InvalidOperationException(
				$"An IPC handler for kind '{kind}' is already registered; each kind must map to exactly one handler.");
		}
	}

	/// <summary>
	/// Registers a payload-agnostic <paramref name="handler"/> — a kind whose handler ignores the
	/// envelope (e.g. <c>ready</c>, <c>doc.save</c>). A thin adapter over the message-taking overload.
	/// </summary>
	/// <exception cref="InvalidOperationException">The kind already has a handler.</exception>
	public void Register(string kind, Action handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		Register(kind, _ => handler());
	}

	/// <summary>
	/// Looks up the handler for <paramref name="kind"/>. Returns <c>false</c> for an unregistered kind,
	/// so the router can fall back to the same "ignore unknown kind" path the switch's default carried.
	/// </summary>
	public bool TryGetHandler(string kind, out Action<IpcMessage> handler) =>
		_handlers.TryGetValue(kind, out handler!);
}
