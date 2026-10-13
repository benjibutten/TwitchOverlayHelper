using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace TwitchOverlayHelper.Speech;

/// <summary>
/// Everything the app says out loud, on the machine running it. Played here rather than in the
/// dock's browser because a dock inside OBS is often muted or on another audio device than the one
/// the streamer is listening to.
///
/// <para>Names and paid readings each have their own <see cref="MediaPlayer"/>, so a click on a
/// speaker button cannot cut a reading off mid-word; the two may overlap.</para>
/// </summary>
public sealed class SpeechAudioPlayer(Dispatcher dispatcher)
{
    private MediaPlayer? _namePlayer;
    private MediaPlayer? _messagePlayer;

    /// <summary>Starts a name clip and completes once playback has started, not when it ends.</summary>
    public Task PlayAsync(string filePath, double volume) =>
        dispatcher.InvokeAsync(() => Play(ref _namePlayer, filePath, volume)).Task;

    /// <summary>
    /// Plays a message and completes when it has finished. Cancelling stops playback where it is.
    /// </summary>
    public async Task PlayToEndAsync(string filePath, double volume, CancellationToken cancellationToken)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(() =>
        {
            MediaPlayer player = _messagePlayer ??= new MediaPlayer();

            // Named so they can be taken off again: this player is reused for every reading, and
            // handlers left behind would have the previous clip's ending complete this one.
            void OnEnded(object? sender, EventArgs e) => Done();
            void OnFailed(object? sender, ExceptionEventArgs e) => Done();

            void Done()
            {
                player.MediaEnded -= OnEnded;
                player.MediaFailed -= OnFailed;
                finished.TrySetResult();
            }

            player.MediaEnded += OnEnded;
            player.MediaFailed += OnFailed;

            try
            {
                Play(ref _messagePlayer, filePath, volume);
            }
            catch
            {
                Done();
                throw;
            }

            // Registered inside the dispatcher call so the stop lands on the player that is playing,
            // and disposed by the continuation below once the clip is over either way.
            CancellationTokenRegistration stop = cancellationToken.Register(() =>
                dispatcher.InvokeAsync(() =>
                {
                    try { player.Stop(); } catch (InvalidOperationException) { }
                    Done();
                }));
            finished.Task.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }).Task.ConfigureAwait(false);

        await finished.Task.ConfigureAwait(false);
    }

    public void Close() => dispatcher.Invoke(() =>
    {
        _namePlayer?.Close();
        _namePlayer = null;
        _messagePlayer?.Close();
        _messagePlayer = null;
    });

    private static void Play(ref MediaPlayer? slot, string filePath, double volume)
    {
        try
        {
            // MediaPlayer belongs to the thread that created it, which is why playback is marshalled here.
            slot ??= new MediaPlayer();
            slot.Volume = Math.Clamp(volume, 0, 1);
            slot.Open(new Uri(filePath));
            slot.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or NotSupportedException
                                      or System.Runtime.InteropServices.COMException)
        {
            throw new SpeechException("Ljudet kunde inte spelas upp på den här datorn: " + ex.Message);
        }
    }
}
