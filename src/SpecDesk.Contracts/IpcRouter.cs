namespace SpecDesk.Contracts;

/// <summary>
/// Pure, transport-agnostic dispatcher for <see cref="IpcMessage"/> envelopes. Handlers are
/// registered by <c>kind</c>; <see cref="Handle"/> turns an incoming wire string into an
/// optional reply wire string. No threading and no I/O — the host owns the transport, so this
/// is fully unit-testable without Photino.
/// </summary>
public sealed class IpcRouter
{
	private readonly Dictionary<string, Func<IpcMessage, IpcMessage?>> _handlers =
		new(StringComparer.Ordinal);

	/// <summary>
	/// Register a handler for a message <paramref name="kind"/>. The handler returns the reply
	/// to send back, or <c>null</c> for a fire-and-forget message. Re-registering a kind
	/// replaces the previous handler.
	/// </summary>
	public IpcRouter Register(string kind, Func<IpcMessage, IpcMessage?> handler)
	{
		ArgumentException.ThrowIfNullOrEmpty(kind);
		ArgumentNullException.ThrowIfNull(handler);
		_handlers[kind] = handler;
		return this;
	}

	/// <summary>
	/// Route an incoming wire string and return the reply wire string, or <c>null</c> when the
	/// input is not a valid envelope, no handler is registered for its kind, or the handler
	/// produced no reply.
	/// </summary>
	public string? Handle(string json)
	{
		IpcMessage? message = IpcSerializer.TryDeserialize(json);
		if (message is null || !_handlers.TryGetValue(message.Kind, out Func<IpcMessage, IpcMessage?>? handler))
		{
			return null;
		}

		IpcMessage? reply = handler(message);
		return reply is null ? null : IpcSerializer.Serialize(reply);
	}
}
