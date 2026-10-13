using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Overlay;

/// <summary>
/// The edge glow itself: a full-screen window that is only ever a border of light. It fades in,
/// breathes gently while it stays, and fades out again – and like the chat overlay it is
/// click-through and never takes focus, so the game underneath never notices it was there.
/// Hidden whenever it is not playing, so it costs nothing between alerts.
/// </summary>
public partial class EdgeAlertWindow : Window
{
    private const double FadeInSeconds = 0.5;
    /// <summary>One breath: peak → dimmed → peak. Slow enough to read as calm rather than blinking.</summary>
    private const double PulseSeconds = 2.4;

    /// <summary>
    /// Which run of the animation is the current one. A new alert while one is playing simply
    /// restarts the light; the old run's Completed must not hide the window under the new one.
    /// </summary>
    private int _playToken;

    /// <summary>
    /// Whether this window has closed for good. A <see cref="Play"/> queued on the dispatcher
    /// during shutdown can land after the window is gone, and <see cref="Window.Show"/> throws on a
    /// closed window. Hide needs no such guard: WPF makes it a no-op once the window has closed.
    /// </summary>
    private bool _closed;

    public EdgeAlertWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Lights the edges once. Blind to <see cref="EdgeAlertStyle.Enabled"/>: the chat triggers
    /// check it, while the settings window's test buttons preview a switched-off alert.
    ///
    /// <para><paramref name="seconds"/> overrides the style's own length, for a glow that is being
    /// held open and has only its remaining time to run.</para>
    /// </summary>
    public void Play(EdgeAlertStyle style, double edgeWidth, double? seconds = null)
    {
        if (_closed) return;

        Color color = ParseColor(style.Color);
        double band = Math.Clamp(edgeWidth, 40, 400);
        TopEdge.Height = band;
        TopEdge.Fill = EdgeBrush(color, new Point(0.5, 0), new Point(0.5, 1));
        BottomEdge.Height = band;
        BottomEdge.Fill = EdgeBrush(color, new Point(0.5, 1), new Point(0.5, 0));
        LeftEdge.Width = band;
        LeftEdge.Fill = EdgeBrush(color, new Point(0, 0.5), new Point(1, 0.5));
        RightEdge.Width = band;
        RightEdge.Fill = EdgeBrush(color, new Point(1, 0.5), new Point(0, 0.5));

        // Re-measured on every play – the screen resolution may have changed since last time.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Show();
        EnsureTopmost();

        int token = ++_playToken;
        // Read before the new animation replaces the old one: starting from the current brightness
        // makes a second alert hold the light rather than blink. Zero when nothing is playing.
        DoubleAnimationUsingKeyFrames animation = BuildAnimation(
            GlowRoot.Opacity,
            Math.Clamp(style.Intensity, 0.15, 1),
            // The floor is well under the shortest setting: a glow one second from its ceiling
            // fades out in that second rather than being rounded up past it.
            Math.Clamp(seconds ?? style.DurationSeconds, 0.4, 20));
        animation.Completed += (_, _) =>
        {
            if (token != _playToken) return;
            GlowRoot.BeginAnimation(OpacityProperty, null);
            GlowRoot.Opacity = 0;
            Hide();
        };
        GlowRoot.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>
    /// Fades in quickly, breathes between full and about two thirds while the light stays, and
    /// takes the longest part of the time fading away.
    ///
    /// <para><paramref name="from"/> is the current brightness. The fade-in shrinks the closer the
    /// light already is to full, so several alerts in a row do not flicker.</para>
    /// </summary>
    private static DoubleAnimationUsingKeyFrames BuildAnimation(double from, double peak, double totalSeconds)
    {
        // Capped at half the run as well: a glow with a second left to live must still reach its
        // peak before it starts leaving, or the keyframes would run past the end of the animation.
        double fadeIn = Math.Min(Math.Max(0.05, FadeInSeconds * (1 - Math.Clamp(from / peak, 0, 1))), totalSeconds * 0.5);
        double fadeOut = Math.Min(1.8, totalSeconds * 0.4);
        var softIn = new SineEase { EasingMode = EasingMode.EaseOut };
        var softInOut = new SineEase { EasingMode = EasingMode.EaseInOut };

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, At(fadeIn), softIn));
        for (double t = fadeIn + PulseSeconds / 2; t + PulseSeconds / 2 <= totalSeconds - fadeOut; t += PulseSeconds)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak * 0.65, At(t), softInOut));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, At(t + PulseSeconds / 2), softInOut));
        }
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, At(totalSeconds), softInOut));
        return animation;

        static KeyTime At(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Full colour at the screen edge, thinning to nothing well before the middle band ends.</summary>
    private static LinearGradientBrush EdgeBrush(Color color, Point start, Point end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(235, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(90, color.R, color.G, color.B), 0.38),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value)!; }
        catch (FormatException) { return Color.FromRgb(245, 158, 11); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_TRANSPARENT + WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW: clicks pass through, focus is
        // never taken, nothing shows in Alt+Tab. Unlike the chat overlay there is no edit mode, so
        // this window is inert for its whole life.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        style |= 0x00000020 | 0x00000080 | 0x08000000;
        SetWindowLongPtr(hwnd, -20, new IntPtr(style));
    }

    private void EnsureTopmost()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
