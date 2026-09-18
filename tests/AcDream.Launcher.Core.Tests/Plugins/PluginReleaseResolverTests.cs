using System.Net;
using System.Text;
using AcDream.Launcher.Core.Plugins;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginReleaseResolverTests
{
    private const string Repo = "shaneedwards/openac-plugin-hello";

    [Fact]
    public async Task StableLatestResolvesItsTagAndManifest()
    {
        Uri latestUri = GitHubReleaseLocator.LatestAsset(Repo, "plugin.json");
        Uri taggedUri = GitHubReleaseLocator.TaggedAsset(Repo, "v0.2.0", "plugin.json");
        byte[] manifestBytes = ManifestJson("0.2.0");
        var handler = new RoutedHandler(uri => uri == latestUri
            ? Redirect(taggedUri)
            : uri == taggedUri
                ? Ok(manifestBytes)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Stable);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v0.2.0", result.Resolution!.Tag);
        Assert.Equal("0.2.0", result.Resolution.Manifest.Version);
        Assert.Equal(manifestBytes, result.Resolution.ManifestBytes);
    }

    [Fact]
    public async Task APrereleaseLatestIsRefused()
    {
        Uri latestUri = GitHubReleaseLocator.LatestAsset(Repo, "plugin.json");
        Uri taggedUri = GitHubReleaseLocator.TaggedAsset(Repo, "v1.3.0-beta.1", "plugin.json");
        var handler = new RoutedHandler(uri => uri == latestUri
            ? Redirect(taggedUri)
            : uri == taggedUri
                ? Ok(ManifestJson("1.3.0-beta.1"))
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Stable);

        Assert.Equal(PluginReleaseResolveStatus.Prerelease, result.Status);
        Assert.Null(result.Resolution);
    }

    [Fact]
    public async Task ATagThatDoesNotMatchTheManifestVersionIsInvalid()
    {
        Uri latestUri = GitHubReleaseLocator.LatestAsset(Repo, "plugin.json");
        Uri taggedUri = GitHubReleaseLocator.TaggedAsset(Repo, "v0.2.0", "plugin.json");
        var handler = new RoutedHandler(uri => uri == latestUri
            ? Redirect(taggedUri)
            : uri == taggedUri
                ? Ok(ManifestJson("0.3.0"))
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Stable);

        Assert.Equal(PluginReleaseResolveStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task A429IsRateLimited()
    {
        var handler = new RoutedHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Stable);

        Assert.Equal(PluginReleaseResolveStatus.RateLimited, result.Status);
    }

    [Fact]
    public async Task A404IsUnavailable()
    {
        var handler = new RoutedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Stable);

        Assert.Equal(PluginReleaseResolveStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task BetaOffersTheHigherPrecedenceBetaOverStable()
    {
        var handler = BetaHandler(stableVersion: "1.3.0", betaVersion: "1.4.0-beta.1");
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.4.0-beta.1", result.Resolution!.Tag);
    }

    [Fact]
    public async Task BetaPrefersStableWhenStableIsTheHigherPrecedence()
    {
        var handler = BetaHandler(stableVersion: "1.3.0", betaVersion: "1.3.0-beta.2");
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.3.0", result.Resolution!.Tag);
    }

    [Fact]
    public async Task ABetaWhoseManifestIs404FallsBackToStable()
    {
        var handler = BetaHandler(
            stableVersion: "1.3.0", betaVersion: "1.4.0-beta.1", betaManifestMissing: true);
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.3.0", result.Resolution!.Tag);
    }

    [Fact]
    public async Task ABetaWhoseManifestVersionDoesNotMatchItsTagFallsBackToStable()
    {
        var handler = BetaHandler(
            stableVersion: "1.3.0", betaVersion: "1.4.0-beta.1", betaManifestVersion: "1.5.0-beta.1");
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.3.0", result.Resolution!.Tag);
    }

    [Fact]
    public async Task AnOversizedFeedFallsBackToStable()
    {
        var handler = BetaHandler(
            stableVersion: "1.3.0", betaVersion: "1.4.0-beta.1", feedOversized: true);
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.3.0", result.Resolution!.Tag);
    }

    [Fact]
    public async Task AMalformedFeedFallsBackToStable()
    {
        var handler = BetaHandler(
            stableVersion: "1.3.0", betaVersion: "1.4.0-beta.1", feedMalformed: true);
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.3.0", result.Resolution!.Tag);
    }

    [Fact]
    public async Task ARefusedStableLatestDoesNotMaskAResolvedBeta()
    {
        // The "latest" release is itself a prerelease: Stable refuses it, but a valid beta from the
        // feed must still be offered rather than reporting nothing.
        var handler = BetaHandler(stableVersion: "1.3.0-beta.9", betaVersion: "1.4.0-beta.1");
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseResolveResult result = await resolver.ResolveAsync(Repo, PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.4.0-beta.1", result.Resolution!.Tag);
    }

    [Fact]
    public void ForBetaReturnsStableWhenBetaIsNull()
    {
        PluginReleaseResolution resolution = new("v0.2.0", ManifestJson("0.2.0"), ParsedManifest("0.2.0"));
        var candidates = new PluginReleaseCandidates(PluginReleaseResolveResult.Success(resolution), null);

        Assert.Same(candidates.Stable, candidates.For(PluginReleaseChannel.Beta));
    }

    [Fact]
    public void ForBetaReturnsBetaWhenStableFailed()
    {
        PluginReleaseResolution betaResolution =
            new("v1.4.0-beta.1", ManifestJson("1.4.0-beta.1"), ParsedManifest("1.4.0-beta.1"));
        var candidates = new PluginReleaseCandidates(
            PluginReleaseResolveResult.Unavailable, PluginReleaseResolveResult.Success(betaResolution));

        PluginReleaseResolveResult result = candidates.For(PluginReleaseChannel.Beta);

        Assert.Equal(PluginReleaseResolveStatus.Success, result.Status);
        Assert.Equal("v1.4.0-beta.1", result.Resolution!.Tag);
    }

    [Fact]
    public void ForBetaReturnsTheHigherOfTheTwoByPrecedence()
    {
        PluginReleaseResolution stableResolution =
            new("v1.3.0", ManifestJson("1.3.0"), ParsedManifest("1.3.0"));
        PluginReleaseResolution higherBetaResolution =
            new("v1.4.0-beta.1", ManifestJson("1.4.0-beta.1"), ParsedManifest("1.4.0-beta.1"));
        PluginReleaseResolution lowerBetaResolution =
            new("v1.3.0-beta.2", ManifestJson("1.3.0-beta.2"), ParsedManifest("1.3.0-beta.2"));
        var stable = PluginReleaseResolveResult.Success(stableResolution);
        var higherBeta = new PluginReleaseCandidates(
            stable, PluginReleaseResolveResult.Success(higherBetaResolution));
        var lowerBeta = new PluginReleaseCandidates(
            stable, PluginReleaseResolveResult.Success(lowerBetaResolution));

        Assert.Equal("v1.4.0-beta.1", higherBeta.For(PluginReleaseChannel.Beta).Resolution!.Tag);
        Assert.Equal("v1.3.0", lowerBeta.For(PluginReleaseChannel.Beta).Resolution!.Tag);
    }

    [Fact]
    public async Task ResolveCandidatesAsyncOnStableDoesNotFetchTheReleasesFeed()
    {
        var handler = BetaHandler(stableVersion: "1.3.0", betaVersion: "1.4.0-beta.1");
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseCandidates candidates =
            await resolver.ResolveCandidatesAsync(Repo, PluginReleaseChannel.Stable);

        Assert.Null(candidates.Beta);
        Assert.DoesNotContain(GitHubReleaseLocator.ReleasesFeed(Repo), handler.Requests);
    }

    [Fact]
    public async Task ResolveCandidatesAsyncOnBetaFetchesTheReleasesFeed()
    {
        var handler = BetaHandler(stableVersion: "1.3.0", betaVersion: "1.4.0-beta.1");
        var resolver = new PluginReleaseResolver(PluginReleaseClient.CreateForTransportTest(handler));

        PluginReleaseCandidates candidates =
            await resolver.ResolveCandidatesAsync(Repo, PluginReleaseChannel.Beta);

        Assert.NotNull(candidates.Beta);
        Assert.Contains(GitHubReleaseLocator.ReleasesFeed(Repo), handler.Requests);
    }

    private static LauncherPluginManifest ParsedManifest(string version) =>
        LauncherPluginManifest.Parse(Encoding.UTF8.GetString(ManifestJson(version)));

    private static RoutedHandler BetaHandler(
        string stableVersion,
        string betaVersion,
        bool betaManifestMissing = false,
        string? betaManifestVersion = null,
        bool feedOversized = false,
        bool feedMalformed = false)
    {
        Uri latestUri = GitHubReleaseLocator.LatestAsset(Repo, "plugin.json");
        Uri stableTaggedUri = GitHubReleaseLocator.TaggedAsset(Repo, "v" + stableVersion, "plugin.json");
        Uri feedUri = GitHubReleaseLocator.ReleasesFeed(Repo);
        string betaTag = "v" + betaVersion;
        Uri betaTaggedUri = GitHubReleaseLocator.TaggedAsset(Repo, betaTag, "plugin.json");

        byte[] feedBytes = Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" href="https://github.com/{{Repo}}/releases/tag/{{betaTag}}"/></entry>
            </feed>
            """);

        return new RoutedHandler(uri =>
        {
            if (uri == latestUri)
            {
                return Redirect(stableTaggedUri);
            }

            if (uri == stableTaggedUri)
            {
                return Ok(ManifestJson(stableVersion));
            }

            if (uri == feedUri)
            {
                if (feedMalformed)
                {
                    return Ok(Encoding.UTF8.GetBytes("<feed xmlns=\"http://www.w3.org/2005/Atom\">"));
                }

                HttpResponseMessage response = Ok(feedBytes);
                if (feedOversized)
                {
                    response.Content.Headers.ContentLength = 2 * 1024 * 1024;
                }

                return response;
            }

            if (uri == betaTaggedUri)
            {
                if (betaManifestMissing)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return Ok(ManifestJson(betaManifestVersion ?? betaVersion));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static byte[] ManifestJson(string version) => Encoding.UTF8.GetBytes($$"""
        {
          "id": "edwards.hello",
          "displayName": "Hello",
          "version": "{{version}}",
          "entryDll": "Hello.dll",
          "apiVersion": 1
        }
        """);

    private static HttpResponseMessage Redirect(Uri location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = location;
        return response;
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    private sealed class RoutedHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("Test request has no URI.");
            Requests.Add(uri);
            return Task.FromResult(respond(uri));
        }
    }
}
