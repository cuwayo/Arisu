using System.Globalization;
using Arisu.Utilities;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingDisplayTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(1234567L, "1,234,567")]
    [InlineData(long.MaxValue, "9,223,372,036,854,775,807")]
    [InlineData(long.MinValue, "-9,223,372,036,854,775,808")]
    public void NumberUsesInvariantThousandsSeparators(long value, string expected)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.Equal(expected, LevelingDisplay.Number(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u0000\u0001\u202e\u200b")]
    public void LabelUsesUnknownForMissingOrEntirelyUnsafeNames(string? value) =>
        Assert.Equal("Unknown", LevelingDisplay.Label(value));

    [Fact]
    public void LabelCollapsesWhitespaceAndRemovesControlAndFormatCharacters()
    {
        var label = LevelingDisplay.Label("  Alice\t\r\n Bob\u00a0\u00a0日本語\u0000\u202e\u200b  ");

        Assert.Equal("Alice Bob 日本語", label);
    }

    [Fact]
    public void LabelPreservesVisibleUnicodeAndCombiningMarksWhenTheyFit() =>
        Assert.Equal("日本語 🙂 e\u0301", LevelingDisplay.Label("日本語 🙂 e\u0301"));

    [Theory]
    [InlineData("ABCDE", 4, "ABC…")]
    [InlineData("A🙂BC", 3, "A…")]
    [InlineData("A🙂BC", 4, "A🙂…")]
    [InlineData("Ae\u0301BC", 3, "A…")]
    [InlineData("Ae\u0301BC", 4, "Ae\u0301…")]
    [InlineData("👩🏽ABCDE", 4, "…")]
    [InlineData("👩🏽ABCDE", 5, "👩🏽…")]
    [InlineData("ABCDE", 1, "…")]
    public void LabelTruncatesAtTextElementBoundaries(string value, int maximumLength, string expected)
    {
        var label = LevelingDisplay.Label(value, maximumLength);

        Assert.Equal(expected, label);
        Assert.True(label.Length <= maximumLength);
        Assert.DoesNotContain('\ufffd', label);
    }

    [Theory]
    [InlineData(1, "…")]
    [InlineData(3, "Un…")]
    [InlineData(7, "Unknown")]
    public void MissingLabelsRespectTheRequestedBound(int maximumLength, string expected) =>
        Assert.Equal(expected, LevelingDisplay.Label(null, maximumLength));

    [Fact]
    public void LabelPreservesJoinedEmojiSequences() =>
        Assert.Equal("Alice 👩‍💻", LevelingDisplay.Label("Alice 👩‍💻"));

    [Fact]
    public void LabelBoundsScanningOfUnexpectedlyLargeInput()
    {
        Assert.Equal("…", LevelingDisplay.Label(new string('\u0000', 10000) + "late name"));
    }

    [Fact]
    public void LabelBoundsLongNamesAndKeepsAnExactLengthNameUnchanged()
    {
        Assert.Equal(new string('W', 80), LevelingDisplay.Label(new string('W', 80)));
        Assert.Equal($"{new string('W', 79)}…", LevelingDisplay.Label(new string('W', 10000)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LabelRejectsNonPositiveMaximumLength(int maximumLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelingDisplay.Label("name", maximumLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelingDisplay.EmbedLabel("name", maximumLength));
    }

    [Fact]
    public void EmbedLabelRemovesMentionsFormattingAndLineBreaks()
    {
        var text = LevelingDisplay.EmbedLabel("<@123> @everyone\n**name**\u202e");

        Assert.DoesNotContain('@', text);
        Assert.DoesNotContain('<', text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\u202e', text);
        Assert.Equal("＜＠123＞ ＠everyone \\*\\*name\\*\\*", text);
    }

    [Theory]
    [InlineData("\\", "\\\\")]
    [InlineData("*", "\\*")]
    [InlineData("_", "\\_")]
    [InlineData("~", "\\~")]
    [InlineData("`", "\\`")]
    [InlineData("|", "\\|")]
    [InlineData("[", "\\[")]
    [InlineData("]", "\\]")]
    [InlineData("(", "\\(")]
    [InlineData(")", "\\)")]
    [InlineData("<@&123>", "＜＠&123＞")]
    [InlineData("<@!123>", "＜＠!123＞")]
    [InlineData("@here", "＠here")]
    public void EmbedLabelEscapesMarkdownAndNeutralizesMentions(string input, string expected) =>
        Assert.Equal(expected, LevelingDisplay.EmbedLabel(input));

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(99L, 100L)]
    [InlineData(100L, 300L)]
    [InlineData(450L, 500L)]
    [InlineData(long.MaxValue, 60740009900L)]
    public void XpForNextLevelReportsPositiveLevelWidthWithoutOverflow(long totalXp, long expected) =>
        Assert.Equal(expected, LevelingDisplay.XpForNextLevel(totalXp));
}
