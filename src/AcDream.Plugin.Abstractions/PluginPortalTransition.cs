namespace AcDream.Plugin.Abstractions;

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
    /// <summary>True while this generation has not completed or cancelled.</summary>
    public bool IsActive => Generation != 0 && !IsCompleted && !IsCancelled;
}
