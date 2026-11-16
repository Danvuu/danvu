using Danvu.Core;

namespace Danvu.Core.Tests;

public sealed class LrcParserTests
{
    [Fact]
    public void ImportedLyricsPreserveTitleSourceAndChronologicalOrder()
    {
        var lyrics = LrcParser.Parse("[ar:Original artist]\r\n[00:12.25]Second\r\n[00:02.5]First\r\n", "My track");

        Assert.Equal("My track", lyrics.Title);
        Assert.Equal("Local LRC file", lyrics.Source);
        Assert.Equal(new[] { new LyricLine(2500, "First"), new LyricLine(12250, "Second") }, lyrics.Lines);
    }

    [Theory]
    [InlineData("[01:02]Line", 62000)]
    [InlineData("[01:02.1]Line", 62100)]
    [InlineData("[01:02.12]Line", 62120)]
    [InlineData("[01:02.123]Line", 62123)]
    [InlineData("[01:02:123]Line", 62123)]
    public void SupportsCommonLrcTimestampFormats(string content, int milliseconds)
    {
        Assert.Equal(new LyricLine(milliseconds, "Line"), Assert.Single(LrcParser.Parse(content).Lines));
    }

    [Fact]
    public void RepeatedTimestampTagsCreateSeparateTimedLines()
    {
        var lyrics = LrcParser.Parse("[00:03.50][00:10.25]  Refrain  ");

        Assert.Equal(new[] { new LyricLine(3500, "Refrain"), new LyricLine(10250, "Refrain") }, lyrics.Lines);
    }

    [Theory]
    [InlineData("[offset:+200]", 1200)]
    [InlineData("[offset:-200]", 800)]
    [InlineData("[offset:-2000]", 0)]
    public void OffsetAppliesToAllLinesWithoutNegativeTimestamps(string offset, int expected)
    {
        var lyrics = LrcParser.Parse($"[00:01.00]Line\n{offset}");

        Assert.Equal(expected, Assert.Single(lyrics.Lines).TimeMs);
    }

    [Fact]
    public void InvalidOrUntimedEntriesDoNotBecomeClickableLyrics()
    {
        var lyrics = LrcParser.Parse("[ti:A title]\n[ar:An artist]\nplain text\n[00:60.00]Invalid seconds\n[00:01.00]   \n[00:02.00]Valid");

        Assert.Equal(new LyricLine(2000, "Valid"), Assert.Single(lyrics.Lines));
    }

    [Fact]
    public void EmptyDocumentHasNoActiveLine()
    {
        var document = LrcParser.Parse(string.Empty);

        Assert.Empty(document.Lines);
        Assert.Equal(-1, LrcParser.FindActiveLine(document.Lines, 10000));
    }

    [Theory]
    [InlineData("[offset:2147483647]", int.MaxValue)]
    [InlineData("[offset:-2147483648]", 0)]
    public void ExtremeOffsetsClampWithoutIntegerOverflow(string offset, int expected)
    {
        var lyrics = LrcParser.Parse(offset + "\n[00:01.000]Line");

        Assert.Equal(expected, Assert.Single(lyrics.Lines).TimeMs);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(999, -1)]
    [InlineData(1000, 0)]
    [InlineData(2999, 0)]
    [InlineData(3000, 1)]
    [InlineData(999999, 2)]
    public void ActiveLineFollowsPlaybackAtExactBoundaries(int position, int expected)
    {
        LyricLine[] lines = [new(1000, "One"), new(3000, "Two"), new(7000, "Three")];

        Assert.Equal(expected, LrcParser.FindActiveLine(lines, position));
    }
}
