using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Settings;

/// <summary>
/// What a viewer spends to have something read out loud. Only the channel points half can ever be
/// paid back.
/// </summary>
public enum TtsTrigger
{
    /// <summary>
    /// A custom Power-up, bought with bits. Twitch delivers the redemption and nothing else: there
    /// is no endpoint to fulfil, cancel or refund one, so a refused reading costs the viewer their
    /// bits all the same.
    /// </summary>
    PowerUp,

    /// <summary>
    /// A channel points reward. When this app created it the redemption sits in the channel's queue
    /// until it is answered, which is what makes a refusal a real refund.
    /// </summary>
    Reward
}

/// <summary>Where a reading actually comes out.</summary>
public enum TtsOutput
{
    /// <summary>
    /// A browser source in OBS. The only output that reaches the viewers without desktop capture:
    /// OBS takes a browser source's audio into the mix itself, on its own track.
    /// </summary>
    Browser,

    /// <summary>
    /// This computer's speakers, through the app. Heard by the viewers only if OBS is capturing
    /// desktop audio, which most setups avoid because it carries every notification sound too.
    /// </summary>
    Desktop,

    /// <summary>Both at once, for a streamer who wants to hear it locally as well.</summary>
    Both
}

/// <summary>
/// Reading a viewer's message out loud, bought with bits or with channel points.
///
/// <para>Sibling to <see cref="SpeechSettings"/> rather than part of it: that one is a reading aid
/// for the streamer, this one is something viewers pay for and all of them hear. They share the
/// ElevenLabs account and its key, but not the voice.</para>
/// </summary>
public sealed class TtsSettings
{
    /// <summary>Master switch. A reading also needs a voice, an ElevenLabs key and a trigger.</summary>
    public bool Enabled { get; set; }

    public TtsTrigger Trigger { get; set; } = TtsTrigger.PowerUp;

    /// <summary>The custom Power-up's id in Twitch. Empty means every custom Power-up counts.</summary>
    public string PowerUpId { get; set; } = string.Empty;

    /// <summary>The Power-up's name on stream, for showing which one is picked without a round trip.</summary>
    public string PowerUpTitle { get; set; } = string.Empty;

    /// <summary>The channel points reward id. Empty means no reward is pointed at yet.</summary>
    public string RewardId { get; set; } = string.Empty;

    public string RewardTitle { get; set; } = string.Empty;

    /// <summary>
    /// This app created the reward on Twitch. The same rule as the pets': Twitch only lets an app
    /// answer redemptions of a reward its own client id made, so a reward set up by hand in the
    /// dashboard can be read out loud but never paid back.
    ///
    /// <para>Written by the app, never by hand.</para>
    /// </summary>
    public bool RewardManaged { get; set; }

    /// <summary>What the reward costs when the app creates it. Twitch's own copy is the truth.</summary>
    public int RewardCost { get; set; } = 500;

    /// <summary>
    /// Whether the streamer has to say yes before anything is read. On by default: this is the one
    /// feature in the app where a stranger's words come out of the stream's speakers.
    /// </summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>
    /// How long a request may sit unanswered before it is let go. On the channel points route that
    /// also hands the points back.
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// The voice for messages. Its own setting rather than <see cref="SpeechSettings.VoiceId"/>:
    /// the name reader and the channel's message voice are two different jobs.
    /// </summary>
    public string VoiceId { get; set; } = string.Empty;

    /// <summary>Only for showing which voice is picked; the id is what the API needs.</summary>
    public string VoiceName { get; set; } = string.Empty;

    public string ElevenLabsModel { get; set; } = "eleven_v4";

    public TtsOutput Output { get; set; } = TtsOutput.Browser;

    /// <summary>
    /// The card the viewers see while a message is read. On the same browser source as the sound,
    /// so the two need one placement in OBS and cannot fall out of step.
    /// </summary>
    public TtsWidgetSettings Widget { get; set; } = new();

    public double Volume { get; set; } = 0.9;

    /// <summary>Whether the reading is meant to reach OBS rather than only this machine's speakers.</summary>
    public bool UsesBrowser => Output is TtsOutput.Browser or TtsOutput.Both;

    public bool UsesDesktop => Output is TtsOutput.Desktop or TtsOutput.Both;

    /// <summary>
    /// Where a message is cut. ElevenLabs bills by the character, so this decides what a single
    /// redemption can cost and how long it can hold the stream.
    ///
    /// <para>Defaults to Twitch's own limit on a chat message: a lower cut would truncate messages
    /// the viewer was allowed to send and paid full price for.</para>
    /// </summary>
    public int MaxCharacters { get; set; } = 500;

    /// <summary>Reads "Kajsa säger:" before the message, so the viewers know whose words they are.</summary>
    public bool AnnounceName { get; set; } = true;

    /// <summary>
    /// How many requests may wait at once. Beyond this the newest is refused – on the channel points
    /// route with the points handed straight back.
    /// </summary>
    public int QueueLimit { get; set; } = 20;

    /// <summary>Whether this app may answer Twitch about a redemption of the chosen reward.</summary>
    public bool CanRefund => Trigger == TtsTrigger.Reward && RewardManaged && RewardId.Length > 0;

    /// <summary>Whether a custom Power-up redemption is the one this app is listening for.</summary>
    public bool MatchesPowerUp(string? powerUpId) =>
        Trigger == TtsTrigger.PowerUp
        && (PowerUpId.Length == 0 || string.Equals(PowerUpId, powerUpId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a channel points redemption is the one this app is listening for. Unlike the pets
    /// there is no catch-all: an empty id claims nothing, or every redemption in the channel would
    /// be read out loud.
    /// </summary>
    public bool MatchesReward(string? rewardId) =>
        Trigger == TtsTrigger.Reward
        && RewardId.Length > 0
        && string.Equals(RewardId, rewardId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the reading page has anything to draw. A card is only shown while a clip is playing
    /// on that page, and nothing plays there unless the sound is meant for OBS.
    /// </summary>
    public bool ShowsWidget => Widget.Enabled && UsesBrowser;

    public void Normalize()
    {
        Widget ??= new TtsWidgetSettings();
        Widget.Normalize();
        PowerUpId = PowerUpId?.Trim() ?? string.Empty;
        PowerUpTitle = PowerUpTitle?.Trim() ?? string.Empty;
        RewardId = RewardId?.Trim() ?? string.Empty;
        RewardTitle = RewardTitle?.Trim() ?? string.Empty;
        VoiceId = VoiceId?.Trim() ?? string.Empty;
        VoiceName = VoiceName?.Trim() ?? string.Empty;
        ElevenLabsModel = string.IsNullOrWhiteSpace(ElevenLabsModel) ? "eleven_v4" : ElevenLabsModel.Trim();
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 0.9, 0, 1);
        MaxCharacters = Math.Clamp(MaxCharacters, 20, 1000);
        ApprovalTimeoutSeconds = Math.Clamp(ApprovalTimeoutSeconds, 30, 7200);
        QueueLimit = Math.Clamp(QueueLimit, 1, 100);
        RewardCost = Math.Clamp(RewardCost, 1, 10_000_000);
        // A hand-edited settings.json could claim a reward is ours without an id to answer on. The
        // flag is dropped rather than trusted: it decides whether viewers get their points back.
        if (RewardId.Length == 0) RewardManaged = false;
    }

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
