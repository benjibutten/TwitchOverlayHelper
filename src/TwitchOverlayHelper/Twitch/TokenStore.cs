using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwitchOverlayHelper.Twitch;

/// <summary>Refresh token that survives restarts so the streamer does not re-authenticate every session.</summary>
public sealed record StoredCredentials(string RefreshToken, string ClientId, string Login, string UserId, string[] Scopes);

/// <summary>
/// Persists the refresh token encrypted with DPAPI under the current Windows user, so another
/// account on the same machine cannot read it and it never appears in plain text on disk.
/// </summary>
public sealed class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TwitchOverlayHelper.Credentials.v1");
    private readonly string _path;

    public TokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchOverlayHelper", "credentials.bin");
    }

    public StoredCredentials? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredCredentials>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // A token we cannot decrypt is worthless; treat it as "not logged in" rather than failing startup.
            return null;
        }
    }

    public void Save(StoredCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        string tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, _path, true);
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
