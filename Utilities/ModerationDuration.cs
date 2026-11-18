using System.Globalization;

namespace Arisu.Utilities;

public static class ModerationDuration
{
    public static bool TryParse(string value, out TimeSpan duration)
    {
        duration = default;
        var text = value.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var multiplier = char.ToLowerInvariant(text[^1]) switch
        {
            's' => 1,
            'm' => 60,
            'h' => 3600,
            'd' => 86400,
            _ => 0
        };
        var number = multiplier == 0 ? text : text[..^1];
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        var seconds = (long)amount * (multiplier == 0 ? 60 : multiplier);
        if (seconds > 28 * 86400)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
