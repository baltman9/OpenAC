using System.Xml;
using System.Xml.Linq;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>One prerelease tag found on a repo's releases Atom feed, and the version parsed from
/// it.</summary>
public sealed record PluginFeedRelease(string Tag, LauncherVersion Version);

public sealed class PluginReleaseFeedException : Exception
{
    public PluginReleaseFeedException(string message) : base(message) { }
    public PluginReleaseFeedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Reads a repo's <c>releases.atom</c> feed for the highest-precedence prerelease
/// published there. A <c>link</c> only counts when it is a direct child of an <c>entry</c> (never
/// the feed itself, a <c>title</c>, or inside <c>content</c>) and names, on <c>github.com</c> over
/// https with no query or fragment, exactly <c>/&lt;owner&gt;/&lt;repo&gt;/releases/tag/&lt;tag&gt;</c>
/// for this same repo, where the tag is <c>v</c> plus a SemVer version with a prerelease part and no
/// build metadata.</summary>
public static class PluginReleaseFeed
{
    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <summary>Independent of <see cref="PluginReleaseClient.MaximumDocumentBytes"/>: a 10-entry
    /// feed carries full release notes, so it needs its own, larger allowance.</summary>
    private const long MaxCharactersInDocument = 4_000_000;

    /// <summary>The highest prerelease on the feed for <paramref name="repo"/>, or null when it has
    /// none. Malformed XML or a DTD throws <see cref="PluginReleaseFeedException"/>; the resolver
    /// treats that the same as no beta this time.</summary>
    public static PluginFeedRelease? HighestPrerelease(byte[] content, string repo)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        XDocument document;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharactersInDocument,
        };
        try
        {
            using var stream = new MemoryStream(content);
            using XmlReader reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new PluginReleaseFeedException("the releases feed is not well-formed XML", ex);
        }

        XElement? feed = document.Root;
        if (feed is null || feed.Name != AtomNamespace + "feed")
        {
            throw new PluginReleaseFeedException("the releases feed has no <feed> root");
        }

        PluginFeedRelease? highest = null;
        foreach (XElement entry in feed.Elements(AtomNamespace + "entry"))
        {
            foreach (XElement link in entry.Elements(AtomNamespace + "link"))
            {
                if (TryMatchPrereleaseTag((string?)link.Attribute("href"), repo, out PluginFeedRelease? candidate)
                    && (highest is null || candidate!.Version.CompareTo(highest.Version) > 0))
                {
                    highest = candidate;
                }
            }
        }

        return highest;
    }

    private static bool TryMatchPrereleaseTag(
        string? href, string repo, out PluginFeedRelease? release)
    {
        release = null;
        if (string.IsNullOrEmpty(href)
            || !Uri.TryCreate(href, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] repoParts = repo.Split('/');
        if (segments.Length != 5
            || !string.Equals(segments[0], repoParts[0], StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[1], repoParts[1], StringComparison.OrdinalIgnoreCase)
            || segments[2] != "releases"
            || segments[3] != "tag")
        {
            return false;
        }

        string tag = segments[4];
        if (!tag.StartsWith('v'))
        {
            return false;
        }

        string versionText = tag[1..];
        if (versionText.Contains('+')
            || !LauncherVersion.TryParse(versionText, out LauncherVersion? version)
            || !version.IsPreRelease)
        {
            return false;
        }

        release = new PluginFeedRelease(tag, version);
        return true;
    }
}
