using AcDream.Platform;

namespace AcDream.Launcher.Core.Updates;

/// <summary>
/// Where a launcher from before the single install folder kept its
/// self-update state. When such a launcher updates itself, it stages this
/// version in its own data folder and starts it from there as the helper;
/// the helper then starts the installed copy as the confirmer. Both must
/// find the transaction where the earlier launcher wrote it, and finish it
/// there, before this version starts on its own install folder.
/// </summary>
public static class EarlierLayoutSelfUpdate
{
    /// <summary>
    /// The data folders an earlier launcher started the same way could have
    /// used, most specific first: a data folder named on the command line or
    /// in the environment (the earlier launcher read the same names), then
    /// the per-user default it used otherwise. Only a transaction that
    /// matches the running launcher exactly is ever acted on, so looking in
    /// both is safe.
    /// </summary>
    public static IReadOnlyList<string> DataDirectoriesFor(
        ApplicationPathSet paths,
        IApplicationPathEnvironment? platform = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string perUserDefault = LegacyApplicationLayout.Detect(platform).DataDirectory;
        if (paths.RootSource is ApplicationRootSource.Pointer or ApplicationRootSource.Default
            || ApplicationPathIdentity.Equals(paths.DataDirectory, perUserDefault))
        {
            return [perUserDefault];
        }

        return [paths.DataDirectory, perUserDefault];
    }

    /// <summary>A manager for each place the earlier launcher's self-update state could be.</summary>
    public static IReadOnlyList<LauncherSelfUpdateManager> ManagersFor(
        ApplicationPathSet paths,
        HttpClient httpClient,
        IApplicationPathEnvironment? platform = null) =>
        DataDirectoriesFor(paths, platform)
            .Select(data => LauncherSelfUpdateManager.ForEarlierLayout(data, httpClient))
            .ToArray();
}
