using AcDream.Platform;

namespace AcDream.Launcher.Core.Launching;

/// <summary>
/// The variables every client and windowless host the launcher starts gets,
/// so a child resolves exactly the folders the launcher itself uses rather
/// than guessing from a pointer file or whatever the shell exported.
/// </summary>
public static class LauncherChildEnvironment
{
    /// <summary>The root, and each member spelled out so none can drift.</summary>
    public static IReadOnlyDictionary<string, string> For(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ApplicationPathSet.RootEnvironmentVariable] = paths.RootDirectory,
            [ApplicationPathSet.ConfigEnvironmentVariable] = paths.ConfigDirectory,
            [ApplicationPathSet.DataEnvironmentVariable] = paths.DataDirectory,
            [ApplicationPathSet.CacheEnvironmentVariable] = paths.CacheDirectory,
        };
    }
}
