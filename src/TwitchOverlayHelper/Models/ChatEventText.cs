using System.Globalization;

namespace TwitchOverlayHelper.Models;

/// <summary>
/// Turns an event into the one line that goes on its card. Lives next to the model rather than in
/// either view, so the dock and the overlay always word the same event the same way.
/// </summary>
public static class ChatEventText
{
    public static string Describe(ChatEvent chatEvent) => chatEvent.Type switch
    {
        ChatEventType.Subscription => Subscription(chatEvent),
        ChatEventType.SubGift => $"{chatEvent.DisplayName} gav en prenumeration till {chatEvent.RecipientDisplayName ?? "någon"}{TierSuffix(chatEvent.Tier)}",
        ChatEventType.CommunityGift => $"{chatEvent.DisplayName} gav bort {chatEvent.GiftCount ?? 1} {Plural(chatEvent.GiftCount ?? 1, "prenumeration", "prenumerationer")}{TierSuffix(chatEvent.Tier)}",
        ChatEventType.SubUpgrade => $"{chatEvent.DisplayName} fortsätter som betalande prenumerant",
        ChatEventType.Raid => $"{chatEvent.DisplayName} raidar med {chatEvent.ViewerCount ?? 0} {Plural(chatEvent.ViewerCount ?? 0, "tittare", "tittare")}",
        ChatEventType.Unraid => "Raiden avbröts",
        ChatEventType.Announcement => $"Meddelande från {chatEvent.DisplayName}",
        ChatEventType.BitsBadge => $"{chatEvent.DisplayName} nådde {chatEvent.Bits ?? 0} bits",
        ChatEventType.WatchStreak => $"{chatEvent.DisplayName} har sett {chatEvent.StreakValue ?? 0} sändningar i rad",
        ChatEventType.NewChatter => $"{chatEvent.DisplayName} skriver här för första gången",
        ChatEventType.RewardRedemption => Redemption(chatEvent),
        ChatEventType.ShoutoutSent => $"Shoutout till {chatEvent.RecipientDisplayName ?? "någon"}",
        ChatEventType.ShoutoutReceived => chatEvent.ViewerCount is > 0
            ? $"{chatEvent.DisplayName} gav er en shoutout inför {chatEvent.ViewerCount} tittare"
            : $"{chatEvent.DisplayName} gav er en shoutout",
        ChatEventType.Celebration => chatEvent.Bits is > 0
            ? $"{chatEvent.DisplayName} skickade ett firande för {chatEvent.Bits} bits"
            : $"{chatEvent.DisplayName} skickade ett firande",
        // An unknown msg-id is worth showing: Twitch's own wording beats swallowing the line.
        _ => Trim(chatEvent.SystemMessage) ?? $"{chatEvent.DisplayName} gjorde något i chatten"
    };

    /// <summary>
    /// A redemption is only worth a line if it can name the reward. Without the title all we have
    /// is a GUID, which tells a reader nothing – so it says "en belöning" instead of showing it.
    /// </summary>
    private static string Redemption(ChatEvent chatEvent)
    {
        string what = Trim(chatEvent.RewardTitle) ?? "en belöning";
        return chatEvent.RewardCost is > 0
            ? $"{chatEvent.DisplayName} löste in {what} för {Points(chatEvent.RewardCost.Value)}"
            : $"{chatEvent.DisplayName} löste in {what}";
    }

    /// <summary>Grouped the Swedish way, so 5000 reads as 5 000 rather than as a wall of digits.</summary>
    private static string Points(int cost) =>
        cost.ToString("#,0", CultureInfo.GetCultureInfo("sv-SE")) + " poäng";

    private static string Subscription(ChatEvent chatEvent)
    {
        string tier = TierSuffix(chatEvent.Tier);
        string headline = chatEvent.Months is > 1
            ? $"{chatEvent.DisplayName} har prenumererat i {chatEvent.Months} månader{tier}"
            : $"{chatEvent.DisplayName} prenumererar nu{tier}";
        // The streak is only sent when the viewer chose to share it, and repeating the total as a
        // streak would claim something the tags do not say.
        return chatEvent.StreakMonths is > 1 && chatEvent.StreakMonths != chatEvent.Months
            ? $"{headline} – {chatEvent.StreakMonths} i rad"
            : headline;
    }

    /// <summary>Twitch writes the plan as "Prime" or as a price in cents; readers want neither.</summary>
    private static string TierSuffix(string? tier) => tier switch
    {
        null or "" => string.Empty,
        "Prime" => " (Prime)",
        "1000" => " (nivå 1)",
        "2000" => " (nivå 2)",
        "3000" => " (nivå 3)",
        _ => $" ({tier})"
    };

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
