using System.Net;
using System.Text;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher;

/// <summary>One listed plugin not yet installed, as shown on the Discover tab.</summary>
internal sealed record PluginDiscoverEntry(
    string Id,
    string Name,
    string Author,
    string Description,
    string Repo);

/// <summary>An installed plugin's newer release: its version and release tag (pinned through to the
/// update dialog's install, so it never re-resolves latest), its compatibility note when that
/// differs from the row's own (e.g. the new release drops a host the old one supported), and the
/// capabilities that release declares, for the fresh-consent prompt an added one needs.</summary>
internal sealed record PluginUpdateAvailability(
    string Version,
    string Tag,
    string? CompatibilityNote,
    bool CompatibilityIsWarning,
    IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities);

/// <summary>The result of <see cref="LauncherPluginComposition.CheckSingleAsync"/>: either an
/// available update or why one is withheld, mirroring <c>CheckAsync</c>'s two dictionaries for one
/// plugin.</summary>
internal sealed record PluginSingleCheckResult(
    PluginUpdateAvailability? Available, string? WithheldReason)
{
    public static readonly PluginSingleCheckResult None = new(null, null);
}

/// <summary>The result of one Check pass (launcher start or Refresh list): the effective catalog,
/// whether GitHub throttled the request, the cached list's age when a fetch could not be made, and
/// the rows built from it.</summary>
internal sealed record PluginCheckOutcome(
    PluginCatalog? Catalog,
    bool RateLimited,
    DateTimeOffset? ListAgeUtc,
    IReadOnlyList<InstalledPluginInfo> Installed,
    IReadOnlyList<PluginDiscoverEntry> Discover,
    IReadOnlyDictionary<string, PluginUpdateAvailability> UpdatesAvailable,
    IReadOnlyDictionary<string, string> UpdateWithheldReasons);

/// <summary>Composition root for the launcher's plugin install feature (L-300, L-308, L-309),
/// beside <see cref="LauncherUpdateComposition"/>. Builds the release client, the installer, the
/// inventory and the record store; runs recovery once at start; and owns the Check pipeline, which
/// both the start-of-launcher check and "Refresh list" share, and which never blocks launching.</summary>
internal sealed class LauncherPluginComposition : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ApplicationPathSet _paths;
    private readonly string _cacheFilePath;

    private LauncherPluginComposition(
        ApplicationPathSet paths,
        Uri listUri,
        HttpClient httpClient,
        PluginReleaseClient releaseClient,
        InstalledPluginRecordStore recordStore,
        PluginInventory inventory,
        PluginInstaller installer)
    {
        _paths = paths;
        _httpClient = httpClient;
        ListUri = listUri;
        ReleaseClient = releaseClient;
        ReleaseResolver = new PluginReleaseResolver(releaseClient);
        RecordStore = recordStore;
        Inventory = inventory;
        Installer = installer;
        _cacheFilePath = Path.Combine(paths.CacheDirectory, "plugins.json");
    }

    public Uri ListUri { get; }

    public PluginReleaseClient ReleaseClient { get; }

    public PluginReleaseResolver ReleaseResolver { get; }

    public InstalledPluginRecordStore RecordStore { get; }

    public PluginInventory Inventory { get; }

    public PluginInstaller Installer { get; }

    /// <summary>The catalog from the most recent successful or cache-backed Check, for filtering
    /// blocked ids out of the next launched session (L-302).</summary>
    public PluginCatalog? CurrentCatalog { get; private set; }

    public static LauncherPluginComposition Create(ApplicationPathSet paths, Uri listUri)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(listUri);

        var httpClient = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
            },
            disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAC-launcher/1");

        var recordStore = InstalledPluginRecordStore.ForApplicationPaths(paths);
        try
        {
            recordStore.Load();
        }
        catch (LauncherUpdateException)
        {
            // A corrupted record only costs the launcher its memory of which plugins it manages
            // (Update/Remove fall back to unavailable, matching a manual install); it should never
            // keep the window from opening.
        }

        var inventory = new PluginInventory(paths, recordStore);
        var installer = new PluginInstaller(paths, httpClient, recordStore, inventory);
        var releaseClient = new PluginReleaseClient(httpClient);
        return new LauncherPluginComposition(
            paths, listUri, httpClient, releaseClient, recordStore, inventory, installer);
    }

    /// <summary>Builds the same pipeline over an injected transport, so the Check pipeline can be
    /// exercised without a network (in the style of <c>PluginReleaseClient.CreateForTransportTest</c>).</summary>
    internal static LauncherPluginComposition CreateForTest(
        ApplicationPathSet paths, Uri listUri, HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler, disposeHandler: true);
        var recordStore = InstalledPluginRecordStore.ForApplicationPaths(paths);
        try
        {
            recordStore.Load();
        }
        catch (LauncherUpdateException)
        {
        }

        var inventory = new PluginInventory(paths, recordStore);
        var installer = new PluginInstaller(paths, httpClient, recordStore, inventory);
        var releaseClient = new PluginReleaseClient(httpClient);
        return new LauncherPluginComposition(
            paths, listUri, httpClient, releaseClient, recordStore, inventory, installer);
    }

    /// <summary>The launch-time Recovery pipeline (L-309), run once before the inventory is first
    /// read.</summary>
    public void Recover() => Installer.Recover();

    /// <summary>The Check pipeline: fetches the list, falls back to the last cached copy on
    /// failure, and writes a fresh cache on success so a later launch can filter blocked ids even
    /// with no network. Never throws; every failure is reported through the returned outcome.</summary>
    public async Task<PluginCheckOutcome> CheckAsync(
        ClientVersionResolution? clientResolution,
        CancellationToken cancellationToken = default)
    {
        bool rateLimited = false;
        DateTimeOffset? listAge = null;
        PluginCatalog? catalog;
        try
        {
            PluginReleaseFetchResult fetch = await ReleaseClient
                .FetchDocumentAsync(ListUri, cancellationToken)
                .ConfigureAwait(false);
            switch (fetch.Status)
            {
                case PluginReleaseFetchStatus.Success:
                    catalog = PluginCatalog.Parse(Encoding.UTF8.GetString(fetch.Document!.Content));
                    WriteCache(fetch.Document.Content);
                    break;
                case PluginReleaseFetchStatus.RateLimited:
                    rateLimited = true;
                    catalog = TryReadCache(out listAge);
                    break;
                default:
                    catalog = TryReadCache(out listAge);
                    break;
            }
        }
        catch (LauncherUpdateException)
        {
            catalog = TryReadCache(out listAge);
        }

        CurrentCatalog = catalog;

        IReadOnlyList<InstalledPluginInfo> installed = Inventory.Build(clientResolution, catalog);
        var updatesAvailable = new Dictionary<string, PluginUpdateAvailability>(StringComparer.OrdinalIgnoreCase);
        var updateWithheldReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!rateLimited)
        {
            foreach (InstalledPluginInfo info in installed)
            {
                InstalledPluginRecord? record = RecordStore.Find(info.Id);
                if (record is null || record.Pending is not null)
                {
                    continue;
                }

                PluginUpdateCheck check = await EvaluateUpdateAsync(
                    record, catalog, clientResolution, cancellationToken).ConfigureAwait(false);
                if (check.Available)
                {
                    updatesAvailable[info.Id] = new PluginUpdateAvailability(
                        check.Version!, check.Tag!, check.CompatibilityNote, check.CompatibilityIsWarning,
                        check.Capabilities);
                }
                else if (check.WithheldReason is { } reason)
                {
                    updateWithheldReasons[info.Id] = reason;
                }
            }
        }

        var installedIds = new HashSet<string>(
            installed.Select(info => info.Id),
            StringComparer.OrdinalIgnoreCase);
        // A plugin blocked for every version is a dead end (L-314): omit it before it ever costs
        // Discover's per-plugin plugin.json request. A version-specific block still needs that
        // request to know the latest version, so it stays listed here and is judged afterward.
        IReadOnlyList<PluginDiscoverEntry> discover = catalog is null
            ? []
            : [.. catalog.Plugins
                .Where(entry => !installedIds.Contains(entry.Id))
                .Where(entry => !catalog.IsBlockedForAllVersions(entry.Id))
                .Select(entry => new PluginDiscoverEntry(
                    entry.Id, entry.Name, entry.Author, entry.Description, entry.Repo))];

        return new PluginCheckOutcome(
            catalog, rateLimited, listAge, installed, discover, updatesAvailable, updateWithheldReasons);
    }

    /// <summary>Re-checks one already-installed plugin against the most recent Check's catalog,
    /// for a user action on that plugin alone (the beta toggle, L-319) rather than the full pass
    /// every other trigger shares.</summary>
    public async Task<PluginSingleCheckResult> CheckSingleAsync(
        string id,
        ClientVersionResolution? clientResolution,
        CancellationToken cancellationToken = default)
    {
        InstalledPluginRecord? record = RecordStore.Find(id);
        if (record is null || record.Pending is not null)
        {
            return PluginSingleCheckResult.None;
        }

        PluginUpdateCheck check = await EvaluateUpdateAsync(
            record, CurrentCatalog, clientResolution, cancellationToken).ConfigureAwait(false);
        return check.Available
            ? new PluginSingleCheckResult(
                new PluginUpdateAvailability(
                    check.Version!, check.Tag!, check.CompatibilityNote, check.CompatibilityIsWarning,
                    check.Capabilities),
                null)
            : new PluginSingleCheckResult(null, check.WithheldReason);
    }

    /// <summary>An advisory badge, not an enforcement decision: the finer min/max/skip compatibility
    /// gate lives on <see cref="PluginInstaller"/>, which refuses the update itself when the user
    /// acts on it. Names why only when a newer release exists but isn't offered; already-current
    /// stays silent, since nothing is actually being withheld.</summary>
    private async Task<PluginUpdateCheck> EvaluateUpdateAsync(
        InstalledPluginRecord record,
        PluginCatalog? catalog,
        ClientVersionResolution? clientResolution,
        CancellationToken cancellationToken)
    {
        if (record.Version is not { } currentVersionText
            || !LauncherVersion.TryParse(currentVersionText, out LauncherVersion? currentVersion))
        {
            return PluginUpdateCheck.None;
        }

        PluginReleaseResolveResult result;
        try
        {
            result = await ReleaseResolver
                .ResolveAsync(record.Repo, record.Channel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LauncherUpdateException)
        {
            return PluginUpdateCheck.None;
        }

        // Rate-limited, unavailable, an invalid manifest and a prerelease latest are all silent
        // here (L-319): none of them is a newer release actually being withheld.
        if (result.Status != PluginReleaseResolveStatus.Success)
        {
            return PluginUpdateCheck.None;
        }

        PluginReleaseResolution resolution = result.Resolution!;
        LauncherPluginManifest manifest = resolution.Manifest;
        LauncherVersion remoteVersion = LauncherVersion.Parse(manifest.Version);

        // Already current is not withheld: there is no newer release for anything to have held
        // back, so the badge stays silent rather than reporting "not newer".
        if (remoteVersion.CompareTo(currentVersion) <= 0)
        {
            return PluginUpdateCheck.None;
        }

        if (catalog?.IsBlocked(record.Id, remoteVersion) == true)
        {
            return new PluginUpdateCheck(false, "the plugin is blocked", null, null, null, false, []);
        }

        if (manifest.CapabilitiesVersion > LauncherPluginCapabilityVocabulary.Current)
        {
            return new PluginUpdateCheck(false, "needs a newer launcher", null, null, null, false, []);
        }

        // Under a vocabulary this launcher does claim to know, an unrecognized name is a manifest
        // the install will refuse, so offering the update would only fail later.
        if (manifest.UnrecognizedCapabilities.Count > 0)
        {
            return new PluginUpdateCheck(false, "the update's manifest is not valid", null, null, null, false, []);
        }

        string? versionReason = VersionOnlyCompatibility(manifest, clientResolution?.Version);
        if (versionReason is not null)
        {
            return new PluginUpdateCheck(false, versionReason, null, null, null, false, []);
        }

        LauncherPluginCompatibility.CompatibilityDescription compatibility =
            LauncherPluginCompatibility.Describe(manifest, clientResolution?.Version);
        return new PluginUpdateCheck(
            true, null, manifest.Version, resolution.Tag, compatibility.Text, compatibility.IsWarning,
            manifest.Capabilities);
    }

    /// <summary>Host-independent compatibility (min/max/skip host version only): the launch-mode
    /// gate in <see cref="LauncherPluginCompatibility.Evaluate"/> is a per-character concern, so a
    /// panel-wide update badge only reports a reason both hosts agree on.</summary>
    private static string? VersionOnlyCompatibility(
        LauncherPluginManifest manifest, LauncherVersion? clientVersion)
    {
        string? gui = LauncherPluginCompatibility.Evaluate(manifest, LaunchMode.Gui, clientVersion);
        string? headless = LauncherPluginCompatibility.Evaluate(manifest, LaunchMode.Headless, clientVersion);
        return string.Equals(gui, headless, StringComparison.Ordinal) ? gui : null;
    }

    private readonly record struct PluginUpdateCheck(
        bool Available,
        string? WithheldReason,
        string? Version,
        string? Tag,
        string? CompatibilityNote,
        bool CompatibilityIsWarning,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities)
    {
        public static readonly PluginUpdateCheck None = new(false, null, null, null, null, false, []);
    }

    private void WriteCache(byte[] content)
    {
        try
        {
            Directory.CreateDirectory(_paths.CacheDirectory);
            string tempPath = _cacheFilePath + ".tmp";
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private PluginCatalog? TryReadCache(out DateTimeOffset? ageUtc)
    {
        ageUtc = null;
        if (!File.Exists(_cacheFilePath))
        {
            return null;
        }

        try
        {
            ageUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
            return PluginCatalog.Parse(File.ReadAllText(_cacheFilePath));
        }
        catch (Exception ex) when (ex is LauncherUpdateException or IOException)
        {
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
