using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering;

internal sealed class LiveEntityAnimationState : ILiveEntityAnimationRuntime
{
    public required WorldEntity Entity;
    WorldEntity ILiveEntityAnimationRuntime.Entity => Entity;
    uint ILiveEntityAnimationRuntime.CurrentMotion => Sequencer?.CurrentMotion ?? 0u;

    public required Setup Setup;
    public required Animation Animation;
    public required int LowFrame;
    public required int HighFrame;
    public required float Framerate;
    public required IReadOnlyList<LiveAnimationPartTemplate> PartTemplate;
    public required IReadOnlyList<bool> PartAvailability;
    public float CurrFrame;

    /// <summary>
    /// How this body moves itself between the server's updates: the sequencer
    /// that plays its cycles, the scale its travel is measured in, and the
    /// scratch frames one advance writes into.
    /// </summary>
    /// <remarks>
    /// The simulation half of an animating body belongs to the shared runtime,
    /// not to the parts that draw it, so that a body advances by the same
    /// amount with or without a window. What is left here is presentation: the
    /// part layout, the frame range, the poses and the scratch lists the
    /// renderer reads.
    /// </remarks>
    public required RuntimeRemoteAnimationState Simulation;

    /// <summary>The sequencer this body plays its cycles with, if any.</summary>
    public AnimationSequencer? Sequencer
    {
        get => Simulation.Sequencer;
        set => Simulation.Sequencer = value;
    }

    /// <summary>The uniform scale this body wears.</summary>
    public float Scale
    {
        get => Simulation.Scale;
        set => Simulation.Scale = value;
    }

    /// <summary>Where one advance writes the root's travel and turn.</summary>
    public Frame RootMotionScratch => Simulation.RootMotionScratch;

    /// <summary>The same travel in the form the physics step consumes.</summary>
    public MotionDeltaFrame RootMotionDeltaScratch =>
        Simulation.RootMotionDeltaScratch;

    public IReadOnlyList<PartTransform>? PreparedSequenceFrames;
    public bool SequenceAdvancedBeforeAnimationPass;
    public readonly List<PartTransform> SequenceFramesScratch = new();
    public readonly List<PartTransform> ScheduleFramesScratch = new();

    public readonly List<MeshRef> MeshRefsScratch = new();
    public readonly List<Matrix4x4> EffectPartPosesScratch = new();
    public readonly List<Matrix4x4> VisualPartPosesScratch = new();
    public bool PresentationPosesInitialized;
    public ulong PresentationRevision { get; private set; } = 1UL;

    public double LastSequenceDiagnosticTime;
    public double LastPartDiagnosticTime;

    public void InvalidatePresentationPoses()
    {
        PresentationRevision++;
        if (PresentationRevision == 0UL)
            PresentationRevision++;
        PresentationPosesInitialized = false;
        VisualPartPosesScratch.Clear();
        EffectPartPosesScratch.Clear();
    }

    public IReadOnlyList<PartTransform> CaptureSequenceFrames(
        IReadOnlyList<PartTransform> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SequenceFramesScratch.Clear();
        for (int i = 0; i < source.Count; i++)
            SequenceFramesScratch.Add(source[i]);
        return SequenceFramesScratch;
    }

    public IReadOnlyList<PartTransform> CaptureScheduleFrames(
        IReadOnlyList<PartTransform> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ScheduleFramesScratch.Clear();
        for (int i = 0; i < source.Count; i++)
            ScheduleFramesScratch.Add(source[i]);
        return ScheduleFramesScratch;
    }
}

internal readonly record struct LiveAnimationPartTemplate(
    uint GfxObjId,
    IReadOnlyDictionary<uint, uint>? SurfaceOverrides,
    bool IsDrawable);
