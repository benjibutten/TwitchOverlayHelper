namespace TwitchOverlayHelper.Web;

internal sealed record TimeoutRequest(string UserId, int Seconds, string? Reason);
internal sealed record BanRequest(string UserId, string? Reason);
internal sealed record UnbanRequest(string UserId);
internal sealed record DeleteMessageRequest(string MessageId);
internal sealed record SendMessageRequest(string Text);
internal sealed record StartRaidRequest(string UserId);
