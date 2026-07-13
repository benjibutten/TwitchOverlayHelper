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
    public double FontSize { get; set; } = 22;
    public double LineSpacing { get; set; } = 1.42;
    public string FontFamily { get; set; } = "Verdana";
    public int MaxMessages { get; set; } = 18;
    public bool ShowBadges { get; set; } = true;
    public bool ShowTimestamps { get; set; }
    public bool UseTwitchNameColors { get; set; } = true;
    public bool EmphasizeMentions { get; set; } = true;
    public bool OverlayVisible { get; set; } = true;
}
