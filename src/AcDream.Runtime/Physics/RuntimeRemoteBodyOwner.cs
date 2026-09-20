using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Physics;

/// <summary>
/// What a host knows about one body at the moment it asks for that body to be
/// carried forward: how much time has passed, where the body and the local
/// player are, whether the thing behind the body is running at all, and the
/// scale its travel is measured in.
/// </summary>
/// <remarks>
/// These are the facts a host holds that the shared owner cannot work out for
/// itself. Everything else about the body — its clock, its declared state, its
/// girth, its swept shape — the owner reads from the one record, so that two
/// hosts asking for the same body to advance ask for the same thing.
/// </remarks>
internal readonly record struct RuntimeRemoteBodyFacts(
    float ElapsedSeconds,
    Vector3 Position,
    Vector3? PlayerPosition,
    bool RootClockAdvances,
    float ObjectScale,
    ulong ObjectClockEpoch,
    int LiveCenterX,
    int LiveCenterY);

/// <summary>
/// What a host that is also presenting the body hangs off an advance. Every
/// entry is optional: a host that presents nothing supplies none of them, and
/// its bodies advance by exactly the same amount with none of the pose work.
/// </summary>
/// <remarks>
/// The two currency questions are deliberately separate, because they are
/// separate questions. One asks whether the whole presentation of this body is
/// still the current one — everything the host hung off it included — and stops
/// the run of steps when it is not. The other asks the narrower question the
/// physics step asks between its own stages: is this still the body this
/// record carries.
/// </remarks>
/// <param name="StillPresenting">
/// Whether the host's presentation of this body is still current. A false
/// answer abandons the rest of the run of steps.
/// </param>
/// <param name="StillOwned">
/// Whether this is still the body the record carries, asked between the
/// stages of one physics step.
/// </param>
/// <param name="AcceptPose">
/// Takes the pose one step settled on. A false answer abandons the step,
/// which is how a host says the body it was drawing has just gone away.
/// </param>
/// <param name="BuildPartPoses">
/// Advances the body's cycle AND builds the poses of its parts, for a host
/// that is drawing them. A host that supplies none gets the travel and the
/// turn alone, at no cost for poses nothing will look at.
/// </param>
/// <param name="CaptureAnimationHooks">
/// Takes the hooks the step's cycle reached, in the position in the step
/// where the body reaches them.
/// </param>
internal readonly record struct RuntimeRemoteBodyPresentation(
    Func<bool>? StillPresenting = null,
    Func<bool>? StillOwned = null,
    Func<RuntimeRemotePhysicsSnapshot, bool>? AcceptPose = null,
    Func<AnimationSequencer, float, Frame, IReadOnlyList<PartTransform>>?
        BuildPartPoses = null,
    Action<uint, AnimationSequencer>? CaptureAnimationHooks = null);

/// <summary>
/// What became of one body's advance: whether the whole run of steps was seen
/// through, how many steps it took, the poses the last step left (when a host
/// asked for poses at all), and the time that passed for a body that has an
/// animation owner but no cycle to play.
/// </summary>
internal readonly record struct RuntimeRemoteBodyAdvance(
    bool Advanced,
    int Steps,
    IReadOnlyList<PartTransform>? PartPoses,
    float UnsequencedSeconds);

/// <summary>
/// Carries the bodies of things other than the local player forward between
/// the server's updates: the one place that decides whether a body is running,
/// how many steps of what length this much elapsed time is worth, and what
/// happens in each step.
/// </summary>
/// <remarks>
/// The decision is per body and not per host, which is why it is here: a body
/// accumulates elapsed time on its own clock, spends it in steps of a bounded
/// length and carries the remainder, so the same body fed the same elapsed
/// time lands in the same place whether the host paints it or not.
///
/// One step is, in order: the body's cycle advances and reports how far the
/// cycle itself means to travel and turn; the body is moved by that travel and
/// by whatever the server has it catching up to, swept against the world and
/// committed; the hooks the cycle reached are handed out; and the managers
/// that re-aim the body at what it is chasing run last. That order is the
/// body's, not the window's, so it is stated once here.
/// </remarks>
internal sealed class RuntimeRemoteBodyOwner
{
    private readonly RuntimePhysicsState _physics;
    private readonly RuntimeRemotePhysicsUpdater _updater;
    private readonly Func<uint, ObjectInfoState> _moverPvpState;

    /// <summary>
    /// Where a body with no animation owner of its own writes the travel and
    /// turn of one step. A body that has an animation owner uses that owner's
    /// own scratch instead; this one is shared, which is safe because a body
    /// finishes all of its steps before the next body takes any.
    /// </summary>
    private readonly Frame _rootFrameScratch = new();
    private readonly MotionDeltaFrame _rootDeltaScratch = new();

    private static readonly Func<bool> AlwaysCurrent = static () => true;

    internal RuntimeRemoteBodyOwner(
        RuntimePhysicsState physics,
        Func<uint, ObjectInfoState>? moverPvpState = null)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _updater = new RuntimeRemotePhysicsUpdater(_physics);
        _moverPvpState = moverPvpState ?? (static _ => ObjectInfoState.None);
    }

    /// <summary>
    /// Carries one body forward by however much time has passed. Returns
    /// nothing done when the body is not running, when no whole step's worth
    /// of time has accumulated yet, or when the host says mid-run that the
    /// body it asked about is no longer the one it is holding.
    /// </summary>
    internal RuntimeRemoteBodyAdvance Advance(
        RuntimeEntityRecord record,
        RemoteMotion remote,
        RuntimeRemoteAnimationState? animation,
        in RuntimeRemoteBodyFacts facts,
        in RuntimeRemoteBodyPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);

        PhysicsStateFlags state = record.FinalPhysicsState;
        if (RetailObjectActivityGate.Evaluate(
                record.ObjectClock,
                remote.Body,
                facts.RootClockAdvances,
                record.HasPartArray,
                (state & PhysicsStateFlags.Static) != 0,
                facts.Position,
                facts.PlayerPosition,
                facts.ElapsedSeconds)
            is not RetailObjectActivityResult.Active)
        {
            return default;
        }

        RetailObjectQuantumBatch batch =
            record.ObjectClock.Advance(facts.ElapsedSeconds);
        if (batch.Count == 0)
            return default;

        bool hidden = (state & PhysicsStateFlags.Hidden) != 0;
        AnimationSequencer? sequencer = animation?.Sequencer;
        Func<bool> stillPresenting =
            presentation.StillPresenting ?? AlwaysCurrent;
        IReadOnlyList<PartTransform>? poses = null;
        float unsequenced = 0f;

        for (int qi = 0; qi < batch.Count; qi++)
        {
            if (!stillPresenting())
                return default;

            float step = batch.GetQuantum(qi);
            if (hidden)
            {
                if (!TickHiddenBody(
                        record,
                        remote,
                        sequencer,
                        step,
                        facts,
                        presentation))
                {
                    return default;
                }

                if (!stillPresenting())
                    return default;
                continue;
            }

            Frame rootFrame = animation?.RootMotionScratch ?? _rootFrameScratch;
            rootFrame.Origin = Vector3.Zero;
            rootFrame.Orientation = Quaternion.Identity;
            if (sequencer is not null)
            {
                if (presentation.BuildPartPoses is { } buildPoses)
                {
                    poses = buildPoses(sequencer, step, rootFrame);
                }
                else
                {
                    sequencer.AdvanceRootMotionOnly(step, rootFrame);
                    poses = null;
                }
            }
            else if (animation is not null)
            {
                unsequenced += step;
            }

            if (!stillPresenting())
                return default;

            if (animation is not null && step > 0f)
            {
                float rootMotionSpeed = rootFrame.Origin.Length()
                    * facts.ObjectScale / step;
                remote.MaxRootMotionSpeedSinceLastUP = MathF.Max(
                    remote.MaxRootMotionSpeedSinceLastUP,
                    rootMotionSpeed);
            }

            MotionDeltaFrame rootDelta =
                animation?.RootMotionDeltaScratch ?? _rootDeltaScratch;
            rootDelta.Origin = rootFrame.Origin;
            rootDelta.Orientation = rootFrame.Orientation;
            if (!TickBody(
                    record,
                    remote,
                    animation,
                    sequencer,
                    step,
                    rootDelta,
                    facts,
                    presentation))
            {
                return default;
            }

            if (!stillPresenting())
                return default;
        }

        _physics.NoteRemoteBodyCarried(record.ServerGuid);
        return new RuntimeRemoteBodyAdvance(
            true,
            batch.Count,
            poses,
            unsequenced);
    }

    /// <summary>
    /// One step of a body the world can see: moved by its own travel and by
    /// whatever it is catching up to, swept against the world, committed,
    /// hooks handed out, managers run.
    /// </summary>
    internal bool TickBody(
        RuntimeEntityRecord record,
        RemoteMotion remote,
        RuntimeRemoteAnimationState? animation,
        AnimationSequencer? sequencer,
        float step,
        MotionDeltaFrame rootMotionLocalFrame,
        in RuntimeRemoteBodyFacts facts,
        in RuntimeRemoteBodyPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);
        uint serverGuid = record.ServerGuid;
        (float Radius, float Height) cylinder = BodyCylinder(serverGuid);
        var shape = _physics.EntityMoverShape(serverGuid);
        return _updater.Tick(
            record,
            remote,
            facts.ObjectScale,
            sequencer,
            step,
            facts.ObjectClockEpoch,
            rootMotionLocalFrame,
            cylinder.Radius,
            cylinder.Height,
            facts.LiveCenterX,
            facts.LiveCenterY,
            presentation.CaptureAnimationHooks,
            animation is not null,
            presentation.AcceptPose,
            presentation.StillOwned,
            sphereList: shape.Spheres,
            sphereScale: shape.Scale,
            stepUpHeight: shape.StepUpHeight,
            stepDownHeight: shape.StepDownHeight,
            moverPvpState: _moverPvpState(serverGuid));
    }

    /// <summary>
    /// One step of a body nothing can see. It still catches up to where the
    /// server last put it and still sweeps against the world, because coming
    /// back into view in the wrong place is the same fault as being in the
    /// wrong place; it just has no travel of its own to add.
    /// </summary>
    internal bool TickHiddenBody(
        RuntimeEntityRecord record,
        RemoteMotion remote,
        AnimationSequencer? sequencer,
        float step,
        in RuntimeRemoteBodyFacts facts,
        in RuntimeRemoteBodyPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);
        uint serverGuid = record.ServerGuid;
        (float Radius, float Height) cylinder = BodyCylinder(serverGuid);
        var shape = _physics.EntityMoverShape(serverGuid);
        return _updater.TickHidden(
            record,
            remote,
            step,
            facts.ObjectClockEpoch,
            cylinder.Radius,
            cylinder.Height,
            sequencer?.Manager,
            presentation.CaptureAnimationHooks,
            sequencer,
            presentation.AcceptPose,
            presentation.StillOwned,
            sphereList: shape.Spheres,
            sphereScale: shape.Scale,
            stepUpHeight: shape.StepUpHeight,
            stepDownHeight: shape.StepDownHeight,
            moverPvpState: _moverPvpState(serverGuid));
    }

    /// <summary>
    /// Keeps the shadow the rest of the world collides against on top of the
    /// body it stands for.
    /// </summary>
    internal void SyncRemoteShadowToBody(
        uint entityId,
        IRuntimeRemotePlacement remote,
        int liveCenterX,
        int liveCenterY,
        uint? authoritativeCellId = null) =>
        _updater.SyncRemoteShadowToBody(
            entityId,
            remote,
            liveCenterX,
            liveCenterY,
            authoritativeCellId);

    /// <summary>
    /// Keeps the shadow on top of a body given straight, for a caller that
    /// already holds the body rather than the thing carrying it.
    /// </summary>
    internal void SyncRemoteShadowToBody(
        uint entityId,
        PhysicsBody body,
        int liveCenterX,
        int liveCenterY,
        uint authoritativeCellId) =>
        _updater.SyncRemoteShadowToBody(
            entityId,
            body,
            liveCenterX,
            liveCenterY,
            authoritativeCellId);

    private (float Radius, float Height) BodyCylinder(uint serverGuid) =>
        _physics.EntityBodyShape(serverGuid) ?? (0f, 0f);
}
