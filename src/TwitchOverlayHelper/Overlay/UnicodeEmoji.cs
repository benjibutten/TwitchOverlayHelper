using System.Globalization;
using System.Text;

namespace TwitchOverlayHelper.Overlay;

internal static class UnicodeEmoji
{
    public static IEnumerable<(string Text, string? ImageCode)> Split(string text)
    {
        if (text.Length == 0) yield break;

        var plainText = new StringBuilder();
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (!TryGetImageCode(element, out string? imageCode))
            {
                plainText.Append(element);
                continue;
            }

            if (plainText.Length > 0)
            {
                yield return (plainText.ToString(), null);
                plainText.Clear();
            }
            yield return (element, imageCode);
        }

        if (plainText.Length > 0)
            yield return (plainText.ToString(), null);
    }

    internal static bool TryGetImageCode(string textElement, out string? imageCode)
    {
        Rune[] runes = textElement.EnumerateRunes().ToArray();
        bool isEmoji = runes.Any(rune => IsEmojiBase(rune.Value))
            || runes.Any(rune => rune.Value is 0xFE0F or 0x20E3)
            || runes.Count(rune => rune.Value is >= 0x1F1E6 and <= 0x1F1FF) >= 2;
        if (!isEmoji)
        {
            imageCode = null;
            return false;
        }

        imageCode = string.Join('-', runes
            .Where(rune => rune.Value != 0xFE0F)
            .Select(rune => rune.Value.ToString("x", CultureInfo.InvariantCulture)));
        return imageCode.Length > 0;
    }

    private static bool IsEmojiBase(int value) =>
        value is >= 0x1F000 and <= 0x1FAFF
        or >= 0x231A and <= 0x231B
        or >= 0x23E9 and <= 0x23F3
        or >= 0x23F8 and <= 0x23FA
        or >= 0x25FD and <= 0x25FE
        or >= 0x2648 and <= 0x2653
        or >= 0x26AA and <= 0x26AB
        or >= 0x26BD and <= 0x26BE
        or >= 0x26C4 and <= 0x26C5
        or >= 0x26F2 and <= 0x26F3
        or >= 0x270A and <= 0x270B
        or >= 0x2753 and <= 0x2755
        or >= 0x2795 and <= 0x2797
        or >= 0x2B1B and <= 0x2B1C
        or 0x2614 or 0x2615 or 0x267F or 0x2693 or 0x26A1 or 0x26CE
        or 0x26D4 or 0x26EA or 0x26F5 or 0x26FA or 0x26FD or 0x2705
        or 0x2728 or 0x274C or 0x274E or 0x2757 or 0x27B0 or 0x27BF
        or 0x2B50 or 0x2B55;
}
