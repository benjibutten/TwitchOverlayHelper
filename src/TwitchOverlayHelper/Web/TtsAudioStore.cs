using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Hands out a short-lived address for one audio file, so the browser source can fetch a reading
/// without the server ever taking a path from the page.
///
/// <para><b>Why not just serve the file by name.</b> The page asking for the audio sits in OBS on
/// the streaming machine, and an endpoint that took a file path – even one under a fixed folder –
/// is one traversal bug away from serving anything on the disk. A token minted here can only ever
/// name a file the app itself synthesised, which makes the whole question go away.</para>
///
/// <para>Tokens are kept rather than expired on a clock: a reading can sit approved behind a long
/// one, and a token that timed out mid-queue would be a reading that silently failed. The oldest
/// go once there are more than a stream's worth.</para>
/// </summary>
public sealed class TtsAudioStore
{
    private const int Limit = 60;

    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    /// <summary>Mints an address for this file, or returns the one it already has.</summary>
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
