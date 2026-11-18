namespace Arisu.Utilities;

public static class LevelingProgress
{
    public static int GetLevel(long totalXp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalXp);

        var scaledXp = totalXp / 100;
        var level = (int)Math.Sqrt(scaledXp);
        while ((long)level * level > scaledXp)
        {
            level--;
        }

        while ((long)(level + 1) * (level + 1) <= scaledXp)
        {
            level++;
        }

        return level;
    }

    public static long GetRequiredXp(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        return checked(100L * level * level);
    }

    public static long GetXpIntoLevel(long totalXp)
    {
        return totalXp - GetRequiredXp(GetLevel(totalXp));
    }

    public static long GetXpForNextLevel(long totalXp)
    {
        var level = GetLevel(totalXp);
        return 100L * (2L * level + 1);
    }
}
