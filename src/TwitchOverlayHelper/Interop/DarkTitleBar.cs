using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TwitchOverlayHelper.Interop;

/// <summary>
/// Tints the caption bar to match the window below it. WPF only owns the client area, so without
/// this a near-black window sits under a white title bar and reads as a rendering fault rather
/// than a dark theme.
/// </summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Hooks the window so the tint is applied as soon as it has a handle – there is no caption to
    /// paint before that. Safe to call from a constructor.
    /// </summary>
    public static void Enable(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        if (new WindowInteropHelper(window).Handle != nint.Zero) Apply(window);
    }

    private static void Apply(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;

        int on = 1;
        // Windows 10 builds before 20H1 used a different attribute number, and anything older than
        // that simply returns a failure code – a light caption bar, which is what we had anyway.
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref on, sizeof(int));
    }
}
