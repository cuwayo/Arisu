using System.Text.RegularExpressions;
using Arisu.Services;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class DiscordAutoModServiceTests
{
    [Theory]
    [InlineData(" Example ", "example")]
    [InlineData("*ExAmPlE*", "*example*")]
    [InlineData("example*", "example*")]
    [InlineData("*example", "*example")]
    [InlineData("two words", "two words")]
    public void NormalizeKeywordPreservesNativeWildcardAndPhraseSemantics(string input, string expected)
    {
        Assert.Equal(expected, DiscordAutoModService.NormalizeKeyword(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("**")]
    [InlineData("* *")]
    [InlineData("**word")]
    [InlineData("word**")]
    [InlineData("wo*rd")]
    [InlineData("two\nwords")]
    public void NormalizeKeywordRejectsBroadOrUnsupportedPatterns(string input)
    {
        Assert.Throws<AutoModConfigurationException>(() => DiscordAutoModService.NormalizeKeyword(input));
    }

    [Fact]
    public void NormalizeKeywordEnforcesNativeLengthLimit()
    {
        Assert.Equal(new string('a', 60), DiscordAutoModService.NormalizeKeyword(new string('A', 60)));
        Assert.Throws<AutoModConfigurationException>(() =>
            DiscordAutoModService.NormalizeKeyword(new string('a', 61)));
    }

    [Theory]
    [InlineData("https://discord.gg/AbC123", true)]
    [InlineData("DISCORD.GG/test-code", true)]
    [InlineData("Join (https://www.discord.com/invite/test).", true)]
    [InlineData("https://discordapp.com/invite/old", true)]
    [InlineData("<https://discord.gg/code>", true)]
    [InlineData("https://discord.gg", false)]
    [InlineData("https://notdiscord.gg/code", false)]
    [InlineData("https://discord.gg.example.com/code", false)]
    [InlineData("https://discord.com/channels/123/456", false)]
    [InlineData("https://discord.com/invites/code", false)]
    [InlineData("discord dot gg/code", false)]
    public void InvitePatternMatchesSupportedLinks(string message, bool expected)
    {
        var matched = Regex.IsMatch(message, DiscordAutoModService.InvitePattern,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.Equal(expected, matched);
    }
}
