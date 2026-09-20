using AcDream.Headless.Configuration;
using AcDream.Headless.Hosting;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Navigation;

namespace AcDream.Headless.Tests;

/// <summary>
/// Every switch that changes what the headless process writes has to be
/// findable in the running guide. A reader who cannot find an option cannot
/// use it, and a stream split nobody documented breaks scripts silently.
/// </summary>
public sealed class HeadlessConsoleDocumentationTests
{
    [Theory]
    [InlineData("--console")]
    [InlineData("--console-stream")]
    [InlineData(HeadlessConsoleOptions.EnvironmentVariable)]
    [InlineData(HeadlessConsoleOptions.StreamEnvironmentVariable)]
    [InlineData(HeadlessConsoleOptions.StdoutValue)]
    [InlineData(HeadlessConsoleOptions.StderrValue)]
    public void TheRunningGuideNamesEveryConsoleOption(string option)
        => Assert.Contains(option, RunningGuide(), StringComparison.Ordinal);

    /// <summary>
    /// Every verb that can be typed at the console has to be findable in the
    /// guide too, taken from the code rather than from a list beside it: the
    /// console's own three, and the verbs the client reserves on the one
    /// command registry both clients hand plugins.
    /// </summary>
    [Theory]
    [InlineData(HeadlessConsoleController.QuitVerb)]
    [InlineData(HeadlessConsoleController.SessionVerb)]
    [InlineData(HeadlessConsoleController.SessionsVerb)]
    [InlineData(RuntimeSessionStatusText.Verb)]
    [InlineData(NavigationChatCommands.NavVerb)]
    [InlineData(NavigationChatCommands.MotorVerb)]
    public void TheRunningGuideNamesEveryVerbTheConsoleAccepts(string verb)
        => Assert.Contains("/" + verb, RunningGuide(), StringComparison.Ordinal);

    /// <summary>
    /// A console serving several sessions needs its addressing documented, or
    /// a second session is invisible to whoever is reading.
    /// </summary>
    [Fact]
    public void TheRunningGuideExplainsAddressingOneOfSeveralSessions()
    {
        string guide = RunningGuide();

        Assert.Contains("@beta", guide, StringComparison.Ordinal);
        Assert.Contains("/session ", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunningGuideSaysWhichStreamTheDiagnosticLinesKeep()
    {
        string guide = RunningGuide();

        Assert.Contains("standard output", guide, StringComparison.Ordinal);
        Assert.Contains("standard error", guide, StringComparison.Ordinal);
    }

    private static string RunningGuide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "docs", "building-and-running.md");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "docs/building-and-running.md was not found above "
            + AppContext.BaseDirectory);
    }
}
