using AcDream.Runtime.Plugins;

namespace AcDream.App.Plugins;

/// <summary>
/// The parts this host builds the plugin surface itself from. Unlike the
/// capability record, these cannot be handed over later: the surface needs
/// them before a plugin can reach it at all.
/// </summary>
internal sealed record GraphicalSurfaceInputParts
{
    /// <summary>
    /// The one object this client hands the surface for world events: the
    /// tick it announces this client on, and where it raises what happened
    /// for plugins to hear.
    /// </summary>
    public required AcDream.Core.Plugins.IPluginEventSink Events { get; init; }

    /// <summary>The folder the clients on this machine find one another in.</summary>
    public required string PeerDirectory { get; init; }

    /// <summary>
    /// The words this client wants to be found by, from the player's
    /// configuration. Null and empty mean the same thing: the player named
    /// none.
    /// </summary>
    public IReadOnlyList<string>? PluginTags { get; init; }
}

internal static partial class GraphicalAutomationCapabilities
{
    /// <summary>
    /// Which construction-time inputs this host hands the plugin surface. The
    /// host-parity census reads the claim and compares it with the record
    /// <see cref="BuildSurfaceInputs"/> really makes.
    /// </summary>
    internal static IReadOnlySet<string> DeclaredSurfaceInputs { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(RuntimeAutomationSurfaceInputs.PluginEvents),
            nameof(RuntimeAutomationSurfaceInputs.PeerDirectory),
            nameof(RuntimeAutomationSurfaceInputs.PeerTags),
        };

    /// <summary>Builds the record this host really builds its surface from.</summary>
    internal static RuntimeAutomationSurfaceInputs BuildSurfaceInputs(
        GraphicalSurfaceInputParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return new RuntimeAutomationSurfaceInputs
        {
            HostName = "windowed",
            PluginEvents = parts.Events,
            PeerDirectory = parts.PeerDirectory,
            PeerTags = parts.PluginTags ?? [],
        };
    }
}
