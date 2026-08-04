using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Speech;

/// <summary>What was read out loud, plus anything the reader should know about it.</summary>
public sealed record NameSpeechResult(string Spoken, string? Warning);

/// <summary>
/// Reads a chatter's name out loud: DeepSeek turns the written name into speakable text, ElevenLabs
/// says it, and the app plays it on this machine. Both steps cost money per call, so both are
/// cached – the same chatter is usually clicked more than once during a stream.
/// </summary>
public sealed class NameSpeechService
{
    /// <summary>Roughly a stream's worth of distinct chatters; beyond that the oldest files go.</summary>
    private const int AudioFileLimit = 300;
    private const int SpokenCacheLimit = 1000;

    private readonly AppSettings _settings;
    private readonly SpeechSecretStore _secrets;
    private readonly DeepSeekClient _deepSeek;
    private readonly ElevenLabsClient _elevenLabs;
    private readonly Func<string, double, Task> _play;
    private readonly string _audioDirectory;
    private readonly ConcurrentDictionary<string, string> _spoken = new(StringComparer.OrdinalIgnoreCase);

    // One name at a time: the button can be clicked repeatedly, and two voices talking over each
    // other helps nobody – nor does paying for both.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NameSpeechService(
        HttpClient httpClient,
        AppSettings settings,
        SpeechSecretStore secrets,
        Func<string, double, Task> play,
        string? audioDirectory = null)
    {
        _settings = settings;
        _secrets = secrets;
        _play = play;
        _deepSeek = new DeepSeekClient(httpClient);
        _elevenLabs = new ElevenLabsClient(httpClient);
        _audioDirectory = audioDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwitchOverlayHelper", "namecache");
    }

    /// <summary>Both keys and a voice are in place, so a name can be spoken.</summary>
    public bool CanSpeak => _settings.Speech.VoiceId.Length > 0 && _secrets.Current.IsComplete;

    /// <summary>Whether the dock should show the speaker button at all.</summary>
    public bool IsConfigured => _settings.Speech.Enabled && CanSpeak;

    public Task<IReadOnlyList<VoiceOption>> GetVoicesAsync(string apiKey, CancellationToken cancellationToken = default) =>
        _elevenLabs.GetVoicesAsync(apiKey, cancellationToken);

    /// <summary>Drops the remembered pronunciations, e.g. after the language model was changed.</summary>
    public void ForgetPronunciations() => _spoken.Clear();

    public async Task<NameSpeechResult> SpeakAsync(string name, CancellationToken cancellationToken = default)
    {
        string subject = name.Trim();
        if (subject.Length == 0) throw new SpeechException("Meddelandet saknar namn att läsa upp.");
        if (!CanSpeak) throw new SpeechException("Uppläsning är inte konfigurerad – fyll i API-nycklar och välj en röst i appen.");

        SpeechSettings speech = _settings.Speech;
        SpeechSecrets secrets = _secrets.Current;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? warning = null;
            if (!_spoken.TryGetValue(subject, out string? spoken))
            {
                try
                {
                    spoken = await _deepSeek.RewriteNameAsync(subject, secrets.DeepSeekApiKey, speech.DeepSeekModel, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SpeechException or HttpRequestException)
                {
                    // Hearing the name spelled out roughly still beats hearing nothing, so the
                    // reading goes ahead with the raw name and the dock says why it may sound off.
                    spoken = subject;
                    warning = $"Namnet kunde inte tolkas, läser upp det som det står. {ex.Message}";
                }

                // Only a real answer is worth keeping: caching the fallback would turn one failed
                // call into a name that never gets interpreted again for the rest of the session.
                if (warning is null) Remember(subject, spoken);
            }

            string file = await EnsureAudioAsync(spoken, speech, secrets, token).ConfigureAwait(false);
            await _play(file, speech.Volume).ConfigureAwait(false);
            return new NameSpeechResult(spoken, warning);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechException("Uppläsningen tog för lång tid och avbröts.");
        }
        finally { _gate.Release(); }
    }

    private void Remember(string subject, string spoken)
    {
        // A stream can meet a lot of names; the cache is a convenience, not a store worth pruning cleverly.
        if (_spoken.Count >= SpokenCacheLimit) _spoken.Clear();
        _spoken[subject] = spoken;
    }

    /// <summary>Returns the path to the MP3 for this text, synthesising it only if it is new.</summary>
    private async Task<string> EnsureAudioAsync(string spoken, SpeechSettings speech, SpeechSecrets secrets, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_audioDirectory, CacheKey(spoken, speech) + ".mp3");
        if (File.Exists(path)) return path;

        byte[] audio = await _elevenLabs
            .SynthesizeAsync(spoken, speech.VoiceId, secrets.ElevenLabsApiKey, speech.ElevenLabsModel, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_audioDirectory);
            string tempPath = path + ".tmp";
            await File.WriteAllBytesAsync(tempPath, audio, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechException("Ljudfilen kunde inte sparas: " + ex.Message);
        }
        return path;
    }

    /// <summary>The voice and model are part of the key, so changing either re-reads the name.</summary>
    private static string CacheKey(string spoken, SpeechSettings speech)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{speech.VoiceId}|{speech.ElevenLabsModel}|{spoken}"));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_audioDirectory).GetFiles("*.mp3");
            if (files.Length <= AudioFileLimit) return;
            foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc).Take(files.Length - AudioFileLimit + 50))
                file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A cache that cannot be tidied is still a working cache.
        }
    }
}
