namespace AcDream.Platform;

/// <summary>
/// Files a client build ships so a launcher can tell what it understands
/// before starting it.
/// </summary>
public static class ClientCapabilities
{
    /// <summary>
    /// Present beside the client executables from the first build that keeps
    /// everything in one install folder. An older client started against the
    /// new folder would read and write its settings and plugins in the old
    /// per-user folders instead.
    /// </summary>
    public const string SingleInstallRootMarkerFileName = "single-install-root.capability";
}
