namespace AcDream.PluginCheck;

/// <summary>Which shape of plugin <see cref="PluginCheckRunner.RunAsync"/> ran against.</summary>
internal enum PluginCheckMode
{
    Directory,
    Zip,
}

internal enum PluginCheckStatus
{
    Pass,
    Fail,
    Skip,
}

/// <summary>Whether the launcher would install this plugin, following the same install/refuse split
/// <see cref="AcDream.Launcher.Core.Plugins.DirectInstallCheck"/> and
/// <see cref="AcDream.Launcher.Core.Plugins.PluginInstaller"/> make.</summary>
internal enum PluginCheckVerdict
{
    WouldInstall,
    WouldBeRefused,
}

/// <summary>One line of the report. A failing <see cref="Message"/> carries both the launcher's own
/// refusal text and what to do about it, so the JSON and human renderings never need a second field.</summary>
internal sealed record PluginCheckItem(string Check, PluginCheckStatus Status, string Message);

internal sealed record PluginCheckReport(
    int SchemaVersion,
    PluginCheckMode Mode,
    string Path,
    PluginCheckVerdict Verdict,
    IReadOnlyList<PluginCheckItem> Checks);

/// <summary>A problem the tool hits before any plugin check can run — the path doesn't exist, or
/// isn't a folder or a .zip — so there is nothing to report (exit code 2).</summary>
internal sealed class PluginCheckUsageException : Exception
{
    public PluginCheckUsageException(string message) : base(message) { }
}
