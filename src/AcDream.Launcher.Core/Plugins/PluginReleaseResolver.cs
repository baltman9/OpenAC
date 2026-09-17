using System.Text;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Which of a repo's releases <see cref="PluginReleaseResolver.ResolveAsync"/> may offer.
/// <see cref="Beta"/> also reads the repo's releases Atom feed for a prerelease (L-319).</summary>
public enum PluginReleaseChannel
{
    Stable,
    Beta,
}

/// <summary>A resolved release: the tag GitHub's redirect named, its manifest bytes, and the
/// manifest parsed from them.</summary>
public sealed record PluginReleaseResolution(string Tag, byte[] ManifestBytes, LauncherPluginManifest Manifest);

public enum PluginReleaseResolveStatus
{
    Success,
    RateLimited,
    Unavailable,
    Invalid,
    Prerelease,
}

public sealed record PluginReleaseResolveResult(
    PluginReleaseResolveStatus Status,
    PluginReleaseResolution? Resolution,
    string? Error)
{
    public static PluginReleaseResolveResult Success(PluginReleaseResolution resolution) =>
        new(PluginReleaseResolveStatus.Success, resolution, null);

    public static readonly PluginReleaseResolveResult RateLimited =
        new(PluginReleaseResolveStatus.RateLimited, null, null);

    public static readonly PluginReleaseResolveResult Unavailable =
        new(PluginReleaseResolveStatus.Unavailable, null, null);

    public static PluginReleaseResolveResult Invalid(string error) =>
        new(PluginReleaseResolveStatus.Invalid, null, error);

    public static PluginReleaseResolveResult Prerelease(string error) =>
        new(PluginReleaseResolveStatus.Prerelease, null, error);
}

/// <summary>Resolves a repo's release (L-308) to its tag and manifest, the one place
/// <c>LauncherPluginComposition.EvaluateUpdateAsync</c>,
/// <c>LauncherPluginsViewModel.RefreshDiscoverDetailsAsync</c> and <c>AddFromUrlAsync</c> agree: the
/// release tag must name the manifest's own version (L-310), and a latest whose version carries a
/// SemVer prerelease part is refused rather than offered (L-319). <c>PluginInstaller.InstallOrUpdateAsync</c>
/// takes its tag pinned from whichever of those already resolved it, so install never re-resolves
/// latest itself. On <see cref="PluginReleaseChannel.Beta"/>, the result is the higher by precedence
/// of that same stable latest and the highest prerelease on the repo's releases feed; a feed that is
/// unavailable, oversized or malformed, or a prerelease whose <c>plugin.json</c> is missing or
/// invalid, falls back to stable silently (no beta this time), and a stable-latest refusal never
/// hides a beta that did resolve.</summary>
public sealed class PluginReleaseResolver
{
    /// <summary>The releases feed's own cap (L-319), independent of
    /// <see cref="PluginReleaseClient.MaximumDocumentBytes"/>: a 10-entry feed carries full release
    /// notes and runs well past it.</summary>
    private const long FeedMaximumBytes = 1024 * 1024;

    private readonly PluginReleaseClient _releaseClient;

    public PluginReleaseResolver(PluginReleaseClient releaseClient)
    {
        _releaseClient = releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
    }

    public async Task<PluginReleaseResolveResult> ResolveAsync(
        string repo,
        PluginReleaseChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        PluginReleaseResolveResult stable = await ResolveStableAsync(repo, cancellationToken)
            .ConfigureAwait(false);
        if (channel != PluginReleaseChannel.Beta)
        {
            return stable;
        }

        PluginReleaseResolveResult? beta = await TryResolveBetaCandidateAsync(repo, cancellationToken)
            .ConfigureAwait(false);
        if (beta is null)
        {
            return stable;
        }

        // A refused or unavailable stable latest must not mask a beta that did resolve.
        if (stable.Status != PluginReleaseResolveStatus.Success)
        {
            return beta;
        }

        LauncherVersion stableVersion = LauncherVersion.Parse(stable.Resolution!.Manifest.Version);
        LauncherVersion betaVersion = LauncherVersion.Parse(beta.Resolution!.Manifest.Version);
        return betaVersion.CompareTo(stableVersion) > 0 ? beta : stable;
    }

    private async Task<PluginReleaseResolveResult> ResolveStableAsync(
        string repo, CancellationToken cancellationToken)
    {
        PluginReleaseFetchResult fetch = await _releaseClient.FetchDocumentAsync(
                GitHubReleaseLocator.LatestAsset(repo, "plugin.json"),
                cancellationToken)
            .ConfigureAwait(false);
        switch (fetch.Status)
        {
            case PluginReleaseFetchStatus.RateLimited:
                return PluginReleaseResolveResult.RateLimited;
            case PluginReleaseFetchStatus.Unavailable:
                return PluginReleaseResolveResult.Unavailable;
        }

        PluginReleaseDocument document = fetch.Document!;
        LauncherPluginManifest manifest;
        try
        {
            manifest = LauncherPluginManifest.Parse(Encoding.UTF8.GetString(document.Content));
        }
        catch (LauncherPluginManifestException ex)
        {
            return PluginReleaseResolveResult.Invalid(ex.Message);
        }

        if (document.Tag is not { } tag || !manifest.MatchesTag(tag))
        {
            return PluginReleaseResolveResult.Invalid(
                $"the release tag does not match plugin version {manifest.Version}");
        }

        if (!LauncherVersion.TryParse(manifest.Version, out LauncherVersion? version))
        {
            return PluginReleaseResolveResult.Invalid(
                $"version '{manifest.Version}' is not a valid SemVer 2.0 version");
        }

        if (version.IsPreRelease)
        {
            return PluginReleaseResolveResult.Prerelease(
                $"the latest release of '{repo}' is a pre-release ({manifest.Version})");
        }

        return PluginReleaseResolveResult.Success(
            new PluginReleaseResolution(tag, document.Content, manifest));
    }

    /// <summary>The highest prerelease on the repo's releases feed, with its own manifest fetched
    /// and matched to its tag. Null covers every way there is no beta to offer this time: the feed
    /// itself is rate-limited, unavailable, oversized or malformed, it has no prerelease entries, or
    /// the candidate's <c>plugin.json</c> is missing, invalid, or doesn't match its tag.</summary>
    private async Task<PluginReleaseResolveResult?> TryResolveBetaCandidateAsync(
        string repo, CancellationToken cancellationToken)
    {
        PluginReleaseFetchResult feedFetch;
        try
        {
            feedFetch = await _releaseClient.FetchDocumentAsync(
                    GitHubReleaseLocator.ReleasesFeed(repo),
                    cancellationToken,
                    maximumBytes: FeedMaximumBytes)
                .ConfigureAwait(false);
        }
        catch (LauncherUpdateException)
        {
            return null;
        }

        if (feedFetch.Status != PluginReleaseFetchStatus.Success)
        {
            return null;
        }

        PluginFeedRelease? candidate;
        try
        {
            candidate = PluginReleaseFeed.HighestPrerelease(feedFetch.Document!.Content, repo);
        }
        catch (PluginReleaseFeedException)
        {
            return null;
        }

        if (candidate is null)
        {
            return null;
        }

        PluginReleaseFetchResult manifestFetch = await _releaseClient.FetchDocumentAsync(
                GitHubReleaseLocator.TaggedAsset(repo, candidate.Tag, "plugin.json"),
                cancellationToken)
            .ConfigureAwait(false);
        if (manifestFetch.Status != PluginReleaseFetchStatus.Success)
        {
            return null;
        }

        LauncherPluginManifest manifest;
        try
        {
            manifest = LauncherPluginManifest.Parse(
                Encoding.UTF8.GetString(manifestFetch.Document!.Content));
        }
        catch (LauncherPluginManifestException)
        {
            return null;
        }

        if (!manifest.MatchesTag(candidate.Tag))
        {
            return null;
        }

        return PluginReleaseResolveResult.Success(
            new PluginReleaseResolution(candidate.Tag, manifestFetch.Document.Content, manifest));
    }
}
