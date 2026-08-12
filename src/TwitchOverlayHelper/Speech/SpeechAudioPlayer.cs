using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace TwitchOverlayHelper.Speech;

/// <summary>
/// Everything the app says out loud, on the machine running it. The dock could play these in the
/// browser, but the buttons are pressed in OBS – where a hidden dock is often muted or on another
/// audio device than the one the streamer is listening to.
///
/// <para>Two players, not one. A name is a word the streamer asked to hear; a paid reading is a
/// sentence the viewers are waiting through, and it can run for half a minute. Sharing one
/// <see cref="MediaPlayer"/> would mean a click on a speaker button cutting a reading off mid-word –
/// so they get one each, and the rare overlap is the cheaper of the two mistakes.</para>
/// </summary>
public sealed class SpeechAudioPlayer(Dispatcher dispatcher)
{
    private MediaPlayer? _namePlayer;
    private MediaPlayer? _messagePlayer;

    /// <summary>Starts a name clip and returns; nothing waits for a word to finish.</summary>
    public Task PlayAsync(string filePath, double volume) =>
        dispatcher.InvokeAsync(() => Play(ref _namePlayer, filePath, volume)).Task;

    /// <summary>
    /// Plays a message and completes when it has actually finished – which is what lets the readings
    /// be a queue rather than a pile-up. Cancelling stops it where it is, for the dock's stop button
    /// and for a shutdown mid-sentence.
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
