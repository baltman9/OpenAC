using AcDream.Headless.Configuration;

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
