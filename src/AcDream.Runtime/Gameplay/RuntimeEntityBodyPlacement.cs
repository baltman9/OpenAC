using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Where a thing's own body has it, for the surfaces that would otherwise
/// have to decide that for themselves.
/// </summary>
internal static class RuntimeEntityBodyPlacement
{
    /// <summary>
    /// Where this thing's body has it, or nothing when it has no body or has
    /// one that has never been settled anywhere.
    /// </summary>
    /// <remarks>
    /// A body is made before it is settled, and an unsettled one answers with
    /// the origin of the world rather than with where the thing is. Saying
    /// nothing for those is what lets a caller fall back to the last thing the
    /// server said, which is the only word about that thing there is until its
    /// body is settled.
    /// </remarks>
    internal static Position? SettledPosition(RuntimeEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.PhysicsBody?.CellPosition is { } placed
            && placed.ObjCellId != 0u
                ? placed
                : null;
    }
}
