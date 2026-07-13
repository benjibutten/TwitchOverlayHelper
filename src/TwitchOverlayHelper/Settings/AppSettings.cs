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
    public uint ToggleHotkeyModifiers { get; set; } = 0x0002; // MOD_CONTROL
    public uint ToggleHotkeyVk { get; set; } = 0x78;          // VK_F9
    public string ToggleHotkeyText { get; set; } = "Ctrl + F9";
    public uint EditHotkeyModifiers { get; set; } = 0x0002;   // MOD_CONTROL
    public uint EditHotkeyVk { get; set; } = 0x79;            // VK_F10
    public string EditHotkeyText { get; set; } = "Ctrl + F10";
}
