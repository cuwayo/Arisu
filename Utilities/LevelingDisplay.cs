using System.Globalization;
using System.Text;

namespace Arisu.Utilities;

public static class LevelingDisplay
{
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Label(string? value, int maximumLength = 80)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);

        var builder = new StringBuilder();
        var scanned = 0;
        foreach (var rune in (value ?? string.Empty).EnumerateRunes())
        {
            // Discord labels are short; bound work even for unexpected input.
            scanned += rune.Utf16SequenceLength;
            if (scanned > 4096)
            {
                builder.Append('…');
                break;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            else if (rune.Value == 0x200D ||
                     Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format))
            {
                builder.Append(rune.ToString());
            }
        }

        var text = builder.ToString().Trim();
        if (text.Length == 0)
        {
            text = "Unknown";
        }

        if (text.Length <= maximumLength)
        {
            return text;
        }

        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var end = boundaries.LastOrDefault(index => index <= maximumLength - 1);
        return $"{text[..end]}…";
    }

    public static string EmbedLabel(string? value, int maximumLength = 80)
    {
        var builder = new StringBuilder();
        foreach (var character in Label(value, maximumLength))
        {
            switch (character)
            {
                case '@':
                    builder.Append('＠');
                    break;
                case '<':
                    builder.Append('＜');
                    break;
                case '>':
                    builder.Append('＞');
                    break;
                case '\\' or '*' or '_' or '~' or '`' or '|' or '[' or ']' or '(' or ')':
                    builder.Append('\\').Append(character);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    public static long XpForNextLevel(long totalXp) => LevelingProgress.GetXpForNextLevel(totalXp);
}
