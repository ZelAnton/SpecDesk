using SpecDesk.AppInfo;

namespace SpecDesk.GitHub;

/// <summary>
/// Configuration for GitHub device-flow sign-in: the OAuth App <see cref="ClientId"/> and the
/// <see cref="Scopes"/> to request. The client id is a <em>public, non-secret</em> value from a
/// registered GitHub OAuth App (device flow has no client secret); the host resolves it (app settings →
/// env <see cref="ClientIdEnvironmentVariable"/> → the compiled <see cref="DefaultClientId"/>) and passes
/// it in, keeping this library free of global/env access.
/// </summary>
public sealed record GitHubAuthOptions(string ClientId, IReadOnlyList<string> Scopes)
{
    /// <summary>The environment variable name the host checks for an override client id, derived from
    /// <see cref="ProductInfo.Name"/> rather than a second hard-coded "SPECDESK" literal.</summary>
    public static string ClientIdEnvironmentVariable { get; } =
        $"{ProductInfo.Name.ToUpperInvariant()}_GITHUB_CLIENT_ID";

    /// <summary>The compiled-in OAuth App client id. Empty until the maintainer registers the app and
    /// pastes its (public) client id here; an empty id means sign-in is unconfigured. RELEASE CHECKLIST:
    /// this MUST be set (or supplied via env <see cref="ClientIdEnvironmentVariable"/>) before shipping
    /// the GitHub round-trip — otherwise the build silently ships with sign-in dark and no error
    /// anywhere.</summary>
    public const string DefaultClientId = "";

    /// <summary><c>repo</c> (push + open PR), <c>read:user</c> and <c>user:email</c> (identify the
    /// signed-in author). Team reviewers may add <c>read:org</c> in a later stage.</summary>
    public static IReadOnlyList<string> DefaultScopes { get; } = ["repo", "read:user", "user:email"];

    /// <summary>Options with the default scopes for a given client id.</summary>
    public static GitHubAuthOptions ForClient(string clientId) => new(clientId, DefaultScopes);
}
