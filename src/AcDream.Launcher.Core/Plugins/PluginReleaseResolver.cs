using System.Text;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Which of a repo's releases <see cref="PluginReleaseResolver.ResolveAsync"/> may offer.
/// Only <see cref="Stable"/> is implemented; a beta channel will also read the releases Atom feed
/// (L-319).</summary>
public enum PluginReleaseChannel
{
    Stable,
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

/// <summary>Resolves a repo's <c>releases/latest</c> plugin release (L-308) to its tag and manifest,
/// the one place <c>LauncherPluginComposition.EvaluateUpdateAsync</c>,
/// <c>LauncherPluginsViewModel.RefreshDiscoverDetailsAsync</c> and <c>AddFromUrlAsync</c> agree: the
/// release tag must name the manifest's own version (L-310), and a latest whose version carries a
/// SemVer prerelease part is refused rather than offered (L-319). <c>PluginInstaller.InstallOrUpdateAsync</c>
/// takes its tag pinned from whichever of those already resolved it, so install never re-resolves
/// latest itself.</summary>
public sealed class PluginReleaseResolver
{
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
        // Stable is the only channel implemented yet (L-319); channel is already on the signature
        // so a later beta channel is a third arm here, not a new one at every call site.
        _ = channel;

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
}
