using AcDream.Core.Physics;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Physics;

/// <summary>
/// Closes a body's own cycle loop on a host that presents nothing.
/// </summary>
/// <remarks>
/// <para>
/// A body plays its cycles whether or not anything draws them, and a cycle a
/// body has been told to play stays outstanding until something reports that
/// it finished. A host that draws the body takes the points one step of a
/// cycle reached out of it as part of drawing that step, and reports the
/// cycles that ended among them. A host that draws nothing has to take them
/// all the same, at the same point in the step, for two reasons: the body's
/// next movement is refused for as long as a cycle is outstanding, so a walk
/// that is never told its turn finished never takes a step; and the points
/// nobody takes are held for as long as the session runs.
/// </para>
/// <para>
/// The order matters and is the drawing host's: the points are taken after
/// the body has been moved by the step, and the cycles that ended among them
/// are reported in the order they were reached.
/// </para>
/// </remarks>
internal static class RuntimeReachedCycleCompletion
{
    /// <summary>
    /// Takes the points the step just taken reached and reports the cycles
    /// among them that finished.
    /// </summary>
    internal static void TakeReachedPoints(AnimationSequencer sequencer)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        IReadOnlyList<AnimationHook> reached = sequencer.ConsumePendingHooks();
        for (int i = 0; i < reached.Count; i++)
        {
            if (reached[i] is AnimationDoneHook)
                sequencer.Manager.AnimationDone(success: true);
        }
    }
}
