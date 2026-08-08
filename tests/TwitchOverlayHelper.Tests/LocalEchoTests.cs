using TwitchOverlayHelper.Models;
using TwitchOverlayHelper.Web;

namespace TwitchOverlayHelper.Tests;

/// <summary>
/// Twitch never sends our own line back down the connection that wrote it, so the app builds the
/// echo itself. When USERSTATE carried no id the echo still needs one to be a message at all – and
/// that invented id is the one thing about it that must never reach Helix.
/// </summary>
public sealed class LocalEchoTests
{
    private static ChatMessage Echo(string id, bool local) =>
        new(id, "Benji", "hej", null, [], IsFirstMessage: false, IsHighlighted: false, DateTimeOffset.UnixEpoch)
        {
            UserLogin = "benji",
            IsLocalEcho = local
        };

    /// <summary>
    /// Sent only when true. Every ordinary chat line would otherwise carry a "no" that says nothing,
    /// and during a raid there is one of these per message going over the socket.
    /// </summary>
    [Fact]
    public void AnOrdinaryMessageSaysNothingAboutBeingALocalEcho()
    {
        DockMessage dock = DockMapper.ToDock(Echo("twitch-id", local: false), _ => (null, null));

        Assert.Null(dock.LocalEcho);
        Assert.DoesNotContain("localEcho", DockJson.Serialize(dock), StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag is what lets the dock drop "pin for the viewers" and "delete", which are the two
    /// buttons that hand a message id to Twitch – and Twitch can only answer "no such message" to an
    /// id it never issued.
    /// </summary>
    [Fact]
    public void AnEchoWithoutATwitchIdIsMarkedAsOne()
    {
        DockMessage dock = DockMapper.ToDock(Echo("locally-made-up", local: true), _ => (null, null));

        Assert.True(dock.LocalEcho);
    }
}
