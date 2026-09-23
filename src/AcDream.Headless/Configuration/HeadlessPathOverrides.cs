namespace AcDream.Headless.Configuration;

/// <summary>
/// Folders a bot configuration or the command line names. A root names the
/// whole install; the three members still override their own folder.
/// </summary>
internal sealed record HeadlessPathOverrides(
    string? ConfigDirectory = null,
    string? DataDirectory = null,
    string? CacheDirectory = null,
    string? RootDirectory = null)
{
    /// <summary>
    /// The command line wins. A root named there replaces the whole
    /// configured set, members included, since it names a different install.
    /// </summary>
    internal HeadlessPathOverrides Merge(HeadlessPathOverrides commandLine) =>
        commandLine.RootDirectory is not null
            ? commandLine
            : new(
            commandLine.ConfigDirectory ?? ConfigDirectory,
            commandLine.DataDirectory ?? DataDirectory,
            commandLine.CacheDirectory ?? CacheDirectory,
            commandLine.RootDirectory ?? RootDirectory);
}
