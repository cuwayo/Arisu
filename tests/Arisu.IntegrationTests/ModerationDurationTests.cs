using Arisu.Utilities;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class ModerationDurationTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData(" 2H ", 7200)]
    [InlineData("28d", 2419200)]
    [InlineData("15", 900)]
    public void AcceptsSupportedDurations(string input, int seconds)
    {
        Assert.True(ModerationDuration.TryParse(input, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1m")]
    [InlineData("1.5h")]
    [InlineData("1h30m")]
    [InlineData("29d")]
    [InlineData("2147483647d")]
    [InlineData("999999999999999999h")]
    public void RejectsUnsupportedOrOutOfRangeDurations(string input) =>
        Assert.False(ModerationDuration.TryParse(input, out _));
}
