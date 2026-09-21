namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One straight line a plugin draws in the world, between two positions.
/// </summary>
/// <param name="Start">Where the line starts.</param>
/// <param name="End">Where the line ends.</param>
/// <param name="ColorRgb">The line's colour as 0xRRGGBB.</param>
public readonly record struct PluginWorldLine(
    PluginNavigationPosition Start,
    PluginNavigationPosition End,
    uint ColorRgb)
{
    /// <summary>
    /// True to keep the line just above the ground along its length, so an
    /// outdoor route reads over hills; false to draw it straight.
    /// </summary>
    public bool FollowTerrain { get; init; }

    /// <summary>The line's thickness in meters.</summary>
    public float WidthMeters { get; init; } = 0.25f;
}

/// <summary>
/// A set of lines one caller owns. Setting the lines replaces the set;
/// disposing the layer removes them from the world.
/// </summary>
public interface IPluginWorldLineLayer : IDisposable
{
    /// <summary>Replaces the layer's lines with <paramref name="lines"/>.</summary>
    void SetLines(IReadOnlyList<PluginWorldLine> lines);
}

/// <summary>Lines a plugin draws in the world, in layers it owns.</summary>
public interface IPluginWorldLines
{
    /// <summary>
    /// Creates a layer to draw into, or null when this host draws nothing
    /// (a process with no window).
    /// </summary>
    IPluginWorldLineLayer? CreateLayer();
}

/// <summary>The world lines of a host that draws nothing.</summary>
public sealed class NoOpPluginWorldLines : IPluginWorldLines
{
    /// <summary>The one shared instance.</summary>
    public static NoOpPluginWorldLines Instance { get; } = new();

    /// <inheritdoc />
    public IPluginWorldLineLayer? CreateLayer() => null;
}
