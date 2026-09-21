using AcDream.Launcher.Core.Launching;

namespace AcDream.Launcher.Core.Tests.Launching;

/// <summary>
/// The console window shows a windowless session's lines in the colours the
/// game shows them in: exactly, when the session colours its own lines, and by
/// the line's tag when it does not.
/// </summary>
public sealed class SessionConsoleTextTests
{
    private const string Esc = "";

    [Fact]
    public void AColouredLineIsShownInThatColourWithNoEscapesLeftInIt()
    {
        IReadOnlyList<SessionConsoleRun> runs = SessionConsoleText.ParseLine(
            Esc + "[38;2;63;191;255m[magic] You resist the spell" + Esc + "[0m");

        SessionConsoleRun run = Assert.Single(runs);
        Assert.Equal("[magic] You resist the spell", run.Text);
        Assert.Equal(0x3FBFFF, run.Color);
        Assert.False(run.Dim);
    }

    [Fact]
    public void ASessionNoticeIsDimWhetherOrNotItSaysSo()
    {
        SessionConsoleRun marked = Assert.Single(
            SessionConsoleText.ParseLine(Esc + "[2m-- entered world" + Esc + "[0m"));
        Assert.Equal("-- entered world", marked.Text);
        Assert.True(marked.Dim);

        SessionConsoleRun plain = Assert.Single(
            SessionConsoleText.ParseLine("-- entered world"));
        Assert.True(plain.Dim);
    }

    [Theory]
    [InlineData("[say] You say, \"hello\"", 0xFFFFFF)]
    [InlineData("[tell] Bob tells you, \"hi\"", 0xFFFF3F)]
    [InlineData("[combat] You evaded Drudge!", 0xFF3F3F)]
    [InlineData("[magic] You resist the spell", 0x3FBFFF)]
    [InlineData("[msg] [MossTank] Loaded settings profile", 0x7FFF7F)]
    public void AnUncolouredLineTakesItsColourFromItsTag(string line, int expected)
    {
        SessionConsoleRun run = Assert.Single(SessionConsoleText.ParseLine(line));
        Assert.Equal(line, run.Text);
        Assert.Equal(expected, run.Color);
    }

    [Fact]
    public void ALineWithNoTagHasNoColourOfItsOwn()
    {
        SessionConsoleRun run = Assert.Single(
            SessionConsoleText.ParseLine("  powered by ACEmulator"));
        Assert.Null(run.Color);
        Assert.False(run.Dim);
    }

    [Fact]
    public void AnEscapeThatIsNotAColourIsDroppedAndTheTextKept()
    {
        IReadOnlyList<SessionConsoleRun> runs = SessionConsoleText.ParseLine(
            "before" + Esc + "[1;31mafter" + Esc + "[Kend");

        Assert.Equal("beforeafter", string.Concat(runs.Take(2).Select(static run => run.Text)));
        Assert.All(runs, static run => Assert.DoesNotContain('', run.Text));
    }
}
