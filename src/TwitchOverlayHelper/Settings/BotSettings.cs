using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Settings;

/// <summary>Who says the bot's lines in chat, if anyone.</summary>
[JsonConverter(typeof(BotModeJsonConverter))]
public enum BotMode
{
    /// <summary>Nothing is ever written to chat.</summary>
    Off,

    /// <summary>A second Twitch account, logged in separately.</summary>
    Bot,

    /// <summary>
    /// The streamer's own account, over the connection the app already has. Works without a second
    /// account being set up.
    /// </summary>
    Streamer
}

/// <summary>
/// One thing the bot can be asked to say. Serialized by name rather than by number, so inserting a
/// flow does not change what a saved settings file means.
/// </summary>
[JsonConverter(typeof(BotFlowJsonConverter))]
public enum BotFlow
{
    /// <summary>
    /// A flow this version does not have. Zero so a rule saved without a flow lands here rather than
    /// on a real flow; dropped by <see cref="BotSettings.Normalize"/>.
    /// </summary>
    Unknown = 0,

    /// <summary>A pet redemption was paid back.</summary>
    PetRefund,

    /// <summary>A pet lived its full time and the purchase was booked as delivered.</summary>
    PetFulfilled,

    /// <summary>Several redemptions were paid back at once – the sweep after a restart, above all.</summary>
    RefundBatch,

    /// <summary>The lawn was full, so the redemption bought nothing.</summary>
    PetLawnFull,

    /// <summary>Pets are switched off in the app and somebody redeemed anyway.</summary>
    PetsDisabled,

    /// <summary>Every pet overlay went away – redemptions from here on are paid straight back.</summary>
    PetOverlayDown,

    /// <summary>A pet overlay is connected again.</summary>
    PetOverlayBack,

    /// <summary>A reading was accepted and is waiting for the streamer's yes.</summary>
    TtsAccepted,

    /// <summary>The reading has been waiting a while and nobody has answered it yet.</summary>
    TtsWaiting,

    /// <summary>A reading was read out in full.</summary>
    TtsSpoken,

    /// <summary>A reading redemption was paid back – refused, expired, or never spoken.</summary>
    TtsRefund,

    /// <summary>The reading queue was full, so the request was refused.</summary>
    TtsQueueFull,

    /// <summary>Reading is switched off or not configured, and somebody paid for one anyway.</summary>
    TtsUnavailable,

    /// <summary>A moderator wrote the call command and the edges lit up.</summary>
    ModCallAck,

    /// <summary>Somebody wrote the call command and nothing happened, with the reason.</summary>
    ModCallMissed,

    /// <summary>Somebody wrote in the channel for the first time.</summary>
    Welcome,

    /// <summary>The channel was raided.</summary>
    Raid,

    /// <summary>Another channel gave this one a shoutout.</summary>
    ShoutoutReceived,

    /// <summary>Someone subscribed or resubscribed.</summary>
    Subscription,

    /// <summary>A hype train started.</summary>
    HypeTrainBegin,

    /// <summary>A hype train ended.</summary>
    HypeTrainEnd,

    /// <summary>The lucky spin landed and somebody owns a new pet.</summary>
    SpinWin,

    /// <summary>
    /// The spin landed on something the winner already owns. The points are spent either way; what
    /// is left is one chance to give the pet to somebody else.
    /// </summary>
    SpinDuplicate,

    /// <summary>A duplicate win found a new home.</summary>
    SpinGifted,

    /// <summary>The name given with the give command matched nobody on Twitch.</summary>
    SpinGiftUnknown,

    /// <summary>
    /// The named recipient already owns the prize – themselves included – so nobody receives it and
    /// the chance is spent.
    /// </summary>
    SpinGiftOwned,

    /// <summary>A duplicate was never given away in time, so its chance ran out.</summary>
    SpinGiftExpired,

    /// <summary>The winner of a duplicate put it on the lawn themselves instead of giving it away.</summary>
    SpinReleased,

    /// <summary>The answer to the list command: what this viewer has won.</summary>
    SpinList,

    /// <summary>The answer to the list command when the viewer has won nothing yet.</summary>
    SpinListEmpty,

    /// <summary>The answer to the pool command: every art the spin can land on, and how rare each is.</summary>
    SpinPool,

    /// <summary>The answer to the pool command when nothing is marked winnable at all.</summary>
    SpinPoolEmpty,

    /// <summary>Somebody asked a pet reward for a win-only pet they never won.</summary>
    SpinNotOwned,

    /// <summary>A spin redemption was paid back.</summary>
    SpinRefund
}

/// <summary>
/// Reads the bot's mode, and answers <see cref="BotMode.Off"/> for a name it does not know instead
/// of throwing.
///
/// <para>The stock string converter throws, which makes the whole settings file unreadable over one
/// unknown word – what a downgrade would otherwise look like. Off rather than a guess: this decides
/// which account writes in the streamer's chat.</para>
/// </summary>
public sealed class BotModeJsonConverter : JsonConverter<BotMode>
{
    public override BotMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
        && Enum.TryParse(reader.GetString(), ignoreCase: true, out BotMode mode)
        && Enum.IsDefined(mode)
            ? mode
            : BotMode.Off;

    public override void Write(Utf8JsonWriter writer, BotMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.GetName(value) ?? nameof(BotMode.Off));
}

/// <summary>
/// Reads a flow name, and answers <see cref="BotFlow.Unknown"/> for one it does not know instead of
/// throwing.
///
/// <para>Flows come and go between versions. The stock string converter throws on an unknown name,
/// and <see cref="SettingsStore.Load"/> answers that exception with a blank
/// <see cref="AppSettings"/> – one unrecognised flow would reset every setting in the file.</para>
/// </summary>
public sealed class BotFlowJsonConverter : JsonConverter<BotFlow>
{
    public override BotFlow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
        && Enum.TryParse(reader.GetString(), ignoreCase: true, out BotFlow flow)
            ? flow
            : BotFlow.Unknown;

    public override void Write(Utf8JsonWriter writer, BotFlow value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.GetName(value) ?? nameof(BotFlow.Unknown));
}

/// <summary>
/// A command the streamer made up: a word viewers type in chat, and what the bot answers with.
///
/// <para>The answer is a template like every other message the bot sends, so <c>{viewer}</c>,
/// <c>{streamer}</c> and this channel's own word for its pets work inside it.</para>
/// </summary>
public sealed class BotCommand
{
    /// <summary>What a viewer types, cleaned to one word starting with "!".</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>What the bot answers.</summary>
    public string Response { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long this command keeps quiet after answering. Not zero by default: viewers can set a
    /// command off on purpose, and a room typing it together would spend the bot's write allowance.
    /// </summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>Whether only the broadcaster and moderators may set it off.</summary>
    public bool ModeratorsOnly { get; set; }

    /// <summary>Whether this row is worth trying to match at all.</summary>
    public bool IsUsable => Enabled && Command.Length > 1 && Response.Length > 0;

    public void Normalize()
    {
        // An empty command stays empty: it is a row not filled in yet, and inventing a trigger for
        // it would put a command in the channel that nobody chose.
        string typed = (Command ?? string.Empty).Trim();
        Command = typed.Length == 0 ? string.Empty : EdgeAlertSettings.CleanCommand(typed);
        Response = (Response ?? string.Empty).Trim();
        if (Response.Length > 400) Response = Response[..400];
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 3600);
    }

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// What the bot says for one flow, and whether it says it at all. The template is the streamer's to
/// write – see <see cref="Bot.BotTemplate"/> for the placeholders each flow fills in.
/// </summary>
public sealed class BotMessageRule
{
    public BotFlow Flow { get; set; }

    public bool Enabled { get; set; }

    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// How long this flow keeps quiet after it has spoken. Zero announces every occurrence: right
    /// for flows that name a viewer, wrong for channel-wide ones such as a lawn filling up in a raid.
    /// </summary>
    public int CooldownSeconds { get; set; }

    public void Normalize(BotMessageRule fallback)
    {
        // Null rather than merely blank when the template was removed by hand from settings.json.
        if (string.IsNullOrWhiteSpace(Template)) Template = fallback.Template;
        Template = Template.Trim();
        // The 500-char IRC limit with room for the "@name " a reply-style template usually starts with.
        if (Template.Length > 400) Template = Template[..400];
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 3600);
    }

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// A chat bot that says out loud what the app otherwise only knows: that a redemption was paid back
/// and why, that a reading is still waiting for an answer, that the overlay is down and points
/// spent right now would come straight back.
///
/// <para>A second account by design, so a viewer can tell the channel's messages from the
/// streamer's; running as the streamer is offered because it works before anything is set up. All
/// the wording is configurable: what a channel calls its pets and its streamer is not the app's to
/// decide.</para>
/// </summary>
public sealed class BotSettings
{
    public BotMode Mode { get; set; } = BotMode.Off;

    /// <summary>What the bot calls the streamer. Empty means the generic word.</summary>
    public string StreamerName { get; set; } = string.Empty;

    /// <summary>What one pet is called in this channel. Singular, indefinite: "en pet".</summary>
    public string PetWord { get; set; } = "pet";

    /// <summary>The plural: "två pets".</summary>
    public string PetWordPlural { get; set; } = "pets";

    /// <summary>
    /// The definite singular: "peten". Its own field because Swedish cannot derive it – "vän" becomes
    /// "vännen" and "pet" becomes "peten".
    /// </summary>
    public string PetWordDefinite { get; set; } = "peten";

    /// <summary>How long a reading may wait for an answer before the bot says something about it.</summary>
    public int TtsWaitingSeconds { get; set; } = 60;

    /// <summary>
    /// How many refunds landing close together are announced as a single line instead of one by
    /// one. The sweep after a restart can refund a whole stream's worth of redemptions in seconds.
    /// </summary>
    public int RefundBatchThreshold { get; set; } = 3;

    /// <summary>How long refunds are collected before they are announced, so a batch can be seen as one.</summary>
    public int RefundBatchWindowSeconds { get; set; } = 4;

    /// <summary>
    /// Messages allowed per 30 seconds. Twitch's own ceiling is 20 for an ordinary account and 100
    /// for a moderator; this stays under both by default, because the punishment for crossing it is
    /// a global write ban rather than a dropped line.
    /// </summary>
    public int MessagesPer30Seconds { get; set; } = 15;

    /// <summary>
    /// Whether the bot's own lines are kept out of the overlay, where they would be notifications
    /// on top of a game rather than answers in chat.
    /// </summary>
    public bool HideOwnMessagesInOverlay { get; set; } = true;

    /// <summary>
    /// Whether the bot's lines are exempt from everything chat can trigger – pets, edge alerts, the
    /// welcome – so it cannot greet itself or spawn a pet from its own "{pet} levde klart".
    /// </summary>
    public bool IgnoreOwnMessages { get; set; } = true;

    public List<BotMessageRule> Messages { get; set; } = [];

    /// <summary>
    /// Commands the streamer made up. Empty by default: what a channel wants to answer is not
    /// something the app can guess.
    /// </summary>
    public List<BotCommand> Commands { get; set; } = [];

    /// <summary>Whether anything would actually be sent, whatever the individual flows say.</summary>
    public bool IsActive => Mode != BotMode.Off;

    public BotMessageRule Rule(BotFlow flow) =>
        Messages.FirstOrDefault(rule => rule.Flow == flow) ?? Defaults.First(rule => rule.Flow == flow);

    public bool Speaks(BotFlow flow) => IsActive && Rule(flow).Enabled;

    public void Normalize()
    {
        StreamerName = (StreamerName ?? string.Empty).Trim();
        PetWord = Word(PetWord, "pet");
        PetWordPlural = Word(PetWordPlural, "pets");
        PetWordDefinite = Word(PetWordDefinite, "peten");
        TtsWaitingSeconds = Math.Clamp(TtsWaitingSeconds, 10, 3600);
        RefundBatchThreshold = Math.Clamp(RefundBatchThreshold, 2, 50);
        RefundBatchWindowSeconds = Math.Clamp(RefundBatchWindowSeconds, 1, 60);
        MessagesPer30Seconds = Math.Clamp(MessagesPer30Seconds, 1, 90);
        // Rebuilt from the defaults rather than patched in place: a flow missing from the file
        // appears with its default text, and one this version does not know is dropped.
        Messages ??= [];
        var kept = new List<BotMessageRule>(Defaults.Count);
        foreach (BotMessageRule fallback in Defaults)
        {
            // A hand-edited "messages": [null] must not throw in the comparison.
            BotMessageRule rule = Messages.FirstOrDefault(saved => saved is not null && saved.Flow == fallback.Flow)
                ?? new BotMessageRule { Flow = fallback.Flow, Enabled = fallback.Enabled, Template = fallback.Template, CooldownSeconds = fallback.CooldownSeconds };
            rule.Normalize(fallback);
            kept.Add(rule);
        }
        Messages = kept;

        // Nulls are a hand-edit and skipped. Of two rows answering the same word only the first
        // would ever fire, so the second is dropped.
        Commands ??= [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<BotCommand>(Commands.Count);
        foreach (BotCommand command in Commands)
        {
            if (command is null) continue;
            command.Normalize();
            // A row still being filled in has no word to collide with and is kept as it is.
            if (command.Command.Length > 0 && !seen.Add(command.Command)) continue;
            commands.Add(command);
            if (commands.Count >= 100) break;
        }
        Commands = commands;
    }

    private static string Word(string? value, string fallback)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? fallback : trimmed;
    }

    /// <summary>
    /// The wording the app ships with, and the source of truth for which flows exist at all.
    ///
    /// <para>On by default are the answers a viewer is owed: their points came back, their reading
    /// is still waiting, the thing they were about to pay for is down. The celebratory flows – subs,
    /// hype trains, a pet that lived out its time – are off, because Twitch already says most of it.</para>
    /// </summary>
    public static IReadOnlyList<BotMessageRule> Defaults { get; } =
    [
        Rule(BotFlow.PetRefund, true, "@{viewer} fick tillbaka {cost} poäng – {reason}."),
        Rule(BotFlow.PetFulfilled, false, "@{viewer}s {pet} levde klart. Tack för besöket!"),
        Rule(BotFlow.RefundBatch, true, "{count} inlösen betalades tillbaka – {reason}."),
        Rule(BotFlow.PetLawnFull, true, "Det är fullt på gräsmattan just nu, så nya {pets} får vänta en stund.", cooldown: 120),
        Rule(BotFlow.PetsDisabled, true, "{pets} är avstängda just nu – spara poängen tills de är igång igen.", cooldown: 120),
        Rule(BotFlow.PetOverlayDown, true, "Just nu syns inga {pets} på skärmen, så inlösta {pets} betalas tillbaka. Vänta gärna med att lösa in.", cooldown: 300),
        Rule(BotFlow.PetOverlayBack, true, "{pets} syns på skärmen igen!", cooldown: 300),
        Rule(BotFlow.TtsAccepted, false, "@{viewer} din uppläsning ligger i kön och väntar på {streamer}."),
        Rule(BotFlow.TtsWaiting, true, "@{viewer} {streamer} har inte hunnit svara på din uppläsning än – den ligger kvar och du får tillbaka poängen om ingen hinner."),
        Rule(BotFlow.TtsSpoken, false, "Läste upp @{viewer}s meddelande."),
        Rule(BotFlow.TtsRefund, true, "@{viewer} fick tillbaka {cost} poäng – {reason}."),
        Rule(BotFlow.TtsQueueFull, true, "Kön för uppläsning är full just nu – prova igen om en stund.", cooldown: 60),
        Rule(BotFlow.TtsUnavailable, true, "Uppläsning är inte igång just nu.", cooldown: 120),
        Rule(BotFlow.ModCallAck, true, "@{viewer} {streamer} är pingad."),
        Rule(BotFlow.ModCallMissed, true, "@{viewer} kommandot gick inte fram: {reason}."),
        Rule(BotFlow.Welcome, true, "Välkommen till chatten, @{viewer}!"),
        Rule(BotFlow.Raid, true, "Tack för raiden @{viewer}! Kolla in {link}"),
        Rule(BotFlow.ShoutoutReceived, true, "Tack för shoutouten @{viewer}!"),
        Rule(BotFlow.Subscription, false, "Tack för stödet @{viewer}!"),
        Rule(BotFlow.HypeTrainBegin, false, "Hypetåget har lämnat stationen! 🚂"),
        Rule(BotFlow.HypeTrainEnd, false, "Hypetåget slutade på nivå {level} – tack allihop!"),
        // The spin flows are on by default, unlike the celebratory ones: every line here is an
        // answer somebody is waiting on – what they won, what to type next, where their gift went.
        Rule(BotFlow.SpinWin, true, "🎉 @{viewer} vann {prize} ({rarity}) i lyckosnurren!"),
        Rule(BotFlow.SpinDuplicate, true, "@{viewer} du vann {prize} ({rarity}) – men den har du redan! Skriv \"{command} namn\" inom {minutes} min för att skänka den till någon, eller \"{release}\" för att släppa ut den på skärmen."),
        Rule(BotFlow.SpinGifted, true, "🎁 @{viewer} skänkte {prize} till @{target}!"),
        Rule(BotFlow.SpinGiftUnknown, true, "@{viewer} hittade ingen som heter \"{target}\" – testa igen."),
        Rule(BotFlow.SpinGiftOwned, true, "@{viewer} @{target} hade redan {prize} – chansen är borta."),
        Rule(BotFlow.SpinGiftExpired, true, "@{viewer} hann inte skänka {prize} – den är borta nu."),
        Rule(BotFlow.SpinReleased, true, "🐾 @{viewer} släppte ut sin {prize} på skärmen!"),
        Rule(BotFlow.SpinList, true, "@{viewer} din samling: {list}.", cooldown: 15),
        Rule(BotFlow.SpinListEmpty, true, "@{viewer} du har inte vunnit någon {pet} än – lyckosnurren väntar!", cooldown: 15),
        // The same answer for everybody who asks, so it keeps quiet for longer than the per-viewer ones.
        Rule(BotFlow.SpinPool, true, "@{viewer} {pets} som går att vinna ({count} st): {list}", cooldown: 30),
        Rule(BotFlow.SpinPoolEmpty, true, "@{viewer} inga {pets} går att vinna just nu.", cooldown: 30),
        Rule(BotFlow.SpinNotOwned, true, "@{viewer} {prize} kan bara användas av den som vunnit den i lyckosnurren."),
        Rule(BotFlow.SpinRefund, true, "@{viewer} fick tillbaka {cost} poäng – {reason}.")
    ];

    private static BotMessageRule Rule(BotFlow flow, bool enabled, string template, int cooldown = 0) =>
        new() { Flow = flow, Enabled = enabled, Template = template, CooldownSeconds = cooldown };

    /// <inheritdoc cref="AppSettings.Unknown"/>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
