using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayHelper.Settings;

/// <summary>Who says the bot's lines in chat, if anyone.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BotMode>))]
public enum BotMode
{
    /// <summary>Nothing is ever written to chat. The app still says everything it says today.</summary>
    Off,

    /// <summary>A second Twitch account, logged in separately. What the feature is for.</summary>
    Bot,

    /// <summary>
    /// The streamer's own account, over the connection the app already has. There when someone wants
    /// the messages without setting up a second account – and the only thing that works at all until
    /// the bot has been logged in.
    /// </summary>
    Streamer
}

/// <summary>
/// One thing the bot can be asked to say. Serialized by name rather than by number: the order here
/// is going to change as more are added, and a saved settings file must not start meaning something
/// else because a flow was inserted in the middle.
/// </summary>
[JsonConverter(typeof(BotFlowJsonConverter))]
public enum BotFlow
{
    /// <summary>
    /// A flow this version does not have – one a later version added, or an earlier version had and
    /// this one dropped. Zero so a rule saved without a flow at all lands here rather than on
    /// whichever real flow happened to be first, and dropped by <see cref="BotSettings.Normalize"/>.
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
    HypeTrainEnd
}

/// <summary>
/// Reads a flow name, and answers <see cref="BotFlow.Unknown"/> for one it does not know instead of
/// throwing.
///
/// <para><b>Why this exists.</b> Flows come and go between versions. The stock string converter
/// throws on a name it has never heard of, and that exception travels all the way out to
/// <see cref="SettingsStore.Load"/> – which catches it and hands back a blank
/// <see cref="AppSettings"/>. Upgrading past a removed flow would therefore not merely drop that
/// flow: it would silently reset the overlay, the channel, the pets and every other setting in the
/// file. One unrecognised word is not worth somebody's whole configuration.</para>
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
/// <para>Deliberately the plainest thing that works. The answer is a template like every other
/// message the bot sends, so <c>{viewer}</c>, <c>{streamer}</c> and this channel's own word for its
/// pets already work inside it without anything here knowing about them.</para>
/// </summary>
public sealed class BotCommand
{
    /// <summary>What a viewer types, cleaned to one word starting with "!".</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>What the bot answers.</summary>
    public string Response { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long this command keeps quiet after answering. Not zero by default: a command is the one
    /// thing here viewers can set off on purpose, and a room deciding to type it together is how a
    /// bot spends its whole write allowance in ten seconds.
    /// </summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>Whether only the broadcaster and moderators may set it off.</summary>
    public bool ModeratorsOnly { get; set; }

    /// <summary>Whether this row is worth trying to match at all.</summary>
    public bool IsUsable => Enabled && Command.Length > 1 && Response.Length > 0;

    public void Normalize()
    {
        // An empty command stays empty rather than becoming "!" plus something invented: this is a
        // row the streamer has added and not filled in yet, and giving it a trigger of our choosing
        // would put a command in the channel that nobody chose.
        string typed = (Command ?? string.Empty).Trim();
        Command = typed.Length == 0 ? string.Empty : EdgeAlertSettings.CleanCommand(typed);
        Response = (Response ?? string.Empty).Trim();
        if (Response.Length > 400) Response = Response[..400];
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 3600);
    }
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
    /// How long this flow keeps quiet after it has spoken. Zero means every occurrence is announced,
    /// which is right for the ones that name a viewer and wrong for the ones about the channel as a
    /// whole: a lawn that fills up during a raid would otherwise say so twenty times in a minute.
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
}

/// <summary>
/// A chat bot that says out loud the things the app currently only knows: that a redemption was paid
/// back and why, that a reading is still waiting for an answer, that the overlay is down and points
/// spent right now would come straight back.
///
/// <para><b>Why a second account.</b> These are the channel's messages rather than the streamer's,
/// and a viewer scrolling chat should be able to tell the difference at a glance. It is also the only
/// shape where the messages keep working when the streamer's own account is busy elsewhere. Running
/// as the streamer is offered anyway, because it works before anything has been set up.</para>
///
/// <para><b>Why the wording is all configurable.</b> Nothing here is the app's business to name. A
/// channel that calls its pets "on screen-vänner" should not have the bot say "pet", and a streamer
/// who would rather be called by name than "streamern" should not have to accept the app's word for
/// themselves.</para>
/// </summary>
public sealed class BotSettings
{
    public BotMode Mode { get; set; } = BotMode.Off;

    /// <summary>
    /// What the bot calls the streamer. Empty means the generic word, which is what a channel wants
    /// before it has thought about it – and exactly what a channel with a name wants to replace.
    /// </summary>
    public string StreamerName { get; set; } = string.Empty;

    /// <summary>What one pet is called in this channel. Singular, indefinite: "en pet".</summary>
    public string PetWord { get; set; } = "pet";

    /// <summary>The plural: "två pets".</summary>
    public string PetWordPlural { get; set; } = "pets";

    /// <summary>
    /// The definite singular: "peten". Its own field because Swedish cannot derive it – "vän" becomes
    /// "vännen" and "pet" becomes "peten", and guessing would put the bot's grammar in the app's
    /// hands rather than the streamer's.
    /// </summary>
    public string PetWordDefinite { get; set; } = "peten";

    /// <summary>How long a reading may wait for an answer before the bot says something about it.</summary>
    public int TtsWaitingSeconds { get; set; } = 60;

    /// <summary>
    /// How many refunds landing close together stop being announced one by one and become a single
    /// line instead. The sweep that runs after a restart can answer a whole stream's worth of
    /// redemptions in a few seconds, and a bot reading them out would be its own incident.
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
    /// Whether the bot's own lines are kept out of the overlay. They belong in chat, where they are
    /// answering someone; on top of a game they are a stream of notifications nobody asked for.
    /// </summary>
    public bool HideOwnMessagesInOverlay { get; set; } = true;

    /// <summary>
    /// Whether the bot's lines are exempt from everything chat can trigger – pets, edge alerts, the
    /// welcome. Left on: a bot that greets itself, or spawns a pet from its own "{pet} levde klart",
    /// is the loop this switch exists to prevent.
    /// </summary>
    public bool IgnoreOwnMessages { get; set; } = true;

    public List<BotMessageRule> Messages { get; set; } = [];

    /// <summary>
    /// Commands the streamer made up. Empty to begin with, on purpose: what a channel wants to be
    /// able to answer is not something the app can guess, and shipping guesses would mean shipping
    /// commands somebody has to go and switch off.
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
        // Rebuilt from the defaults rather than patched in place: a flow added by a later version has
        // to appear with its default text, and a flow this version has never heard of is dropped
        // rather than kept as a rule nothing will ever raise.
        Messages ??= [];
        var kept = new List<BotMessageRule>(Defaults.Count);
        foreach (BotMessageRule fallback in Defaults)
        {
            // "messages": [null] is the same kind of hand-edit, and the null would land in the
            // comparison rather than in the result.
            BotMessageRule rule = Messages.FirstOrDefault(saved => saved is not null && saved.Flow == fallback.Flow)
                ?? new BotMessageRule { Flow = fallback.Flow, Enabled = fallback.Enabled, Template = fallback.Template, CooldownSeconds = fallback.CooldownSeconds };
            rule.Normalize(fallback);
            kept.Add(rule);
        }
        Messages = kept;

        // Nulls here are the same hand-edit as everywhere else in this file. Two rows answering the
        // same word are not: only the first would ever fire, so the second is dropped rather than
        // left in the list looking as though it works.
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
    /// <para>What is on by default is the half a viewer is owed an answer to: their points came back,
    /// their reading is still waiting, the thing they were about to pay for is not working right now.
    /// The celebratory half – subs, hype trains, a pet that lived out its time – is off, because
    /// Twitch already says most of it and a channel should choose to add to that rather than have to
    /// switch it off.</para>
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
        Rule(BotFlow.HypeTrainEnd, false, "Hypetåget slutade på nivå {level} – tack allihop!")
    ];

    private static BotMessageRule Rule(BotFlow flow, bool enabled, string template, int cooldown = 0) =>
        new() { Flow = flow, Enabled = enabled, Template = template, CooldownSeconds = cooldown };
}
