using TwitchOverlayHelper.Overlay;

namespace TwitchOverlayHelper.Tests;

public sealed class UnicodeEmojiTests
{
    [Theory]
    [InlineData("🐴", "1f434")]
    [InlineData("🔄", "1f504")]
    [InlineData("♻️", "267b")]
    [InlineData("👋🏽", "1f44b-1f3fd")]
    [InlineData("👩‍💻", "1f469-200d-1f4bb")]
    [InlineData("🇸🇪", "1f1f8-1f1ea")]
    public void Split_returns_Twemoji_code_for_complete_grapheme(string emoji, string expectedCode)
    {
        (string text, string? imageCode) = Assert.Single(UnicodeEmoji.Split(emoji));

        Assert.Equal(emoji, text);
        Assert.Equal(expectedCode, imageCode);
    }

    [Fact]
    public void Split_keeps_normal_text_in_combined_runs()
    {
        var parts = UnicodeEmoji.Split("Hej 🐴 där").ToArray();

        Assert.Equal(3, parts.Length);
        Assert.Equal(("Hej ", null), parts[0]);
        Assert.Equal(("🐴", "1f434"), parts[1]);
        Assert.Equal((" där", null), parts[2]);
    }
}
