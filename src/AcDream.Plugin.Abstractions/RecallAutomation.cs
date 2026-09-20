namespace AcDream.Plugin.Abstractions;

/// <summary>Recall destination that can be requested from the host.</summary>
public enum PluginRecallKind
{
    /// <summary>Recall to the bound lifestone.</summary>
    Lifestone,
    /// <summary>Recall to the marketplace.</summary>
    Marketplace,
    /// <summary>Recall to the house.</summary>
    House,
    /// <summary>Recall to the mansion.</summary>
    Mansion,
    /// <summary>Recall to the allegiance hometown.</summary>
    Allegiance,
}

/// <summary>Why a recall request was accepted or refused.</summary>
public enum PluginRecallStatus
{
    /// <summary>The session is unavailable.</summary>
    Unavailable,
    /// <summary>The host does not support this recall.</summary>
    Unsupported,
    /// <summary>The recall request was sent.</summary>
    Started,
    /// <summary>The host refused the request.</summary>
    Refused,
}

/// <summary>A recall destination known to the current session.</summary>
public readonly record struct PluginRecallLocation(
    PluginRecallKind Kind,
    PluginNavigationPosition Position,
    string Name,
    long Revision,
    bool IsKnown);

/// <summary>The most recent recall request issued by this surface.</summary>
public readonly record struct PluginRecallRequest(
    long Revision,
    PluginRecallKind Kind,
    PluginRecallStatus Status);

/// <summary>The local result of issuing a recall command.</summary>
public readonly record struct PluginRecallResult(
    PluginRecallStatus Status,
    string? Notice = null)
{
    /// <summary>True when the request was sent to the host.</summary>
    public bool Accepted => Status == PluginRecallStatus.Started;
}

/// <summary>
/// Issues generation-safe recall requests and exposes destinations learned
/// authoritatively by the host.
/// </summary>
public interface IRecallAutomation
{
    /// <summary>True when a live session can issue recall commands.</summary>
    bool IsAvailable => false;

    /// <summary>The most recent request, or a zero-revision default.</summary>
    PluginRecallRequest LastRequest => default;

    /// <summary>Returns authoritative destinations learned by this session.</summary>
    IReadOnlyList<PluginRecallLocation> CaptureLocations() =>
        Array.Empty<PluginRecallLocation>();

    /// <summary>Requests one of the host's supported recall destinations.</summary>
    PluginRecallResult Recall(PluginRecallKind kind) =>
        new(PluginRecallStatus.Unavailable);
}
