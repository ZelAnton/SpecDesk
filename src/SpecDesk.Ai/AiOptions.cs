namespace SpecDesk.Ai;

/// <summary>
/// The AI assistant's configuration surface (docs/design/08-ai-agent.md's <c>[ai]</c> block). The design
/// sources these from the app's TOML settings; this scaffold reads them from the environment — the same
/// mechanism the rest of the host uses for optional config (<c>SPECDESK_DATA_ROOT</c>, the GitHub client
/// id) — so a TOML loader can replace <see cref="FromEnvironment"/> later without touching consumers.
/// </summary>
/// <param name="Provider">The intended provider name (<c>claude</c> / <c>openai</c> / <c>azure-openai</c> /
/// …), or <c>offline</c> for the built-in stub. Informational in this scaffold: the chat always runs on the
/// offline <see cref="EchoChatClient"/> until a real provider client is wired behind the same seam.</param>
/// <param name="Model">The provider-specific model id (informational until a real provider is wired).</param>
/// <param name="RemoteTemplatesUrl">The URL of the remote prompt-template library, or <c>null</c> when
/// none is configured (then the remote library is simply empty). See <see cref="RemoteTemplateSource"/>.</param>
public sealed record AiOptions(string Provider, string Model, Uri? RemoteTemplatesUrl)
{
	/// <summary>The offline default: the in-repo stub provider, no remote template URL.</summary>
	public static AiOptions Offline { get; } = new("offline", string.Empty, RemoteTemplatesUrl: null);

	/// <summary>Environment variable names, grouped so the (few) config keys have one definition.</summary>
	public const string ProviderEnvironmentVariable = "SPECDESK_AI_PROVIDER";

	public const string ModelEnvironmentVariable = "SPECDESK_AI_MODEL";

	public const string RemoteTemplatesUrlEnvironmentVariable = "SPECDESK_AI_TEMPLATES_URL";

	/// <summary>
	/// Read the options from the environment via <paramref name="getEnv"/> (defaults to the process
	/// environment). Pure over the accessor so it is unit-testable without touching process env. A blank
	/// or malformed template URL is treated as "not configured" rather than a startup failure.
	/// </summary>
	public static AiOptions FromEnvironment(Func<string, string?>? getEnv = null)
	{
		getEnv ??= Environment.GetEnvironmentVariable;

		string provider = Trimmed(getEnv(ProviderEnvironmentVariable)) ?? Offline.Provider;
		string model = Trimmed(getEnv(ModelEnvironmentVariable)) ?? Offline.Model;

		Uri? templatesUrl = null;
		if (Trimmed(getEnv(RemoteTemplatesUrlEnvironmentVariable)) is { } raw
			&& Uri.TryCreate(raw, UriKind.Absolute, out Uri? parsed)
			&& parsed.Scheme is "http" or "https")
		{
			templatesUrl = parsed;
		}

		return new AiOptions(provider, model, templatesUrl);
	}

	private static string? Trimmed(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
