using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Settings;

namespace TwitchOverlayHelper.Web;

/// <summary>Wire shapes for the dock. Kept flat and explicit so the browser code stays readable.</summary>
internal static class DockJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

internal sealed record DockBadge(string SetId, string Version, string? ImageUrl, string? Title);

internal sealed record DockEmote(string Id, int Start, int Length);

internal sealed record DockMessage(
    string Id,
    string DisplayName,
    string Login,
    string UserId,
    string Text,
    string? Color,
    IReadOnlyList<DockBadge> Badges,
    IReadOnlyList<DockEmote> Emotes,
    bool IsFirstMessage,
    bool IsHighlighted,
    bool IsAction,
    bool IsBroadcaster,
    bool IsModerator,
    long SentAt);

internal sealed record DockStatus(string Text, string State);

internal sealed record DockAuth(bool LoggedIn, string Login, bool CanSend, bool CanRaid, string? Error);

internal sealed record DockHello(
    string Type,
    DockSettings Settings,
    DockStatus Status,
    DockAuth Auth,
    string Channel,
    string MentionName,
    bool SpeechEnabled,
    IReadOnlyList<DockMessage> History);

/// <summary>Whether the speaker button next to every name has anything to call.</summary>
internal sealed record DockSpeech(bool Enabled);

internal sealed record DockEnvelope<T>(string Type, T Payload);

internal sealed record DockModerationPayload(string Kind, string? MessageId, string? UserId, string? Login, int? DurationSeconds);

internal static class DockMapper
{
    public static DockMessage ToDock(ChatMessage message, Func<ChatBadge, (string? Url, string? Title)> badgeLookup)
    {
        var badges = new List<DockBadge>(message.Badges.Count);
        foreach (ChatBadge badge in message.Badges)
        {
            (string? url, string? title) = badgeLookup(badge);
            badges.Add(new DockBadge(badge.SetId, badge.Version, url, title));
        }

        var emotes = new List<DockEmote>(message.Emotes.Count);
        foreach (EmoteSpan emote in message.Emotes)
            emotes.Add(new DockEmote(emote.EmoteId, emote.Start, emote.Length));

        return new DockMessage(
            message.Id,
            message.DisplayName,
            message.UserLogin,
            message.UserId,
            message.Text,
            message.NameColor,
            badges,
            emotes,
            message.IsFirstMessage,
            message.IsHighlighted,
            message.IsAction,
            message.IsBroadcaster,
            message.IsModerator,
            message.SentAt.ToUnixTimeMilliseconds());
    }

    public static DockModerationPayload ToDock(ChatModerationEvent moderation) => new(
        moderation.Kind switch
        {
            ChatEventKind.MessageDeleted => "messageDeleted",
            ChatEventKind.UserPurged => "userPurged",
            _ => "chatCleared"
        },
        moderation.TargetMessageId,
        moderation.TargetUserId,
        moderation.TargetLogin,
        moderation.DurationSeconds);
}
