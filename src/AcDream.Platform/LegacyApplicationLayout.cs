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
    /// The top-level entries that only an earlier version's data folder has:
    /// its self-update folder, its prepared content and the records naming
    /// it, and its crash reports. The single install folder keeps each of
    /// these one level down (<c>app/</c>, <c>data/</c>, <c>logs/crash/</c>),
    /// so none of them ever appears at the top of an install folder.
    /// </summary>
    public static readonly IReadOnlyList<string> EarlierDataEntries =
    [
        "launcher-update",
        "pak",
        "install.json",
        "install.verification.json",
        "crash-reports",
    ];

    /// <summary>The earlier-version entries at the top of <paramref name="folder"/>.</summary>
    public static IReadOnlyList<string> EarlierDataEntriesIn(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return EarlierDataEntries
            .Where(name => File.Exists(Path.Combine(folder, name))
                || Directory.Exists(Path.Combine(folder, name)))
            .ToArray();
    }

    /// <summary>
    /// Why <paramref name="paths"/> may not be used as an install folder, or
    /// null. An earlier version's data folder named as the install folder
    /// (the same <c>--data-dir</c> or <c>ACDREAM_DATA_DIR</c> it was run
    /// with) would be taken over: its client records rewritten and its
    /// plugins managed in place. It is refused, and left as it is.
    /// </summary>
    public static string? RefusalToUseAsInstallFolder(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        IReadOnlyList<string> found = EarlierDataEntriesIn(paths.RootDirectory);
        if (found.Count == 0)
            return null;

        return $"{paths.RootDirectory} holds files from an earlier OpenAC version "
            + $"({string.Join(", ", found)}), which kept its files differently. OpenAC "
            + "does not use or change that folder. Name a new, empty folder instead "
            + "(--root-dir or ACDREAM_ROOT_DIR; --data-dir and ACDREAM_DATA_DIR also name the "
            + "install folder now), or leave the option out to use the default folder. "
            + "Nothing is copied from the earlier version: add your accounts and plugins again.";
    }

    /// <summary>
    /// The old top-level folders that still exist beside an install found
    /// the ordinary way (the pointer or the default), for telling the player
    /// they are no longer used. A root named on the command line or in the
    /// environment is an isolated install: the per-user folders are not its
    /// business, so it gets none. A folder the running launcher sits in is
    /// left out, so the player is never told to delete the launcher itself.
    /// </summary>
    /// <param name="paths">The install as this program resolved it.</param>
    /// <param name="platform">Where the per-user folders are; the real ones when null.</param>
    /// <param name="launcherDirectory">The running launcher's folder, when a launcher asks.</param>
    public static IReadOnlyList<string> ExistingFolders(
        ApplicationPathSet paths,
        IApplicationPathEnvironment? platform = null,
        string? launcherDirectory = null)
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
            .Where(folder => launcherDirectory is null
                || !ApplicationPathIdentity.IsSameOrInside(launcherDirectory, folder))
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
