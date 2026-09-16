using System.Net;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.ViewModels;
using AcDream.Tests.Fixtures.PluginIcons;
using Avalonia.Headless.XUnit;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    private const string IconRepo = "shaneedwards/openac-plugin-hello";
    private const string IconPluginId = "edwards.discoverable";

    [AvaloniaFact]
    public async Task DiscoverRowShowsIconFetchedFromTheTaggedRelease()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            IconPluginId, "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(IconRepo, "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", "plugin.json");
        Uri iconUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", LauncherPluginIcon.FileName);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : request.RequestUri == iconUri
                        ? Ok(PngTestData.Valid())
                        : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.True(row.HasIcon);
        Assert.NotNull(row.Icon);
        Assert.Equal(1, handler.Requests.Count(uri => uri == iconUri));
    }

    [AvaloniaFact]
    public async Task DiscoverIconComesFromDiskCacheWithoutASecondRequest()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            IconPluginId, "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(IconRepo, "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", "plugin.json");
        Uri iconUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", LauncherPluginIcon.FileName);

        var firstHandler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : request.RequestUri == iconUri
                        ? Ok(PngTestData.Valid())
                        : new HttpResponseMessage(HttpStatusCode.NotFound));
        using (LauncherPluginComposition firstComposition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, firstHandler))
        using (var firstOrchestrator = new FakeLauncherOrchestrator())
        using (var firstViewModel = CreateInitialized(firstOrchestrator))
        {
            firstViewModel.ConfigurePlugins(firstComposition, () => null);
            await firstViewModel.Plugins.CheckNowCommand.ExecuteAsync();
            await firstViewModel.Plugins.RefreshDiscoverDetailsAsync();
            Assert.True(Assert.Single(firstViewModel.Plugins.Discover).HasIcon);
        }

        // A fresh viewModel starts with an empty in-memory cache, so this only proves the icon
        // itself came from the disk cache the first pass wrote: the second handler still needs to
        // resolve the manifest, but refuses the icon asset outright.
        var secondHandler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : request.RequestUri == iconUri
                        ? throw new InvalidOperationException(
                            "The icon should have come from the disk cache: " + request.RequestUri)
                        : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition secondComposition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, secondHandler);
        using var secondOrchestrator = new FakeLauncherOrchestrator();
        using var secondViewModel = CreateInitialized(secondOrchestrator);
        secondViewModel.ConfigurePlugins(secondComposition, () => null);
        await secondViewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await secondViewModel.Plugins.RefreshDiscoverDetailsAsync();

        PluginDiscoverRowViewModel row = Assert.Single(secondViewModel.Plugins.Discover);
        Assert.True(row.HasIcon);
        Assert.NotNull(row.Icon);
    }

    [AvaloniaFact]
    public async Task DiscoverIconIsKeptAfterASecondCheckRebuildsTheRow()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            IconPluginId, "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(IconRepo, "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", "plugin.json");
        Uri iconUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", LauncherPluginIcon.FileName);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : request.RequestUri == iconUri
                        ? Ok(PngTestData.Valid())
                        : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();
        Assert.True(Assert.Single(viewModel.Plugins.Discover).HasIcon);

        // A second Check rebuilds every Discover row from scratch; the icon must ride along with
        // the rest of the cached details rather than needing another refresh.
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.True(row.HasIcon);
        Assert.NotNull(row.Icon);
        Assert.Equal(1, handler.Requests.Count(uri => uri == iconUri));
    }

    [AvaloniaFact]
    public async Task DiscoverIconIsNotOfferedForAnUnregisteredAssetAndIsNotRetried()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            IconPluginId, "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(IconRepo, "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", "plugin.json");
        Uri iconUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", LauncherPluginIcon.FileName);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.False(Assert.Single(viewModel.Plugins.Discover).HasIcon);
        Assert.Equal(1, handler.Requests.Count(uri => uri == iconUri));

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.False(Assert.Single(viewModel.Plugins.Discover).HasIcon);
        Assert.Equal(1, handler.Requests.Count(uri => uri == iconUri));
    }

    [AvaloniaFact]
    public async Task DiscoverIconIsNotWrittenForInvalidBytes()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            IconPluginId, "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(IconRepo, "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", "plugin.json");
        Uri iconUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v0.2.0", LauncherPluginIcon.FileName);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : request.RequestUri == iconUri
                        ? Ok(PngTestData.WrongDimensions())
                        : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.False(Assert.Single(viewModel.Plugins.Discover).HasIcon);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.CacheDirectory, "plugin-icons")));
    }

    [AvaloniaFact]
    public async Task DiscoverIconIsNotFetchedWhenTheTagDoesNotMatchTheManifestVersion()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            IconPluginId, "0.2.0", "0.1.0", ["headless", "graphical"]);
        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(IconRepo, "plugin.json");
        // The redirect names a different release than the manifest content actually returned.
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(IconRepo, "v9.9.9", "plugin.json");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : request.RequestUri!.AbsolutePath.EndsWith(
                        LauncherPluginIcon.FileName, StringComparison.Ordinal)
                        ? throw new InvalidOperationException(
                            "A mismatched tag must never fetch an icon: " + request.RequestUri)
                        : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.False(Assert.Single(viewModel.Plugins.Discover).HasIcon);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.CacheDirectory, "plugin-icons")));
    }

    [AvaloniaFact]
    public async Task DiscoverIconIsNotFetchedWhenTheManifestVersionFailsToParse()
    {
        using var fixture = new PluginPanelFixture();
        // Not valid SemVer: "MatchesTag" can still agree with a redirect tag naming this exact
        // (malformed) string, so the version must be parsed, not just string-compared, before it
        // is ever trusted as a file name or URL segment.
        const string malformedVersion = "not-a-version";
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            IconPluginId, malformedVersion, "0.1.0", ["headless", "graphical"]);
        Uri latestManifestUri = GitHubReleaseLocator.LatestAsset(IconRepo, "plugin.json");
        Uri taggedManifestUri = GitHubReleaseLocator.TaggedAsset(
            IconRepo, "v" + malformedVersion, "plugin.json");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : request.RequestUri == latestManifestUri
                ? Redirect(taggedManifestUri)
                : request.RequestUri == taggedManifestUri
                    ? Ok(remoteManifest)
                    : request.RequestUri!.AbsolutePath.EndsWith(
                        LauncherPluginIcon.FileName, StringComparison.Ordinal)
                        ? throw new InvalidOperationException(
                            "An unparsable version must never fetch an icon: " + request.RequestUri)
                        : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.False(Assert.Single(viewModel.Plugins.Discover).HasIcon);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.CacheDirectory, "plugin-icons")));
    }

    [AvaloniaFact]
    public async Task InstalledRowReadsIconFromItsFolder()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", IconRepo, "0.1.0");
        File.WriteAllBytes(
            Path.Combine(fixture.Paths.PluginsDirectory, "edwards.managed", LauncherPluginIcon.FileName),
            PngTestData.Valid());
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel row = Assert.Single(
            viewModel.Plugins.Installed, r => r.Id == "edwards.managed");
        Assert.True(row.HasIcon);
        Assert.NotNull(row.Icon);
    }

    [AvaloniaFact]
    public async Task DisposingThePluginsViewModelDisposesCachedBitmaps()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", IconRepo, "0.1.0");
        File.WriteAllBytes(
            Path.Combine(fixture.Paths.PluginsDirectory, "edwards.managed", LauncherPluginIcon.FileName),
            PngTestData.Valid());
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel row = Assert.Single(
            viewModel.Plugins.Installed, r => r.Id == "edwards.managed");
        Avalonia.Media.Imaging.Bitmap icon = Assert.IsType<Avalonia.Media.Imaging.Bitmap>(row.Icon);

        viewModel.Dispose();

        Assert.Throws<ObjectDisposedException>(() => icon.PixelSize);
    }
}
