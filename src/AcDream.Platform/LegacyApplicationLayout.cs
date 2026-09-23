namespace AcDream.Platform;

/// <summary>
/// Where the layout before the single install root kept things: separate
/// settings, data and cache folders named <c>acdream</c> in the operating
/// system's per-user locations. Nothing is brought over from them; they are
/// only read to finish an older launcher's own self-update where it started,
/// and named to the player as folders the new install no longer uses.
/// </summary>
/// <param name="ConfigDirectory">Settings, key bindings, launcher profiles, plugin storage.</param>
/// <param name="DataDirectory">Client versions, prepared content, plugins, logs, the launcher's self-update state.</param>
/// <param name="CacheDirectory">Rebuildable files and client crash reports.</param>
public sealed record LegacyApplicationLayout(
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory)
{
    /// <summary>The per-user folders the old layout used on this operating system.</summary>
    public static LegacyApplicationLayout Detect(
        IApplicationPathEnvironment? platform = null)
    {
        platform ??= ApplicationPathEnvironment.Instance;
        if (platform.IsWindows)
        {
            string roaming = ApplicationPathSet.RequireFolder(
                platform,
                Environment.SpecialFolder.ApplicationData);
            string local = ApplicationPathSet.RequireFolder(
                platform,
                Environment.SpecialFolder.LocalApplicationData);
            return new LegacyApplicationLayout(
                Path.Combine(roaming, "acdream"),
                Path.Combine(local, "acdream"),
                Path.Combine(local, "acdream", "cache"));
        }

        string home = ApplicationPathSet.RequireFolder(
            platform,
            Environment.SpecialFolder.UserProfile);
        if (platform.IsMacOS)
        {
            string data = Path.Combine(
                home,
                "Library",
                "Application Support",
                "acdream");
            return new LegacyApplicationLayout(
                Path.Combine(data, "config"),
                data,
                Path.Combine(home, "Library", "Caches", "acdream"));
        }

        return new LegacyApplicationLayout(
            ApplicationPathSet.ResolveXdg(
                platform,
                "XDG_CONFIG_HOME",
                Path.Combine(home, ".config"),
                "acdream"),
            ApplicationPathSet.ResolveXdg(
                platform,
                "XDG_DATA_HOME",
                Path.Combine(home, ".local", "share"),
                "acdream"),
            ApplicationPathSet.ResolveXdg(
                platform,
                "XDG_CACHE_HOME",
                Path.Combine(home, ".cache"),
                "acdream"));
    }

    /// <summary>
    /// The old top-level folders that still exist beside an install found
    /// the ordinary way (the pointer or the default), for telling the player
    /// they are no longer used. A root named on the command line or in the
    /// environment is an isolated install: the per-user folders are not its
    /// business, so it gets none.
    /// </summary>
    public static IReadOnlyList<string> ExistingFolders(
        ApplicationPathSet paths,
        IApplicationPathEnvironment? platform = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.RootSource is not (ApplicationRootSource.Pointer
            or ApplicationRootSource.Default))
        {
            return [];
        }

        LegacyApplicationLayout old = Detect(platform);
        return old.TopLevelRoots()
            .Where(Directory.Exists)
            .Where(folder => !ApplicationPathIdentity.IsSameOrInside(paths.RootDirectory, folder)
                && !ApplicationPathIdentity.IsSameOrInside(folder, paths.RootDirectory))
            .ToArray();
    }

    /// <summary>
    /// The top-level folders of the old layout: every old folder that is not
    /// inside another one.
    /// </summary>
    public IReadOnlyList<string> TopLevelRoots()
    {
        string[] candidates = [ConfigDirectory, DataDirectory, CacheDirectory];
        var roots = new List<string>();
        foreach (string candidate in candidates)
        {
            bool nested = candidates.Any(other =>
                !ApplicationPathIdentity.Equals(other, candidate)
                && ApplicationPathIdentity.IsSameOrInside(candidate, other));
            if (!nested && !roots.Any(root => ApplicationPathIdentity.Equals(root, candidate)))
                roots.Add(candidate);
        }

        return roots;
    }
}
