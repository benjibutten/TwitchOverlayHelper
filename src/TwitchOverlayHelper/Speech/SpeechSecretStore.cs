using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwitchOverlayHelper.Speech;

/// <summary>API keys for the two services behind name pronunciation.</summary>
public sealed record SpeechSecrets(string DeepSeekApiKey, string ElevenLabsApiKey)
{
    public static readonly SpeechSecrets Empty = new(string.Empty, string.Empty);

    public bool IsComplete => DeepSeekApiKey.Length > 0 && ElevenLabsApiKey.Length > 0;
}

/// <summary>
/// Keeps the DeepSeek and ElevenLabs keys out of settings.json. They are billable credentials, so
/// they get the same treatment as the Twitch refresh token: DPAPI under the current Windows user.
/// </summary>
public sealed class SpeechSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TwitchOverlayHelper.Speech.v1");
    private readonly string _path;
    private readonly Lock _lock = new();
    private SpeechSecrets? _cached;

    public SpeechSecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchOverlayHelper", "speech.bin");
    }

    /// <summary>Cached so the speaker button does not pay for a DPAPI round trip on every click.</summary>
    public SpeechSecrets Current
    {
        get
        {
            lock (_lock) return _cached ??= Read();
        }
    }

    public void Save(SpeechSecrets secrets)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            string tempPath = _path + ".tmp";
            File.WriteAllBytes(tempPath, encrypted);
            File.Move(tempPath, _path, true);
            _cached = secrets;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _cached = SpeechSecrets.Empty;
        }
    }

    private SpeechSecrets Read()
    {
        try
        {
            if (!File.Exists(_path)) return SpeechSecrets.Empty;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SpeechSecrets>(plain) ?? SpeechSecrets.Empty;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Keys we cannot decrypt are keys we do not have; the button simply stays hidden.
            return SpeechSecrets.Empty;
        }
    }
}
