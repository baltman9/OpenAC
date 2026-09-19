using System.Text;
using AcDream.Launcher.Core.Plugins;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginReleaseFeedTests
{
    private const string Repo = "shaneedwards/openac-plugin-hello";

    [Fact]
    public void ReleasesFeedIsTheRepoAtomFeedUrl()
    {
        Uri uri = GitHubReleaseLocator.ReleasesFeed(Repo);

        Assert.Equal(
            new Uri("https://github.com/shaneedwards/openac-plugin-hello/releases.atom"),
            uri);
    }

    [Fact]
    public void APrereleaseEntryLinkIsFound()
    {
        PluginFeedRelease? release = Parse(Entry(Link("v1.3.0-beta.1")));

        Assert.NotNull(release);
        Assert.Equal("v1.3.0-beta.1", release!.Tag);
    }

    [Fact]
    public void AStableEntryLinkDoesNotCount()
    {
        PluginFeedRelease? release = Parse(Entry(Link("v1.3.0")));

        Assert.Null(release);
    }

    [Fact]
    public void ALinkNamingAForeignRepoIsIgnored()
    {
        PluginFeedRelease? release = Parse(Entry(
            $"""<link rel="alternate" href="https://github.com/someone-else/other-plugin/releases/tag/v1.3.0-beta.1"/>"""));

        Assert.Null(release);
    }

    [Fact]
    public void ALinkNamingARenamedRepoIsIgnored()
    {
        PluginFeedRelease? release = Parse(Entry(
            $"""<link rel="alternate" href="https://github.com/shaneedwards/openac-plugin-hello-renamed/releases/tag/v1.3.0-beta.1"/>"""));

        Assert.Null(release);
    }

    [Fact]
    public void AnHttpLinkIsIgnored()
    {
        PluginFeedRelease? release = Parse(Entry(
            $"""<link rel="alternate" href="http://github.com/{Repo}/releases/tag/v1.3.0-beta.1"/>"""));

        Assert.Null(release);
    }

    [Fact]
    public void AForeignHostLinkIsIgnored()
    {
        PluginFeedRelease? release = Parse(Entry(
            $"""<link rel="alternate" href="https://example.com/{Repo}/releases/tag/v1.3.0-beta.1"/>"""));

        Assert.Null(release);
    }

    [Fact]
    public void ALinkWithAQueryIsIgnored()
    {
        PluginFeedRelease? release = Parse(Entry(
            $"""<link rel="alternate" href="https://github.com/{Repo}/releases/tag/v1.3.0-beta.1?foo=bar"/>"""));

        Assert.Null(release);
    }

    [Fact]
    public void ALinkWithExtraPathSegmentsIsIgnored()
    {
        PluginFeedRelease? release = Parse(Entry(
            $"""<link rel="alternate" href="https://github.com/{Repo}/releases/tag/v1.3.0-beta.1/extra"/>"""));

        Assert.Null(release);
    }

    [Fact]
    public void AFeedLevelLinkIsIgnored()
    {
        string feedLevelLink = $"""<link rel="self" href="https://github.com/{Repo}/releases/tag/v1.3.0-beta.1"/>""";
        PluginFeedRelease? release = Parse(feedLevelLink + Entry("<title>no link here</title>"));

        Assert.Null(release);
    }

    [Fact]
    public void ALinkInsideContentIsIgnored()
    {
        string entry = $"""
            <entry>
              <content type="html"><link href="https://github.com/{Repo}/releases/tag/v1.3.0-beta.1"/></content>
            </entry>
            """;
        PluginFeedRelease? release = Parse(entry);

        Assert.Null(release);
    }

    [Fact]
    public void ADtdIsRejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <!DOCTYPE feed [<!ENTITY x "y">]>
            <feed xmlns="http://www.w3.org/2005/Atom"></feed>
            """);

        Assert.Throws<PluginReleaseFeedException>(
            () => PluginReleaseFeed.HighestPrerelease(content, Repo));
    }

    [Fact]
    public void MalformedXmlIsRejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("<feed xmlns=\"http://www.w3.org/2005/Atom\">");

        Assert.Throws<PluginReleaseFeedException>(
            () => PluginReleaseFeed.HighestPrerelease(content, Repo));
    }

    [Fact]
    public void AnEmptyFeedReturnsNull()
    {
        PluginFeedRelease? release = Parse(string.Empty);

        Assert.Null(release);
    }

    [Fact]
    public void ATagWithBuildMetadataIsIgnored()
    {
        PluginFeedRelease? release = Parse(Entry(Link("v1.3.0-beta.1+build.5")));

        Assert.Null(release);
    }

    [Fact]
    public void TheHighestPrereleaseByPrecedenceWins()
    {
        PluginFeedRelease? release = Parse(Entry(Link("v1.3.0-beta.2")) + Entry(Link("v1.3.0-beta.10")));

        Assert.Equal("v1.3.0-beta.10", release!.Tag);
    }

    private static string Link(string tag) =>
        $"""<link rel="alternate" href="https://github.com/{Repo}/releases/tag/{tag}"/>""";

    private static string Entry(string body) => $"<entry>{body}</entry>";

    private static PluginFeedRelease? Parse(string entries)
    {
        byte[] content = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">{entries}</feed>
            """);
        return PluginReleaseFeed.HighestPrerelease(content, Repo);
    }
}
