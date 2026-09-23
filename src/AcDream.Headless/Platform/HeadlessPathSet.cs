using AcDream.Headless.Configuration;
using AcDream.Platform;

namespace AcDream.Headless.Platform;

/// <summary>
/// The folders a bot process uses. They come from the same resolver the
/// graphical client and the launcher use, so a plugin driven by a bot and a
/// plugin driven by a window read and write the same files.
/// </summary>
internal sealed record HeadlessPathSet(
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory)
{
    /// <summary>How the install root was chosen.</summary>
    internal ApplicationRootSource RootSource { get; init; } =
        ApplicationRootSource.Explicit;

    /// <summary>The shared path set these folders belong to.</summary>
    internal ApplicationPathSet Application =>
        new(ConfigDirectory, DataDirectory, CacheDirectory)
        {
            RootSource = RootSource,
        };

    internal string PluginsDirectory => Application.PluginsDirectory;

    /// <summary>
    /// Where plugin private storage is keyed: each plugin's own files live in
    /// <c>&lt;id&gt;/files</c> beneath it, inside that plugin's folder.
    /// </summary>
    internal string PluginStorageDirectory => Application.PluginStorageDirectory;

    internal string VtankProfilesDirectory => Application.VtankProfilesDirectory;

    internal string PluginPeersDirectory => Application.PluginPeersDirectory;

    internal static HeadlessPathSet Resolve(
        HeadlessPathOverrides overrides,
        IHeadlessPlatformEnvironment? platform = null)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        platform ??= HeadlessPlatformEnvironment.Instance;

        try
        {
            ApplicationPathSet paths = ApplicationPathSet.Resolve(
                overrides.ConfigDirectory,
                overrides.DataDirectory,
                overrides.CacheDirectory,
                platform,
                overrides.RootDirectory);
            return new HeadlessPathSet(
                paths.ConfigDirectory,
                paths.DataDirectory,
                paths.CacheDirectory)
            {
                RootSource = paths.RootSource,
            };
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException
                or NotSupportedException)
        {
            throw new HeadlessConfigurationException(exception.Message);
        }
    }
}
