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
    private readonly string _signedOutPath;
    private readonly Action<string> _deleteFile;
    private readonly Action<string, byte[]> _writeFile;

    public FileTokenStore(ITokenProtector protector, string authDir)
        : this(protector, authDir, File.Delete, File.WriteAllBytes)
    {
    }

    internal FileTokenStore(
        ITokenProtector protector,
        string authDir,
        Action<string> deleteFile,
        Action<string, byte[]>? writeFile = null)
    {
        _protector = protector;
        _path = Path.Combine(authDir, "github-token");
        _signedOutPath = Path.Combine(authDir, "github-token.signed-out");
        _deleteFile = deleteFile;
        _writeFile = writeFile ?? File.WriteAllBytes;
    }

    public void Save(StoredToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(token);
        _writeFile(_path, _protector.Protect(plaintext));

        // The new token must reach disk before the sign-out marker is removed. If either operation
        // fails, Load remains fail-closed instead of reviving the token from the previous session.
        _deleteFile(_signedOutPath);
    }

    public StoredToken? Load()
    {
        if (File.Exists(_signedOutPath) || !File.Exists(_path))
        {
            return null;
        }

        try
        {
            byte[] plaintext = _protector.Unprotect(File.ReadAllBytes(_path));
            return JsonSerializer.Deserialize<StoredToken>(plaintext);
        }
        catch (Exception ex) when (
            ex is JsonException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            // A corrupt, partially-written, undecryptable (e.g. copied from another machine, so DPAPI can't
            // unprotect it), or unreadable (ACL-denied / path is a directory — an UnauthorizedAccessException,
            // which is NOT an IOException) token is treated as "signed out": the user re-authenticates rather
            // than the app crashing or the account affordance faulting on a tampered/inaccessible file.
            return null;
        }
    }

    public void Clear()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Persist the signed-out state before attempting to remove the token. Token deletion can be
        // blocked by antivirus, another process, or an ACL; the marker prevents that stale token from
        // authorizing a later process. Failure to persist the marker is surfaced to the caller.
        _writeFile(_signedOutPath, []);

        try
        {
            // File.Delete is idempotent for a missing file; avoiding a separate exists probe also avoids
            // racing an ACL or filesystem change between the probe and deletion.
            _deleteFile(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The durable marker already makes the stale encrypted token unusable on subsequent launches.
        }
    }
}
