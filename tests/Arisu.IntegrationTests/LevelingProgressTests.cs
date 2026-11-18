using Arisu.Utilities;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingProgressTests
{
    [Theory]
    [InlineData(0, 0L)]
    [InlineData(1, 100L)]
    [InlineData(2, 400L)]
    [InlineData(3, 900L)]
    [InlineData(10, 10000L)]
    [InlineData(1000, 100000000L)]
    public void RequiredXpUsesTheQuadraticLevelThreshold(int level, long expectedXp) =>
        Assert.Equal(expectedXp, LevelingProgress.GetRequiredXp(level));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(1000)]
    [InlineData(303700049)]
    public void LevelChangesExactlyAtTheThresholdIncludingLargeValues(int level)
    {
        var threshold = LevelingProgress.GetRequiredXp(level);

        Assert.Equal(level - 1, LevelingProgress.GetLevel(threshold - 1));
        Assert.Equal(level, LevelingProgress.GetLevel(threshold));
        Assert.Equal(level, LevelingProgress.GetLevel(threshold + 1));
        Assert.Equal(0L, LevelingProgress.GetXpIntoLevel(threshold));
        Assert.Equal(1L, LevelingProgress.GetXpIntoLevel(threshold + 1));
    }

    [Theory]
    [InlineData(0L, 0, 0L, 100L)]
    [InlineData(99L, 0, 99L, 100L)]
    [InlineData(100L, 1, 0L, 300L)]
    [InlineData(125L, 1, 25L, 300L)]
    [InlineData(399L, 1, 299L, 300L)]
    [InlineData(400L, 2, 0L, 500L)]
    [InlineData(899L, 2, 499L, 500L)]
    [InlineData(900L, 3, 0L, 700L)]
    [InlineData(100000000L, 1000, 0L, 200100L)]
    public void ProgressReportsXpIntoTheLevelAndTheFullNextLevelWidth(
        long totalXp, int level, long xpIntoLevel, long xpForNextLevel)
    {
        Assert.Equal(level, LevelingProgress.GetLevel(totalXp));
        Assert.Equal(xpIntoLevel, LevelingProgress.GetXpIntoLevel(totalXp));
        Assert.Equal(xpForNextLevel, LevelingProgress.GetXpForNextLevel(totalXp));
    }

    [Fact]
    public void MaximumXpDoesNotOverflowProgressCalculations()
    {
        const int level = 303700049;

        Assert.Equal(level, LevelingProgress.GetLevel(long.MaxValue));
        Assert.Equal(long.MaxValue - LevelingProgress.GetRequiredXp(level),
            LevelingProgress.GetXpIntoLevel(long.MaxValue));
        Assert.Equal(60740009900L, LevelingProgress.GetXpForNextLevel(long.MaxValue));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void XpCalculationsRejectNegativeTotals(long totalXp)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelingProgress.GetLevel(totalXp));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelingProgress.GetXpIntoLevel(totalXp));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelingProgress.GetXpForNextLevel(totalXp));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RequiredXpRejectsNegativeLevels(int level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelingProgress.GetRequiredXp(level));

    [Theory]
    [InlineData(303700050)]
    [InlineData(int.MaxValue)]
    public void RequiredXpRejectsThresholdsThatCannotFitInALong(int level) =>
        Assert.Throws<OverflowException>(() => LevelingProgress.GetRequiredXp(level));
}
