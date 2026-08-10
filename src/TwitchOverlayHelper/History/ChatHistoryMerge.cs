using TwitchOverlayHelper.Models;

namespace TwitchOverlayHelper.History;

/// <summary>
/// Puts two accounts of the same chat together: the lines this app saw itself, and the lines the
/// recent-messages service kept for the stretch we were away. They overlap – the service holds
/// everything, including what we already have – so the join has to be by identity rather than by
/// appending, or a restart would show every line twice.
/// </summary>
public static class ChatHistoryMerge
{
    /// <summary>
    /// One timeline, oldest first, newest <paramref name="limit"/> lines kept.
    ///
    /// <para>Where the two sources disagree about a line, <paramref name="mine"/> wins: our own copy
    /// has been through reward-name and power-up enrichment that the raw feed knows nothing about,
    /// so taking the fetched version would quietly strip a redemption of its name.</para>
    /// </summary>
    public static IReadOnlyList<ChatTimelineItem> Combine(
        IReadOnlyList<ChatTimelineItem> mine,
        IReadOnlyList<ChatTimelineItem> fetched,
        int limit,
        TimeSpan maxAge,
        DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - maxAge;
        Dictionary<string, ChatTimelineItem> byId = new(StringComparer.Ordinal);
        List<string> order = [];

        // Fetched first so our own copy overwrites it in place – the line keeps the position the
        // service gave it, and the content we already had.
        foreach (ChatTimelineItem item in fetched.Concat(mine))
        {
            if (IdOf(item) is not { } key) continue;
            if (item.At < cutoff) continue;
            if (!byId.ContainsKey(key)) order.Add(key);
            byId[key] = item;
        }

        List<ChatTimelineItem> merged = order.Select(key => byId[key])
            .OrderBy(item => item.At)
            .ToList();
        return merged.Count > limit ? merged[^limit..] : merged;
    }

    /// <summary>
    /// What makes a line itself. Twitch's message id is the honest answer and is on everything that
    /// came off the wire; a line without one is ours alone – a local echo – and cannot be a duplicate
    /// of anything the service has, so it is kept under a key of its own.
    ///
    /// <para>Shared rather than private because the same question is asked in a second place: before
    /// the overlay is redrawn, the lines still waiting to be drawn on it have to be told apart from
    /// the ones the redraw already covers.</para>
    /// </summary>
    internal static string? IdOf(ChatTimelineItem item) =>
        item.Message is { } message ? "m:" + message.Id
        : item.Event is { } chatEvent ? "e:" + chatEvent.Id
        : null;
}
