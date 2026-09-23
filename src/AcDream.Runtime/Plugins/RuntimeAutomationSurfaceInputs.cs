using System.Reflection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Plugins;

/// <summary>
/// What a host hands the plugin surface when the surface is BUILT, as
/// opposed to what it binds into it afterwards.
///
/// These inputs cannot be supplied late. The surface follows the host's tick
/// to publish where this client is and what it answers to, so a host that
/// builds the surface without a tick leaves that announcement dead for the
/// whole run and nothing bound later can revive it. Collecting them in one
/// record lets the host-parity census compare what each host really passes
/// instead of trusting two separate construction sites.
/// </summary>
internal sealed record RuntimeAutomationSurfaceInputs
{
    /// <summary>The host this record was built by, for failure messages.</summary>
    public required string HostName { get; init; }

    /// <summary>
    /// The one object this host hands the surface for world events: the tick
    /// the surface follows, and where it raises what happened for plugins to
    /// hear. Every host has one; which object it is is the host's own
    /// business. Without it the surface never announces this client to the
    /// other clients on this machine, never polls the owners that need a
    /// heartbeat, and no world event ever reaches a plugin.
    /// </summary>
    public required AcDream.Core.Plugins.IPluginEventSink PluginEvents { get; init; }

    /// <summary>
    /// The folder the clients on this machine leave small notes in to find
    /// one another, so two clients pointed at the same folder see each other
    /// and two pointed at different ones do not.
    /// </summary>
    public required string PeerDirectory { get; init; }

    /// <summary>
    /// The words this client wants to be found by, from the player's own
    /// configuration. Empty is a legitimate answer -- it means the player
    /// named none -- but a host that cannot carry them at all is a host where
    /// a plugin's peer filtering silently matches nothing.
    /// </summary>
    public required IReadOnlyList<string> PeerTags { get; init; }

    /// <summary>
    /// The inputs that are not the record's own bookkeeping, in the order the
    /// census reports them.
    /// </summary>
    internal static IReadOnlyList<PropertyInfo> InputProperties { get; } =
        typeof(RuntimeAutomationSurfaceInputs)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.Name != nameof(HostName))
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every input name a host can be asked for.</summary>
    internal static IReadOnlySet<string> AllInputNames { get; } =
        InputProperties
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// What this particular record actually carries. A blank directory counts
    /// as absent: a host that passes an empty path has not named one.
    /// </summary>
    internal IReadOnlySet<string> Supplied()
    {
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in InputProperties)
        {
            object? value = property.GetValue(this);
            if (value is null)
                continue;
            if (value is string text && string.IsNullOrWhiteSpace(text))
                continue;
            supplied.Add(property.Name);
        }
        return supplied;
    }
}
