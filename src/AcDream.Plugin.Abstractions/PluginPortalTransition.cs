namespace AcDream.Plugin.Abstractions;

/// <summary>Broad source of a world transition.</summary>
public enum PluginPortalTransitionKind
{
    /// <summary>The host could not identify the transition source.</summary>
    Unknown,
    /// <summary>A login or reconnect transition.</summary>
    Login,
    /// <summary>A portal or recall transition while already in-world.</summary>
    Portal,
}

/// <summary>
/// A semantic portal or recall transition observed by the host. This is a
/// state notification, not a raw protocol event; repeated notifications for
/// one generation can be compared by <see cref="Revision"/>.
/// </summary>
public readonly record struct PluginPortalTransition(
    long Revision,
    long Generation,
    uint DestinationCell,
    bool IsReady,
    bool IsMaterialized,
    bool IsCompleted,
    bool IsCancelled)
{
    /// <summary>
    /// The recall request revision that initiated this transition, or zero
    /// when the transition was not correlated to a plugin recall request.
    /// </summary>
    public long RecallRequestRevision { get; init; }

    /// <summary>The semantic source of this transition.</summary>
    public PluginPortalTransitionKind Kind { get; init; }

    /// <summary>True while this generation has not completed or cancelled.</summary>
    public bool IsActive => Generation != 0 && !IsCompleted && !IsCancelled;

    /// <summary>True when the transition reached completion or cancellation.</summary>
    public bool IsTerminal => Generation != 0 && (IsCompleted || IsCancelled);

    /// <summary>
    /// Returns true when this notification is older than a transition
    /// revision the plugin has already processed.
    /// </summary>
    public bool IsStaleComparedTo(long handledRevision) =>
        Revision != 0 && handledRevision > 0 && Revision <= handledRevision;
}
