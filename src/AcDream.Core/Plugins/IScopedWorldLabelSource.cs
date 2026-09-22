using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>
/// A world-label surface that can tell plugins apart. The scoped plugin host
/// asks it for a view of its own for each plugin, so one plugin replacing
/// its label set never touches another's, the per-plugin cap is really per
/// plugin, and a plugin's labels go with it when it is unloaded.
/// </summary>
public interface IScopedWorldLabelSource
{
    /// <summary>The label surface as one plugin sees it; every set pushed through it is that plugin's.</summary>
    IWorldLabelAutomation ScopeTo(string ownerId);

    /// <summary>Drops every label the owner had showing.</summary>
    void Release(string ownerId);
}
