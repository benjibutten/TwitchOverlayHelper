using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Hands out a random token for one audio file, so the browser source can fetch a reading
/// without the server ever taking a path from the page. A token can only name a file the app
/// itself synthesised, which rules out path traversal.
///
/// <para>Tokens do not expire on a clock, because a reading can wait in the queue behind a long
/// one. The oldest are dropped once there are more than the limit.</para>
/// </summary>
public sealed class TtsAudioStore
{
    private const int Limit = 60;

    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    /// <summary>Mints a new token for this file.</summary>
    public string Publish(string filePath)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _files[token] = filePath;
        _order.Enqueue(token);
        while (_order.Count > Limit && _order.TryDequeue(out string? oldest)) _files.TryRemove(oldest, out _);
        return token;
    }

    public bool TryGet(string token, out string filePath) => _files.TryGetValue(token, out filePath!);
}
