namespace TwitchOverlayHelper.Models;

/// <summary>Where in its life a train is. Twitch sends one notification per step.</summary>
public enum HypeTrainPhase
{
    Begin,
    Progress,
    Ended
}

/// <summary>
/// One of the people carrying a train. Twitch measures bits and subscriptions in the same points,
/// but writes a subscription as its tier price – 500, 1000 or 2500 – rather than as a count, so the
/// raw number is never shown to a reader.
/// </summary>
public sealed record HypeTrainContribution(string DisplayName, string Kind, int Total);

/// <summary>
/// A hype train as it stands right now. Unlike everything else the chat views carry, this is not a
/// line in a log: it is a state that lives for minutes and is replaced rather than appended to,
/// which is why the dock draws it as a strip instead of a card that would scroll away under it.
/// </summary>
public sealed record HypeTrainState(
    string Id,
    HypeTrainPhase Phase,
    int Level,
    int Progress,
    int Goal,
    int Total,
    DateTimeOffset At)
{
    /// <summary>How long a finished train stays on screen so the level it reached can be read.</summary>
    public static readonly TimeSpan EndedLinger = TimeSpan.FromSeconds(12);

    /// <summary>The biggest contributors, in the order Twitch ranked them.</summary>
    public IReadOnlyList<HypeTrainContribution> TopContributions { get; init; } = [];

    /// <summary>
    /// When the current level runs out. Twitch pushes it forward on every level-up, so it is both
    /// the deadline the viewers are racing and the only thing that can retire a strip belonging to
    /// a train we have lost contact with.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Twitch's own kind: "regular", "treasure" or "golden_kappa".</summary>
    public string? Kind { get; init; }

    /// <summary>Whether this train is being run together with other channels.</summary>
    public bool IsShared { get; init; }

    public bool HasEnded => Phase == HypeTrainPhase.Ended;

    /// <summary>
    /// Whether this update should replace the one already on screen. Twitch makes no promise about
    /// the order of hype train notifications – the begin can arrive after the progress that started
    /// it – so the rule is that a train never walks backwards, and its end always wins.
    /// </summary>
    public bool Supersedes(HypeTrainState? current)
    {
        if (current is null || !string.Equals(Id, current.Id, StringComparison.Ordinal)) return true;
        // A train ends once. A late progress notification arriving afterwards would otherwise put
        // the bar back and claim the train is still running.
        if (current.HasEnded) return false;
        return HasEnded
            || Level > current.Level
            || (Level == current.Level && Total >= current.Total);
    }

    /// <summary>
    /// Whether the strip still has something true to say. A finished train lingers a few seconds;
    /// a running one is only trusted as far as its own deadline, so a train whose notifications
    /// stopped reaching us cannot sit on screen for the rest of the stream.
    /// </summary>
    public bool IsWorthShowing(DateTimeOffset now) => HasEnded
        ? now - At < EndedLinger
        : ExpiresAt is not { } expires || now < expires;

    /// <summary>
    /// The two moments in a train that are worth a card of their own. A progress update is not one
    /// of them: a bar that moves every few seconds belongs in the dock's strip, and a card per
    /// contribution would bury the chat it sits next to.
    /// </summary>
    public ChatEvent? ToChatEvent() => Phase switch
    {
        HypeTrainPhase.Begin => Card(ChatEventType.HypeTrainBegin, "start"),
        HypeTrainPhase.Ended => Card(ChatEventType.HypeTrainEnd, "slut"),
        _ => null
    };

    // The train's own id plus which moment it is: a begin and an end are two cards from one train.
    private ChatEvent Card(ChatEventType type, string moment) => new(type, $"hype-{Id}-{moment}", string.Empty, At)
    {
        HypeLevel = Level,
        HypeTotal = Total,
        HypeKind = Kind,
        HypeIsShared = IsShared,
        TopContributions = TopContributions
    };
}
