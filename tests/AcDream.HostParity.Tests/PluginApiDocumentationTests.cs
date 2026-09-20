namespace AcDream.HostParity.Tests;

/// <summary>
/// The published plugin guide tells a plugin author what is not available
/// without a window. That list is the census allow-list, so the two are
/// checked against each other: a difference closed here has to leave the
/// guide, and a difference opened here has to enter it. Before this the
/// guide's headless section was a hand-written list that had drifted years
/// behind the code and told authors that most of the surface was inert.
/// </summary>
public sealed class PluginApiDocumentationTests
{
    [Fact]
    public void TheGuideNamesEverySeamAWindowlessClientCannotFill()
    {
        string guide = PluginGuide();

        string[] missing = HostParityAllowList.Seams
            .Where(static entry => entry.MissingHost == ParityHost.Windowless)
            .Select(static entry => entry.Member)
            .Distinct(StringComparer.Ordinal)
            .Where(member => !guide.Contains(member, StringComparison.Ordinal))
            .OrderBy(static member => member, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "The plugin guide does not tell an author about a seam a "
            + "windowless client cannot fill: " + string.Join(", ", missing));
    }

    /// <summary>
    /// A seam neither client fills is not a windowless difference, but a
    /// plugin asking for it gets nothing anywhere, so the guide says so too.
    /// </summary>
    [Fact]
    public void TheGuideNamesEverySeamNeitherClientFills()
    {
        string guide = PluginGuide();

        string[] onBoth = HostParityAllowList.Seams
            .GroupBy(static entry => entry.Member, StringComparer.Ordinal)
            .Where(static group => ParityHost.Both.All(host =>
                group.Any(entry => entry.MissingHost == host)))
            .Select(static group => group.Key)
            .Where(member => !guide.Contains(member, StringComparison.Ordinal))
            .OrderBy(static member => member, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            onBoth.Length == 0,
            "The plugin guide does not tell an author about a seam neither "
            + "client fills: " + string.Join(", ", onBoth));
    }

    /// <summary>
    /// The guide must not warn about a seam that is no longer a difference,
    /// which is what made the old section misleading.
    /// </summary>
    [Fact]
    public void TheGuideDoesNotStillWarnAboutAClosedDifference()
    {
        string guide = PluginGuide();
        var listed = HostParityAllowList.Seams
            .Select(static entry => entry.Member)
            .ToHashSet(StringComparer.Ordinal);

        string[] stale = typeof(AcDream.Runtime.Plugins.RuntimeAutomationSurface)
            .GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .Where(static name => name.StartsWith("Bind", StringComparison.Ordinal)
                // "Bind" itself is a word in the guide's own prose.
                && name.Length > 4)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !listed.Contains(name)
                && guide.Contains(name, StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            stale.Length == 0,
            "The plugin guide still names a seam both clients fill, so it "
            + "tells an author something is missing that is not: "
            + string.Join(", ", stale));
    }

    /// <summary>
    /// A capability one client can only sometimes supply is a difference a
    /// plugin meets in a particular session, so the guide names its reason.
    /// </summary>
    [Fact]
    public void TheGuideNamesEveryConditionalDifference()
    {
        string guide = PluginGuide();

        string[] missing = HostParityAllowList.ConditionalSeams
            .Select(static entry => entry.Member)
            .Distinct(StringComparer.Ordinal)
            .Where(member => !guide.Contains(member, StringComparison.Ordinal)
                && !guide.Contains(
                    "installed data files", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "The plugin guide does not explain a capability a windowless "
            + "client can only sometimes supply: " + string.Join(", ", missing));
    }

    private static string PluginGuide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "docs", "plugin-api.md");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "docs/plugin-api.md was not found above " + AppContext.BaseDirectory);
    }
}
