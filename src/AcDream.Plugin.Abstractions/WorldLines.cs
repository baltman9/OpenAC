namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginWorldLine(
    PluginNavigationPosition Start,
    PluginNavigationPosition End,
    uint ColorRgb)
{
    public bool FollowTerrain { get; init; }
    public float WidthMeters { get; init; } = 0.25f;
}

public interface IPluginWorldLineLayer : IDisposable
{
    void SetLines(IReadOnlyList<PluginWorldLine> lines);
}

public interface IPluginWorldLines
{
    IPluginWorldLineLayer? CreateLayer();
}

public sealed class NoOpPluginWorldLines : IPluginWorldLines
{
    public static NoOpPluginWorldLines Instance { get; } = new();
    public IPluginWorldLineLayer? CreateLayer() => null;
}
