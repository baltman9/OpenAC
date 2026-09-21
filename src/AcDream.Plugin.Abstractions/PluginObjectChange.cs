namespace AcDream.Plugin.Abstractions;

/// <summary>What changed about a world object reported by <see cref="IEvents.ObjectChanged"/>.</summary>
public enum PluginObjectChangeKind
{
    /// <summary>The object entered the client's object table for the first time.</summary>
    Created,

    /// <summary>A property or other non-positional field on the object changed.</summary>
    Updated,

    /// <summary>The client took delivery of appraisal data for the object.</summary>
    IdentReceived,

    /// <summary>The object's position changed enough to move it into a different cell.</summary>
    Moved,

    /// <summary>The object left the client's object table.</summary>
    Released,
}

/// <summary>Fields affected by a world-object change.</summary>
[Flags]
public enum PluginObjectChangeFields
{
    /// <summary>No field detail was supplied.</summary>
    None = 0,
    /// <summary>The object entered or left the tracked object set.</summary>
    Lifecycle = 1 << 0,
    /// <summary>Identity or normalized metadata changed.</summary>
    Identity = 1 << 1,
    /// <summary>The object's position or cell changed.</summary>
    Position = 1 << 2,
    /// <summary>Appraisal/property data changed.</summary>
    Appraisal = 1 << 3,
}

/// <summary>One change to a world object, as reported by <see cref="IEvents.ObjectChanged"/>.</summary>
public readonly record struct PluginObjectChange(
    uint ObjectId,
    PluginObjectChangeKind Kind)
{
    /// <summary>
    /// Monotonically increasing host revision for this session's object
    /// changes. Plugins can retain the greatest revision they have handled
    /// and ignore stale queued notifications. Zero is reserved for an
    /// unsequenced value supplied by a fixture or inert host.
    /// </summary>
    public long Revision { get; init; }

    /// <summary>The normalized fields affected by this notification.</summary>
    public PluginObjectChangeFields ChangedFields { get; init; }

    /// <summary>Maps a coarse change kind to its affected fields.</summary>
    public static PluginObjectChangeFields FieldsFor(PluginObjectChangeKind kind) =>
        kind switch
        {
            PluginObjectChangeKind.Created => PluginObjectChangeFields.Lifecycle
                | PluginObjectChangeFields.Identity
                | PluginObjectChangeFields.Position,
            PluginObjectChangeKind.Updated => PluginObjectChangeFields.Identity,
            PluginObjectChangeKind.IdentReceived => PluginObjectChangeFields.Appraisal,
            PluginObjectChangeKind.Moved => PluginObjectChangeFields.Position,
            PluginObjectChangeKind.Released => PluginObjectChangeFields.Lifecycle,
            _ => PluginObjectChangeFields.None,
        };

    /// <summary>
    /// Returns true when this notification has a non-zero revision that is
    /// older than a revision the plugin has already handled.
    /// </summary>
    public bool IsStaleComparedTo(long handledRevision) =>
        Revision != 0 && handledRevision > 0 && Revision <= handledRevision;

    /// <summary>
    /// The normalized object snapshot at the time of the change, when the
    /// object is still known. This is absent for a release after the host has
    /// discarded the object.
    /// </summary>
    public PluginWorldObject? Current { get; init; }
}
