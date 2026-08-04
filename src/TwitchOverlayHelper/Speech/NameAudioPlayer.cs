using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace TwitchOverlayHelper.Speech;

/// <summary>
/// Plays the name clip through the app's own audio session, on the machine running the app. The
/// dock could play it in the browser, but the button is pressed in OBS – where a hidden dock is
/// often muted or on another audio device than the one the streamer is listening to.
/// </summary>
public sealed class NameAudioPlayer(Dispatcher dispatcher)
{
    private MediaPlayer? _player;

    public Task PlayAsync(string filePath, double volume) =>
        dispatcher.InvokeAsync(() => Play(filePath, volume)).Task;

    public void Close() => dispatcher.Invoke(() =>
    {
        _player?.Close();
        _player = null;
    });

    private void Play(string filePath, double volume)
    {
        try
        {
            // MediaPlayer belongs to the thread that created it, which is why playback is marshalled here.
            _player ??= new MediaPlayer();
            _player.Volume = Math.Clamp(volume, 0, 1);
            _player.Open(new Uri(filePath));
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or NotSupportedException
                                      or System.Runtime.InteropServices.COMException)
        {
            throw new SpeechException("Ljudet kunde inte spelas upp på den här datorn: " + ex.Message);
        }
    }
}
