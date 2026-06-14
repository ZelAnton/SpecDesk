using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpecDesk.Contracts;

/// <summary>
/// The single JSON envelope exchanged in both directions between the native host and the
/// webview. See docs/design/09-ipc-protocol.md.
/// </summary>
/// <param name="Kind">Dotted message name (namespace.action).</param>
/// <param name="Id">Correlation id; present only when a reply is expected.</param>
/// <param name="Version">Monotonic editor-content counter; lets a receiver drop stale work.</param>
/// <param name="Payload">Message-specific object, carried verbatim as JSON.</param>
public sealed record IpcMessage(
	string Kind,
	string? Id = null,
	long? Version = null,
	JsonElement? Payload = null);

/// <summary>
/// (De)serialization for <see cref="IpcMessage"/> using the wire conventions: camelCase
/// property names and null <c>id</c>/<c>version</c> omitted from the output.
/// </summary>
public static class IpcSerializer
{
	/// <summary>Shared options — the single definition of the wire shape.</summary>
	public static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>Serialize a message to its wire string.</summary>
	public static string Serialize(IpcMessage message) =>
		JsonSerializer.Serialize(message, Options);

	/// <summary>
	/// Deserialize a wire string into an <see cref="IpcMessage"/>, returning <c>null</c> when
	/// the input is not a valid envelope (malformed JSON, or no <c>kind</c>).
	/// </summary>
	public static IpcMessage? TryDeserialize(string json)
	{
		try
		{
			IpcMessage? message = JsonSerializer.Deserialize<IpcMessage>(json, Options);
			return message is null || string.IsNullOrEmpty(message.Kind) ? null : message;
		}
		catch (JsonException)
		{
			// Malformed input from the webview is expected, not exceptional — treat as "no message".
			return null;
		}
	}
}
