using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Physics;

/// <summary>
/// One body's motion simulation state: the sequencer that plays its cycles,
/// the uniform scale its travel is measured in, and the scratch frames a
/// single advance writes into.
/// </summary>
/// <remarks>
/// This is everything a body needs to carry itself forward between the
/// server's updates, so it is held here rather than beside the parts that draw
/// it: a body moves by the same amount whether or not anything is drawing it,
/// and there is one owner that can say so.
///
/// The two scratch frames belong to the body and are reused on every advance.
/// An advance writes this step's travel and turn into them and the physics
/// step reads it straight back, so a crowd of bodies costs no allocation per
/// object per step. They are per body and never shared, because two bodies
/// advancing in the same frame would otherwise overwrite each other's travel.
/// </remarks>
internal sealed class RuntimeRemoteAnimationState
{
    /// <summary>
    /// The sequencer that plays this body's cycles, or null when the body has
    /// no cycles to play — either because nothing authored any for it, or
    /// because this host has no animation content at all.
    /// </summary>
    public AnimationSequencer? Sequencer { get; set; }

    /// <summary>
    /// The uniform scale this body wears. Authored travel is measured on the
    /// unscaled body, so every step of it is grown by this before it moves
    /// anything.
    /// </summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Where one advance writes the root's travel and turn.</summary>
    public Frame RootMotionScratch { get; } = new();

    /// <summary>The same travel in the form the physics step consumes.</summary>
    public MotionDeltaFrame RootMotionDeltaScratch { get; } = new();
}
