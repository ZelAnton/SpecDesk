using System.Security.Cryptography;
using System.Text.Json;

namespace SpecDesk.GitHub;

/// <summary>The persisted session: the GitHub access token and the login it belongs to (so the signed-in
/// user can be shown without a network call).</summary>
internal sealed record StoredToken(string AccessToken, string Login);

/// <summary>Loads/saves/clears the single signed-in <see cref="StoredToken"/>.</summary>
internal interface ITokenStore
{
    void Save(StoredToken token);

    StoredToken? Load();

    void Clear();
}

/// <summary>
/// Stores the token as an encrypted JSON file under an auth directory (the host passes
/// <c>%LOCALAPPDATA%\SpecDesk\auth</c>). Encryption is delegated to an <see cref="ITokenProtector"/>; this
/// type owns only the file/serialization logic, which is what the cross-platform tests exercise.
/// </summary>
internal sealed class FileTokenStore : ITokenStore
{
    private readonly ITokenProtector _protector;
    private readonly string _path;

    public FileTokenStore(ITokenProtector protector, string authDir)
    {
        _protector = protector;
        _path = Path.Combine(authDir, "github-token");
    }

    public void Save(StoredToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(token);
        File.WriteAllBytes(_path, _protector.Protect(plaintext));
    }

    public StoredToken? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            byte[] plaintext = _protector.Unprotect(File.ReadAllBytes(_path));
            return JsonSerializer.Deserialize<StoredToken>(plaintext);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or IOException)
        {
            // A corrupt, partially-written, or undecryptable token (e.g. copied from another machine, so
            // DPAPI can't unprotect it) is treated as "signed out" — the user re-authenticates rather than
            // the app crashing on a tampered file.
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
