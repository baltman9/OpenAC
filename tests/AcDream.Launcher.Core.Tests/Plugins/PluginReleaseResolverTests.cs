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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("Test request has no URI.");
            return Task.FromResult(respond(uri));
        }
    }
}
