using System.Collections.Concurrent;
using TwitchOverlayHelper.Diagnostics;
using TwitchOverlayHelper.Speech;

namespace TwitchOverlayHelper.Web;

/// <summary>
/// Plays a reading through the OBS browser source rather than through this machine's speakers.
///
/// <para><b>Why this exists at all.</b> The app's own audio comes out of the desktop, and most OBS
/// setups deliberately do not capture desktop audio – it would carry every notification sound on the
/// machine onto the stream. So a reading played locally is heard by the streamer and by nobody else,
/// which is the one thing a paid reading must not be. A browser source, on the other hand, is mixed
/// by OBS itself: its own track, its own fader, its own monitoring.</para>
///
/// <para>The wait for the page's acknowledgement is what makes the queue a queue: without it the app
/// would fire every approved clip at once and the browser would play them on top of each other. It
/// is also the only honest evidence that a reading was delivered, which is what the channel points
/// route answers Twitch with.</para>
/// </summary>
public sealed class BrowserTtsOutput(ChatHub hub, TtsAudioStore audio, Func<string> keyProvider)
{
    /// <summary>
    /// The longest a clip is waited for. Generous: the reading itself can run a good while at the
    /// character limits the settings allow, and a browser fetching the audio over loopback adds
    /// almost nothing. It exists so a page that dies mid-clip cannot hold the queue forever.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiting = new(StringComparer.Ordinal);

    /// <summary>Wired to the hub's report so a page's answer lands on the clip that is waiting.</summary>
    public void OnFinished(string playbackId, bool played)
    {
        if (_waiting.TryRemove(playbackId, out TaskCompletionSource<bool>? waiter)) waiter.TrySetResult(played);
    }

    /// <summary>
    /// How many reading pages are connected, from the hub. Zero means whatever is in flight is playing
    /// to nobody – OBS took the source out of the scene, reloaded it, or the page died – so it is
    /// failed here and now instead of at the timeout, which is five minutes of a queue that has
    /// stopped moving and a viewer left waiting for points they are going to get anyway.
    ///
    /// <para>The page cannot say this itself. Its own report travels over the socket that has just
    /// closed, and a source removed from a scene never runs another line of script.</para>
    /// </summary>
    public void OnOverlayCountChanged(int count)
    {
        if (count > 0) return;
        foreach (string playbackId in _waiting.Keys)
            if (_waiting.TryRemove(playbackId, out TaskCompletionSource<bool>? waiter)) waiter.TrySetResult(false);
    }

    /// <summary>
    /// Sends one clip to the reading pages and returns when they are done with it. Throws
    /// <see cref="SpeechException"/> when it could not be played, which is what turns an unheard
    /// reading into a refund rather than into a purchase quietly spent on silence.
    /// </summary>
    public async Task PlayAsync(TtsClip clip, CancellationToken cancellationToken)
    {
        string playbackId = Guid.NewGuid().ToString("N");
        string token = audio.Publish(clip.FilePath);
        // The key rides along because every /api path is gated on it, this one included: a page on
        // this machine that is not ours should not be able to pull the readings down.
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
                // The streamer pressed stop, or the app is closing. The page is told so the sound
                // actually stops rather than playing on to the end of a reading nobody wants.
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
