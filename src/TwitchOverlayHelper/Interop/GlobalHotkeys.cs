using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TwitchOverlayHelper.Interop;

/// <summary>Registers system-wide hotkeys on a WPF window (same approach as StreamDecky).</summary>
public static class GlobalHotkeys
{
    public const int WmHotkey = 0x0312;

    // Without MOD_NOREPEAT, holding the hotkey down makes Windows post repeated
    // WM_HOTKEY messages (keyboard auto-repeat), which would toggle the overlay
    // several times per press. This flag delivers exactly one message per press.
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool Register(Window window, int id, uint modifiers, uint vk)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || vk == 0) return false;
        return RegisterHotKey(hwnd, id, modifiers | ModNoRepeat, vk);
    }

    public static void Unregister(Window window, int id)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, id);
    }

    public static bool ReRegister(Window window, int id, uint modifiers, uint vk)
    {
        Unregister(window, id);
        return Register(window, id, modifiers, vk);
    }
}
