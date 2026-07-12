using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;

namespace SpecDesk.Ai;

/// <summary>
/// Fetches the shared/remote prompt-template library from a configured URL (a JSON array of
/// <see cref="PromptTemplate"/>). Deliberately best-effort: any failure — no URL configured, a network
/// error, a non-success status, a timeout, or malformed JSON — yields an empty list rather than an error,
/// so the assistant's template picker degrades to just the personal library and never blocks on the network.
/// </summary>
public sealed class RemoteTemplateSource
{
	// Bound the fetch so a slow/hung endpoint can't stall the templates reply (which the webview awaits with
	// its own 30s IPC timeout). Kept well under that so a slow-but-failing fetch still answers in time.
	private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	private readonly HttpClient _http;
	private readonly Uri? _url;
	private readonly ILogger _logger;

	public RemoteTemplateSource(HttpClient http, Uri? url, ILogger<RemoteTemplateSource>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(http);
		_http = http;
		_url = url;
		_logger = logger ?? NullLogger<RemoteTemplateSource>.Instance;
	}

	/// <summary>Whether a remote URL is configured at all (no URL → the remote library is always empty).</summary>
	public bool IsConfigured => _url is not null;

	/// <summary>Fetch the remote templates, or an empty list on any failure (logged, never thrown).</summary>
	public async Task<IReadOnlyList<PromptTemplate>> FetchAsync(CancellationToken cancellationToken = default)
	{
		if (_url is null)
		{
			return [];
		}

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(FetchTimeout);

		try
		{
			PromptTemplate[]? loaded = await _http.GetFromJsonAsync<PromptTemplate[]>(
				_url, SerializerOptions, timeout.Token);
			return loaded is null
				? []
				: Array.FindAll(loaded, t => t is { Id.Length: > 0, Title.Length: > 0 });
		}
		catch (Exception ex) when (
			ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException
			&& !cancellationToken.IsCancellationRequested)
		{
			// A transport error, a bad payload, or the fetch timeout: the remote library is simply empty this
			// time. Not surfaced to the author — the personal library still works. (Genuine caller
			// cancellation, e.g. app teardown, is re-thrown by the filter above rather than swallowed here.)
			_logger.LogWarning(ex, "Could not fetch the remote prompt-template library");
			return [];
		}
	}
}
