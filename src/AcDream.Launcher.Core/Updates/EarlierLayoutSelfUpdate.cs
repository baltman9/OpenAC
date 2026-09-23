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
    /// The data folder an earlier launcher started the same way would have
    /// used. A data folder named on the command line or in the environment
    /// was its data folder too; otherwise it was the per-user default.
    /// </summary>
    public static string DataDirectoryFor(
        ApplicationPathSet paths,
        IApplicationPathEnvironment? platform = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.RootSource is ApplicationRootSource.Pointer
            or ApplicationRootSource.Default
            ? LegacyApplicationLayout.Detect(platform).DataDirectory
            : paths.DataDirectory;
    }

    /// <summary>The manager for the earlier launcher's self-update state.</summary>
    public static LauncherSelfUpdateManager ManagerFor(
        ApplicationPathSet paths,
        HttpClient httpClient,
        IApplicationPathEnvironment? platform = null) =>
        LauncherSelfUpdateManager.ForEarlierLayout(
            DataDirectoryFor(paths, platform),
            httpClient);
}
