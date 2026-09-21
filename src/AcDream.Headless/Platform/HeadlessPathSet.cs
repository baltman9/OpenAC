using AcDream.Headless.Configuration;
using AcDream.Platform;

namespace AcDream.Headless.Platform;

internal sealed record HeadlessPathSet(
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory)
{
    internal string PluginsDirectory =>
        Path.Combine(DataDirectory, "plugins");

    /// <summary>
    /// Where a plugin's own persisted state lives. The same place the
    /// graphical host uses, so a plugin driven by a bot and a plugin driven
    /// by a window read and write the same files.
    /// </summary>
    internal string PluginStorageDirectory =>
        Path.Combine(ConfigDirectory, "plugins");

    internal string VtankProfilesDirectory =>
        Path.Combine(DataDirectory, "vtank");

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
                platform);
            return new HeadlessPathSet(
                paths.ConfigDirectory,
                paths.DataDirectory,
                paths.CacheDirectory);
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
