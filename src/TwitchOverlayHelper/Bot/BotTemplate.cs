using System.Text;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Bot;

/// <summary>
/// Turns a streamer's message template into the line the bot writes.
///
/// <para>Two jobs, and the second one is the reason this is not a string.Replace at the call site.
/// The first is filling in <c>{viewer}</c> and its siblings. The second is that everything the app
/// already words for the streamer – the reason a redemption was paid back, above all – is written in
/// the app's own vocabulary, and chat is a different audience: the words "pet" and "streamern" are
/// the app's guesses at what this channel calls things, and a reason string is sometimes an error
/// message from ElevenLabs that no viewer should ever be shown.</para>
/// </summary>
public static class BotTemplate
{
    /// <summary>
    /// What the bot may say about a redemption that did not work out, keyed by the wording the app
    /// uses internally.
    ///
    /// <para><b>Why a list and not a passthrough.</b> A reading that failed carries whatever the
    /// synthesis threw – an HTTP status, a quota message, the word "Unauthorized" – and passing that
    /// to chat would put the streamer's plumbing on stream. Anything not recognised here becomes the
    /// flow's own fallback instead, which says the honest thing without saying too much.</para>
    /// </summary>
    private static readonly Dictionary<string, string> KnownReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["peten levde klart"] = "{peten} levde klart",
        ["peten fick gå hem i förtid"] = "{peten} fick gå hem i förtid",
        ["overlayen ritade aldrig peten"] = "{peten} kom aldrig upp på skärmen",
        ["pet-overlayen försvann"] = "{pets} slutade synas på skärmen",
        ["pet-overlayen var inte igång"] = "{pets} syntes inte på skärmen",
        ["pets är avstängda i appen"] = "{pets} är avstängda just nu",
        ["det var fullt på gräsmattan"] = "det var fullt på gräsmattan",
        ["appen var inte igång"] = "appen var inte igång",
        ["återbetalad i Twitch"] = "återbetald i Twitch",
        ["uppläsning är avstängd i appen"] = "uppläsning är avstängd just nu",
        ["ingen röst eller ElevenLabs-nyckel är inställd"] = "uppläsning är inte igång just nu",
        ["inlösen innehöll ingen text att läsa upp"] = "inlösen innehöll ingen text",
        ["kön för uppläsning är full"] = "kön för uppläsning var full",
        ["nekad av streamern"] = "nekad av {streamer}",
        ["ingen hann svara"] = "ingen hann svara i tid",
        ["uppläst"] = "uppläst",
        ["avbruten av streamern"] = "avbruten av {streamer}",
        ["avbruten innan något hann läsas upp"] = "avbruten innan något hann läsas upp",
        ["ett oväntat fel avbröt uppläsningen"] = "något gick fel under uppläsningen"
    };

    /// <summary>What a template for this flow can fill in, beyond the words every flow gets.</summary>
    public static IReadOnlyList<string> PlaceholdersFor(BotFlow flow) => flow switch
    {
        BotFlow.PetRefund or BotFlow.TtsRefund => ["viewer", "cost", "reason"],
        BotFlow.PetFulfilled or BotFlow.TtsSpoken => ["viewer", "cost"],
        BotFlow.RefundBatch => ["count", "total", "reason"],
        BotFlow.TtsAccepted or BotFlow.TtsWaiting => ["viewer", "cost"],
        BotFlow.TtsQueueFull or BotFlow.TtsUnavailable or BotFlow.PetLawnFull or BotFlow.PetsDisabled => ["viewer", "cost"],
        BotFlow.ModCallAck => ["viewer"],
        BotFlow.ModCallMissed => ["viewer", "reason"],
        BotFlow.Welcome or BotFlow.ShoutoutReceived or BotFlow.Subscription => ["viewer"],
        BotFlow.Raid => ["viewer", "viewers", "link"],
        BotFlow.HypeTrainEnd => ["level"],
        _ => []
    };

    /// <summary>
    /// What a command the streamer wrote themselves can fill in. Only the person who typed it, for
    /// now – the words every message gets are added by the caller.
    /// </summary>
    public static IReadOnlyList<string> CommandPlaceholders { get; } = ["viewer"];

    /// <summary>The words every template can use, whatever raised it.</summary>
    public static IReadOnlyList<string> GlobalPlaceholders { get; } = ["streamer", "pet", "pets", "peten"];

    /// <summary>
    /// Stand-in values, so the settings window can show what a template will actually look like
    /// without waiting for a viewer to redeem something.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SampleFor(BotFlow flow, BotSettings settings)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["viewer"] = "Kajsa",
            ["cost"] = "500",
            ["count"] = "4",
            ["total"] = "2000",
            ["viewers"] = "37",
            ["level"] = "3",
            ["link"] = "twitch.tv/kajsa"
        };
        values["reason"] = flow switch
        {
            BotFlow.TtsRefund => Reason("ingen hann svara", "det gick inte den här gången", settings),
            BotFlow.ModCallMissed => "du är varken moderator eller broadcaster",
            BotFlow.RefundBatch => Reason("appen var inte igång", "det gick inte den här gången", settings),
            _ => Reason("overlayen ritade aldrig peten", "det gick inte den här gången", settings)
        };
        return values;
    }

    /// <summary>
    /// The reason worded for chat: the app's own sentence when it is one we recognise, with this
    /// channel's words in it, and the caller's fallback when it is anything else.
    /// </summary>
    public static string Reason(string? reason, string fallback, BotSettings settings)
    {
        string trimmed = (reason ?? string.Empty).Trim();
        return KnownReasons.TryGetValue(trimmed, out string? known)
            ? Render(known, settings, null)
            : fallback;
    }

    /// <summary>
    /// Fills in a template. Placeholders are <c>{name}</c>, case-insensitive; an unknown one is left
    /// standing rather than silently swallowed, because the settings window previews every template
    /// and a typo that shows up there is one the streamer can fix before chat ever sees it.
    /// </summary>
    public static string Render(string template, BotSettings settings, IReadOnlyDictionary<string, string>? values)
    {
        if (template.Length == 0) return string.Empty;

        var result = new StringBuilder(template.Length + 32);
        int index = 0;
        while (index < template.Length)
        {
            int open = template.IndexOf('{', index);
            if (open < 0) { result.Append(template, index, template.Length - index); break; }
            int close = template.IndexOf('}', open + 1);
            if (close < 0) { result.Append(template, index, template.Length - index); break; }

            result.Append(template, index, open - index);
            string key = template[(open + 1)..close];
            if (Lookup(key, settings, values) is { } replacement) result.Append(replacement);
            else result.Append(template, open, close - open + 1);
            index = close + 1;
        }

        // A line whose only content was an empty placeholder would otherwise go out as "@ fick
        // tillbaka  poäng"; collapsing the gaps keeps a half-filled template readable.
        return CollapseSpaces(result.ToString());
    }

    private static string? Lookup(string key, BotSettings settings, IReadOnlyDictionary<string, string>? values)
    {
        if (values is not null && values.TryGetValue(key, out string? value)) return value;
        return key.ToLowerInvariant() switch
        {
            "streamer" => settings.StreamerName.Length > 0 ? settings.StreamerName : "streamern",
            "pet" => settings.PetWord,
            "pets" => settings.PetWordPlural,
            "peten" => settings.PetWordDefinite,
            _ => null
        };
    }

    private static string CollapseSpaces(string text)
    {
        var result = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            bool isSpace = c == ' ';
            if (isSpace && lastWasSpace) continue;
            result.Append(c);
            lastWasSpace = isSpace;
        }
        return result.ToString().Trim();
    }
}
