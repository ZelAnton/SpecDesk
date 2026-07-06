using System.Net.Http.Headers;
using System.Text.Json;

namespace SpecDesk.GitHub;

/// <summary>
/// Shared HTTP conventions for the hand-rolled BCL-only GitHub transports (device flow and review): the
/// per-request timeout, the User-Agent GitHub's REST/GraphQL APIs require, the linked-CancellationTokenSource
/// pattern that applies the timeout without mutating a (possibly shared) injected <see cref="HttpClient"/>,
/// and the safe JSON-field readers both transports parse GitHub's responses with. Kept internal — this is
/// transport plumbing, not part of the project's public surface.
/// </summary>
internal static class GitHubHttp
{
    // A single request's wall-clock budget — well under HttpClient's 100s default so a stalled request is
    // detected promptly. Applied via a linked CancellationTokenSource (see NewTimeout) so a (possibly shared)
    // injected HttpClient is never mutated. A stall then maps to a retryable outcome, bounded by the caller's
    // own loop where one exists.
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // GitHub's REST API rejects requests without a User-Agent; this identifies the app (any value is fine).
    public static readonly ProductInfoHeaderValue UserAgent = new("SpecDesk", "1.0");

    /// <summary>A <see cref="CancellationTokenSource"/> linked to <paramref name="ct"/> with
    /// <see cref="RequestTimeout"/> already armed — the shared per-request-timeout pattern used by every
    /// GitHub call. Dispose it ("using") once the request completes.</summary>
    public static CancellationTokenSource NewTimeout(CancellationToken ct)
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        return timeout;
    }

    /// <summary>The named field's JSON-string value, or <see cref="string.Empty"/> when <paramref name="root"/>
    /// isn't an object, the field is absent, or its kind isn't string.</summary>
    public static string StringOf(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>The named field's JSON-number value as an <see cref="int"/>, or 0 when <paramref name="root"/>
    /// isn't an object, the field is absent, or its kind/range isn't a valid <see cref="int"/>.</summary>
    public static int NumberOf(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out int value)
            ? value
            : 0;

    /// <summary>The element's value when it is a JSON string, else <c>null</c> — so a malformed response
    /// with a non-string field (a number, bool, object…) degrades to "no usable value" instead of throwing
    /// from <see cref="JsonElement.GetString"/> (which requires String or Null).</summary>
    public static string? StringValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
