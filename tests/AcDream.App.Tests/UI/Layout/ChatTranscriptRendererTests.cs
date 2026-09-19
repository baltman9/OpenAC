using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.Tests.UI.Layout;

public class ChatTranscriptRendererTests
{
    [Theory]
    [InlineData("go to 28.5S, 59.3E", 59.3, -28.5)]
    [InlineData("28.5S, 59.3E", 59.3, -28.5)]
    public void CoordinateText_ParsesCompassCoordinates(string text, double expectedEastWest, double expectedNorthSouth)
    {
        Assert.True(ChatTextTag.TryParseCoordinatePair(text.Replace("go to ", string.Empty), out double eastWest, out double northSouth));
        Assert.Equal(expectedEastWest, eastWest);
        Assert.Equal(expectedNorthSouth, northSouth);
    }

    [Fact]
    public void CoordinateTag_ParsesInvariantWorldCoordinates()
    {
        var spans = ChatTagMarkup.Parse("Go <COORD:World:12.5,-3.25> here");

        ChatTextTag tag = Assert.Single(spans, s => s.Tag is not null).Tag!.Value;
        tag = tag with { Data = "12.5N, 3.25E" };
        Assert.True(tag.TryGetCoordinate(out double eastWest, out double northSouth));
        Assert.Equal(3.25, eastWest);
        Assert.Equal(12.5, northSouth);
    }

    [Theory]
    [InlineData("COORD:World:12.5", false)]
    [InlineData("COORD:Other:12.5,3", false)]
    [InlineData("COORD:World:12.5,not-a-number", false)]
    public void CoordinateTag_RejectsMalformedValues(string tagText, bool expected)
    {
        ChatTextTag tag = new("COORD", tagText.Contains(":World:") ? "World" : "Other", tagText.Contains(":World:") ? tagText[(tagText.IndexOf(":World:", StringComparison.Ordinal) + 7)..] : "12.5,3");

        Assert.Equal(expected, tag.TryGetCoordinate(out _, out _));
    }

    private static float MeasureByCharCount(string s) => s.Length;

    [Fact]
    public void WrapText_EmbeddedNewlines_ProduceOneRenderedLinePerSegment()
    {
        string text = "line one\nline two\nline three";

        // maxW is generous — every segment fits without word-wrapping, so
        // this isolates the newline-split behavior specifically.
        var lines = new List<string>(ChatTranscriptRenderer.WrapText(text, 1000f, MeasureByCharCount));

        Assert.Equal(new[] { "line one", "line two", "line three" }, lines);
    }

    [Fact]
    public void WrapText_CarriageReturnNewline_NormalizesTheSameAsBareNewline()
    {
        string text = "line one\r\nline two";

        var lines = new List<string>(ChatTranscriptRenderer.WrapText(text, 1000f, MeasureByCharCount));

        Assert.Equal(new[] { "line one", "line two" }, lines);
    }

    [Fact]
    public void WrapText_SegmentLongerThanMaxWidth_StillWordWraps()
    {
        // Each segment is independently word-wrapped by the SAME algorithm
        // the single-line path always used — a multi-line server message
        // whose second line overflows the window still wraps that line.
        string text = "short\nthis segment is much too long to fit on one line";

        var lines = new List<string>(ChatTranscriptRenderer.WrapText(text, 10f, MeasureByCharCount));

        Assert.Equal("short", lines[0]);
        Assert.True(lines.Count > 2, "the long second segment should have wrapped into multiple lines");
        Assert.All(lines, line => Assert.True(MeasureByCharCount(line) <= 10f));
        Assert.Equal(
            "this segment is much too long to fit on one line",
            string.Join(" ", lines.Skip(1)));
    }

    [Fact]
    public void WrapText_SingleSegmentText_KeepsTheEarlyOutBehavior()
    {
        string text = "no newlines here";

        var lines = new List<string>(ChatTranscriptRenderer.WrapText(text, 1000f, MeasureByCharCount));

        Assert.Equal(new[] { text }, lines);
    }

    [Fact]
    public void WrapText_ConsecutiveNewlines_ProduceABlankLine()
    {
        string text = "first\n\nthird";

        var lines = new List<string>(ChatTranscriptRenderer.WrapText(text, 1000f, MeasureByCharCount));

        Assert.Equal(new[] { "first", "", "third" }, lines);
    }


    private static float MeasureByWidth(string s) => s.Length;

    private static readonly Vector4 OffWhite = new(0.8f, 0.8f, 0.8f, 1f); // ARGB(255,204,204,204)

    [Fact]
    public void BuildLines_InRangeLogTextType_AlwaysUsesItsOwnTableColor_RegardlessOfDefaultColor()
    {
        var lines = new[] { new FormattedLine("hello", ChatKind.LocalSpeech, null, LogTextType: 0x02u) };

        var withOffWhiteDefault = ChatTranscriptRenderer.BuildLines(lines, 1000f, MeasureByWidth, null, OffWhite);
        var withUnrelatedDefault = ChatTranscriptRenderer.BuildLines(
            lines, 1000f, MeasureByWidth, null, new Vector4(0f, 1f, 0f, 1f));

        Assert.True(RetailChatColorTable.TryGetColor(0x02u, out Vector4 expected));
        Assert.Equal(expected, Assert.Single(withOffWhiteDefault).Color);
        Assert.Equal(expected, Assert.Single(withUnrelatedDefault).Color);
    }

    [Fact]
    public void BuildLines_OutOfRangeLogTextType_AsFirstLine_UsesTheSuppliedDefaultColor()
    {
        var lines = new[] { new FormattedLine("mystery", ChatKind.System, null, LogTextType: 0xFFu) };

        var result = ChatTranscriptRenderer.BuildLines(lines, 1000f, MeasureByWidth, null, OffWhite);

        Assert.Equal(OffWhite, Assert.Single(result).Color);
    }

    [Fact]
    public void BuildLines_OutOfRangeLogTextType_AfterAnInRangeLine_CarriesForwardThePriorTableColor()
    {
        var lines = new[]
        {
            new FormattedLine("says hi", ChatKind.LocalSpeech, null, LogTextType: 0x03u), // Tell -> yellow
            new FormattedLine("mystery", ChatKind.System, null, LogTextType: 0xFFu),      // out of range
        };

        var result = ChatTranscriptRenderer.BuildLines(lines, 1000f, MeasureByWidth, null, OffWhite);

        Assert.True(RetailChatColorTable.TryGetColor(0x03u, out Vector4 tellColor));
        Assert.Equal(2, result.Count);
        Assert.Equal(tellColor, result[0].Color);
        Assert.Equal(tellColor, result[1].Color);
    }

    [Fact]
    public void BuildLines_EmptyDetailed_ReturnsEmpty_RegardlessOfDefaultColor()
    {
        var result = ChatTranscriptRenderer.BuildLines(
            System.Array.Empty<FormattedLine>(), 1000f, MeasureByWidth, null, OffWhite);
        Assert.Empty(result);
    }
}
