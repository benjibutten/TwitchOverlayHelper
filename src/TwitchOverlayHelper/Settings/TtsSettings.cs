namespace TwitchOverlayHelper.Settings;

/// <summary>
/// What a viewer spends to have something read out loud. The two are deliberately not the same
/// feature seen twice: only the channel points half can ever be paid back.
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
    /// A browser source in OBS. The default, and the only one that reaches the viewers without the
    /// streamer having to capture their own desktop: OBS takes a browser source's audio into the
    /// mix itself, on its own track, with its own volume and monitoring.
    /// </summary>
    Browser,

    /// <summary>
    /// This computer's speakers, through the app. Heard by the streamer, and by the viewers only if
    /// OBS is capturing desktop audio – which most setups deliberately do not do, because it would
    /// also carry every notification sound on the machine.
    /// </summary>
    Desktop,

    /// <summary>Both at once, for a streamer who wants to hear it locally as well.</summary>
    Both
}

/// <summary>
/// Reading a viewer's message out loud, bought with bits or with channel points.
///
/// <para>Sibling to <see cref="SpeechSettings"/> rather than part of it: that one is a reading aid
/// for the streamer – a button beside a name in the dock – while this one is something viewers pay
/// for and every one of them hears the result of. They share the ElevenLabs account and its key;
/// they share nothing else, and above all not the voice. The name reader is picked for saying odd
/// logins clearly, which is rarely the voice a channel wants for its messages.</para>
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
    /// How long a request may sit unanswered before it is let go. A reading nobody approved during
    /// the whole stream is not one the viewer still wants to hear; on the channel points route it
    /// also hands the points back rather than leaving them spent on silence.
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// The voice for messages. Its own setting rather than <see cref="SpeechSettings.VoiceId"/>:
    /// the name reader and the channel's message voice are two different jobs.
    /// </summary>
    public string VoiceId { get; set; } = string.Empty;

    /// <summary>Only for showing which voice is picked; the id is what the API needs.</summary>
    public string VoiceName { get; set; } = string.Empty;

    public string ElevenLabsModel { get; set; } = "eleven_v3";

    public TtsOutput Output { get; set; } = TtsOutput.Browser;

    public double Volume { get; set; } = 0.9;

    /// <summary>Whether the reading is meant to reach OBS rather than only this machine's speakers.</summary>
    public bool UsesBrowser => Output is TtsOutput.Browser or TtsOutput.Both;

    public bool UsesDesktop => Output is TtsOutput.Desktop or TtsOutput.Both;

    /// <summary>
    /// Where a message is cut. ElevenLabs bills by the character, so this is the one setting that
    /// decides what a single redemption can cost – and a viewer who pastes a novel should not be
    /// able to hold the stream for four minutes either.
    /// </summary>
    public int MaxCharacters { get; set; } = 240;

    /// <summary>Reads "Kajsa säger:" before the message, so the viewers know whose words they are.</summary>
    public bool AnnounceName { get; set; } = true;

    /// <summary>
    /// How many requests may wait at once. Beyond this the newest is refused – on the channel points
    /// route with the points handed straight back, which is a better answer than a queue nobody will
    /// reach the end of.
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
    /// there is no catch-all: an empty id claims nothing, because a reward nobody named would turn
    /// every redemption in the channel into something read out loud.
    /// </summary>
    public bool MatchesReward(string? rewardId) =>
        Trigger == TtsTrigger.Reward
        && RewardId.Length > 0
        && string.Equals(RewardId, rewardId, StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        PowerUpId = PowerUpId?.Trim() ?? string.Empty;
        PowerUpTitle = PowerUpTitle?.Trim() ?? string.Empty;
        RewardId = RewardId?.Trim() ?? string.Empty;
        RewardTitle = RewardTitle?.Trim() ?? string.Empty;
        VoiceId = VoiceId?.Trim() ?? string.Empty;
        VoiceName = VoiceName?.Trim() ?? string.Empty;
        ElevenLabsModel = string.IsNullOrWhiteSpace(ElevenLabsModel) ? "eleven_v3" : ElevenLabsModel.Trim();
        Volume = Math.Clamp(double.IsFinite(Volume) ? Volume : 0.9, 0, 1);
        MaxCharacters = Math.Clamp(MaxCharacters, 20, 1000);
        ApprovalTimeoutSeconds = Math.Clamp(ApprovalTimeoutSeconds, 30, 7200);
        QueueLimit = Math.Clamp(QueueLimit, 1, 100);
        RewardCost = Math.Clamp(RewardCost, 1, 10_000_000);
        // A hand-edited settings.json could claim a reward is ours without an id to answer on. The
        // flag is dropped rather than trusted: it decides whether viewers get their points back.
        if (RewardId.Length == 0) RewardManaged = false;
    }
}
