namespace TwitchOverlayHelper.Web;

internal sealed record TimeoutRequest(string UserId, int Seconds, string? Reason);
internal sealed record BanRequest(string UserId, string? Reason);
internal sealed record UnbanRequest(string UserId);
internal sealed record DeleteMessageRequest(string MessageId);
internal sealed record PinMessageRequest(string MessageId);
internal sealed record SendMessageRequest(string Text);
internal sealed record StartRaidRequest(string UserId);
internal sealed record SpeakNameRequest(string? Login, string? DisplayName);
/// <summary>Unix milliseconds: everything said before this moment is put away. Sent as the time of
/// the first line of the current sitting, which is the one thing both sides can agree on.</summary>
internal sealed record TrimHistoryRequest(long Before);
/// <summary>An empty or blank text is a removal – there is no separate endpoint for taking one back.</summary>
internal sealed record SetNicknameRequest(string? UserId, string? Login, string? Text);
/// <summary>Which paid reading the streamer just answered. The verdict is in the path, not here.</summary>
internal sealed record TtsDecisionRequest(string? Id);
