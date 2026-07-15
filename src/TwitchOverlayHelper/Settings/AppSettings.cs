namespace TwitchOverlayHelper.Settings;

public sealed class AppSettings
{
    public string Channel { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public double OverlayLeft { get; set; } = 42;
    public double OverlayTop { get; set; } = 120;
    public double OverlayWidth { get; set; } = 520;
    public double OverlayHeight { get; set; } = 720;
    public double BackgroundOpacity { get; set; } = 0.72;
    public double MessageBackgroundOpacity { get; set; } = 0.19;
    public double FontSize { get; set; } = 22;
    public double LineSpacing { get; set; } = 1.42;
    public string FontFamily { get; set; } = "Verdana";
    public int MaxMessages { get; set; } = 18;
    public bool ShowBadges { get; set; } = true;
    public bool ShowTimestamps { get; set; }
    public bool UseTwitchNameColors { get; set; } = true;
    public bool EmphasizeMentions { get; set; } = true;
    public bool ShowEmotes { get; set; } = true;
    public bool TextOutline { get; set; }
    public bool OverlayVisible { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public uint ToggleHotkeyModifiers { get; set; } = 0x0002; // MOD_CONTROL
    public uint ToggleHotkeyVk { get; set; } = 0x78;          // VK_F9
    public string ToggleHotkeyText { get; set; } = "Ctrl + F9";
    public uint EditHotkeyModifiers { get; set; } = 0x0002;   // MOD_CONTROL
    public uint EditHotkeyVk { get; set; } = 0x79;            // VK_F10
    public string EditHotkeyText { get; set; } = "Ctrl + F10";

    public void Normalize()
    {
        OverlayLeft = FiniteOrDefault(OverlayLeft, 42);
        OverlayTop = FiniteOrDefault(OverlayTop, 120);
        OverlayWidth = Math.Clamp(FiniteOrDefault(OverlayWidth, 520), 320, 4000);
        OverlayHeight = Math.Clamp(FiniteOrDefault(OverlayHeight, 720), 260, 4000);
        BackgroundOpacity = Math.Clamp(FiniteOrDefault(BackgroundOpacity, 0.72), 0, 0.95);
        MessageBackgroundOpacity = Math.Clamp(FiniteOrDefault(MessageBackgroundOpacity, 0.19), 0, 0.8);
        FontSize = Math.Clamp(FiniteOrDefault(FontSize, 22), 16, 36);
        LineSpacing = Math.Clamp(FiniteOrDefault(LineSpacing, 1.42), 1.15, 1.8);
        MaxMessages = Math.Clamp(MaxMessages, 1, 200);
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Verdana" : FontFamily;
        Channel ??= string.Empty;
        ClientId ??= string.Empty;
        UserName ??= string.Empty;
        ToggleHotkeyText ??= "Ctrl + F9";
        EditHotkeyText ??= "Ctrl + F10";
    }

    private static double FiniteOrDefault(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
