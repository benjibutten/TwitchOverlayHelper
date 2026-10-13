using System.Collections.Concurrent;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Speech;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Plays a reading through the OBS browser source rather than through this machine's speakers.
///
/// <para>Most OBS setups do not capture desktop audio, so a reading played locally would be heard
/// by the streamer and nobody else. A browser source is mixed by OBS itself, with its own track
/// and fader.</para>
///
/// <para>Each clip waits for the page's acknowledgement, which keeps clips from playing on top of
/// each other and is the only evidence that a reading was delivered.</para>
/// </summary>
public sealed class BrowserTtsOutput(ChatHub hub, TtsAudioStore audio, Func<string> keyProvider)
{
    // The longest a clip is waited for, so a page that dies mid-clip cannot hold the queue
    // forever. Long enough for a reading at the character limits the settings allow.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiting = new(StringComparer.Ordinal);

    /// <summary>Completes the clip waiting on <paramref name="playbackId"/> with the page's report.</summary>
    public void OnFinished(string playbackId, bool played)
    {
        if (_waiting.TryRemove(playbackId, out TaskCompletionSource<bool>? waiter)) waiter.TrySetResult(played);
    }

    /// <summary>
    /// Takes the number of connected reading pages from the hub. At zero every clip in flight is
    /// failed at once rather than at the timeout: the page that left cannot report over its
    /// closed socket.
    /// </summary>
    public void OnOverlayCountChanged(int count)
    {
        if (count > 0) return;
        foreach (string playbackId in _waiting.Keys)
            if (_waiting.TryRemove(playbackId, out TaskCompletionSource<bool>? waiter)) waiter.TrySetResult(false);
    }

    /// <summary>
    /// Sends one clip to the reading pages and returns when they are done with it. Throws
    /// <see cref="SpeechException"/> when it could not be played, so the caller can refund it.
    /// </summary>
    public async Task PlayAsync(TtsClip clip, CancellationToken cancellationToken)
    {
        string playbackId = Guid.NewGuid().ToString("N");
        string token = audio.Publish(clip.FilePath);
        // Every /api path is gated on the key, this one included.
        string url = $"/api/tts/audio/{token}?key={Uri.EscapeDataString(keyProvider())}";

        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[playbackId] = waiter;
        try
        {
            if (hub.PublishTtsPlay(playbackId, url, clip) == 0)
                throw new SpeechException(
                    "Ingen ljudkälla för uppläsning är ansluten – lägg till uppläsningens adress som en Browser Source i OBS.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            bool played;
            try
            {
                played = await waiter.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The streamer pressed stop, or the app is closing; the page is told so the sound stops.
                hub.PublishTtsStop();
                throw;
            }
            catch (OperationCanceledException)
            {
                hub.PublishTtsStop();
                AppLog.Warn($"Uppläsning: ljudkällan svarade aldrig om uppspelning {playbackId}.");
                throw new SpeechException("Ljudkällan i OBS svarade inte – kolla att browserkällan är igång.");
            }

            if (!played) throw new SpeechException("Ljudkällan i OBS kunde inte spela upp ljudet.");
        }
        finally
        {
            _waiting.TryRemove(playbackId, out _);
        }
    }
}
