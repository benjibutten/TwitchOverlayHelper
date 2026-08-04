namespace TwitchOverlayHelper.Settings;

/// <summary>
/// Name pronunciation: a speaker button next to every name in the dock reads the name out loud on
/// this machine. Only the choices belong here – the two API keys are secret and live in
/// <see cref="Twitch.TokenStore"/>'s neighbour, the DPAPI-encrypted speech secret store.
/// </summary>
public sealed class SpeechSettings
{
    /// <summary>Master switch. The button also needs both keys and a voice before it appears.</summary>
    public bool Enabled { get; set; }

    /// <summary>Turns the written user name into something a voice model can say.</summary>
    public string DeepSeekModel { get; set; } = "deepseek-v4-flash";

    /// <summary>Eleven v3 is the expressive model; older ids still work if the account lacks it.</summary>
    public string ElevenLabsModel { get; set; } = "eleven_v3";

    public string VoiceId { get; set; } = string.Empty;
    /// <summary>Only for showing which voice is picked; the id is what the API needs.</summary>
    public string VoiceName { get; set; } = string.Empty;

    public double Volume { get; set; } = 0.9;

    public void Normalize()
    {
        DeepSeekModel = Fallback(DeepSeekModel, "deepseek-v4-flash");
        ElevenLabsModel = Fallback(ElevenLabsModel, "eleven_v3");
        VoiceId = VoiceId?.Trim() ?? string.Empty;
        VoiceName = VoiceName?.Trim() ?? string.Empty;
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 0.9, 0, 1);
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
