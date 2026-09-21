namespace AcDream.Plugin.Abstractions;

/// <summary>Normalized allegiance state known by the current session.</summary>
public readonly record struct PluginAllegianceSnapshot(
    long Revision,
    bool IsKnown,
    string Name,
    uint Rank,
    uint MemberCount,
    uint VassalCount,
    uint MonarchObjectId)
{
    /// <summary>True when a monarch identity is available.</summary>
    public bool HasMonarch => MonarchObjectId != 0u;
}

/// <summary>Reads authoritative allegiance state.</summary>
public interface IAllegianceAutomation
{
    /// <summary>True when the host is tracking allegiance state.</summary>
    bool IsAvailable => false;

    /// <summary>The latest normalized allegiance snapshot.</summary>
    PluginAllegianceSnapshot Snapshot => default;
}
