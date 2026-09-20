using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// What a host that also draws the character hangs off the character's own
/// locomotion. Both entries are optional, and a host that draws nothing
/// supplies neither: its character then travels and turns by exactly the same
/// amount, with none of the pose work.
/// </summary>
/// <param name="AdvanceRootMotion">
/// Advances the character's cycle AND builds the poses of its parts, writing
/// the travel and turn that cycle means to make into the frame it is handed.
/// </param>
/// <param name="CaptureAnimationHooks">
/// Takes the hooks the step's cycle reached, in the position in the step where
/// the character reaches them.
/// </param>
internal readonly record struct RuntimeLocalPlayerMotionPresentation(
    Action<float, MotionDeltaFrame>? AdvanceRootMotion = null,
    Action? CaptureAnimationHooks = null);

/// <summary>
/// Gives the character its own locomotion: the cycle its body travels and
/// turns by, the speed that cycle is carrying, the scale that travel is
/// measured in, and somewhere for the motions it is told to play to go.
/// </summary>
/// <remarks>
/// <para>
/// How far the character gets in one step is the character's own business and
/// not the window's, so it is decided here, once, for every host. What a host
/// adds is presentation: a host that draws the character hands over a cycle
/// advance that also builds the poses of its parts, and a host that draws
/// nothing advances the same cycle by the same amount and stops there. That is
/// the same split the other creatures' bodies are carried by.
/// </para>
/// <para>
/// Binding is retried until it takes, because the character's cycles are built
/// from content that may not be to hand the moment the body is. A host that
/// has no animation content bound has no cycles to play and never gets a body
/// that moves itself, which is the honest answer and the one such a host has
/// always given.
/// </para>
/// </remarks>
internal sealed class RuntimeLocalPlayerMotionArming
{
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly Func<uint> _localPlayerGuid;
    private RuntimeLocalPlayerMotionPresentation _presentation;
    private PlayerMovementController? _armedController;
    private AnimationSequencer? _armedSequencer;

    internal RuntimeLocalPlayerMotionArming(
        RuntimeEntityObjectLifetime entityObjects,
        Func<uint> localPlayerGuid)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _localPlayerGuid = localPlayerGuid
            ?? throw new ArgumentNullException(nameof(localPlayerGuid));
    }

    /// <summary>
    /// What this host hangs off the character's locomotion. Bound once per
    /// host, before the character has a body.
    /// </summary>
    internal void BindPresentation(
        RuntimeLocalPlayerMotionPresentation presentation) =>
        _presentation = presentation;

    /// <summary>
    /// Forgets what is bound, so that the next step binds again from the
    /// beginning. A host says this when the character has just taken hold of
    /// its own body, which is the moment its outstanding motions are cleared.
    /// </summary>
    internal void Rearm()
    {
        _armedController = null;
        _armedSequencer = null;
    }

    /// <summary>
    /// Makes sure this controller is driving the character's own cycle.
    /// False when the character has no cycles to play yet, in which case it
    /// has nothing to move itself by and stands where the server put it.
    /// </summary>
    internal bool EnsureArmed(PlayerMovementController? controller)
    {
        if (controller is null)
            return false;
        if (ResolveAnimation() is not { Sequencer: { } sequencer } animation)
            return false;
        if (ReferenceEquals(_armedController, controller)
            && ReferenceEquals(_armedSequencer, sequencer))
        {
            return true;
        }

        Arm(controller, animation, sequencer);
        _armedController = controller;
        _armedSequencer = sequencer;
        return true;
    }

    /// <summary>Whether the character is driving a cycle of its own.</summary>
    internal bool IsArmed => _armedSequencer is not null;

    private void Arm(
        PlayerMovementController controller,
        RuntimeRemoteAnimationState animation,
        AnimationSequencer sequencer)
    {
        controller.AttachCycleVelocityAccessor(() => sequencer.CurrentVelocity);
        controller.ObjectScale = animation.Scale;
        controller.AttachAnimationRootMotionSource(
            _presentation.AdvanceRootMotion
                ?? ((deltaSeconds, output) =>
                    AdvanceRootMotionOnly(
                        animation, sequencer, deltaSeconds, output)),
            _presentation.CaptureAnimationHooks);
        controller.Motion.RemoveLinkAnimations =
            sequencer.Manager.HandleEnterWorld;
        controller.Motion.InitializeMotionTables =
            sequencer.Manager.InitializeState;
        controller.Motion.CheckForCompletedMotions =
            sequencer.Manager.CheckForCompletedMotions;
        controller.Motion.DefaultSink = new MotionTableDispatchSink(sequencer);
        sequencer.Manager.HandleEnterWorld();
        controller.Motion.HandleExitWorld();
    }

    /// <summary>
    /// The travel and turn one step of the character's cycle means to make,
    /// for a host that is drawing none of it.
    /// </summary>
    private static void AdvanceRootMotionOnly(
        RuntimeRemoteAnimationState animation,
        AnimationSequencer sequencer,
        float deltaSeconds,
        MotionDeltaFrame output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Reset();
        DatReaderWriter.Types.Frame rootFrame = animation.RootMotionScratch;
        rootFrame.Origin = System.Numerics.Vector3.Zero;
        rootFrame.Orientation = System.Numerics.Quaternion.Identity;
        sequencer.AdvanceRootMotionOnly(deltaSeconds, rootFrame);
        output.Origin = rootFrame.Origin;
        output.Orientation = rootFrame.Orientation;
    }

    /// <summary>
    /// The character's motion state: the one a host that draws the character
    /// built for it, or one built here from the description the server sent.
    /// Null when this host has no animation content bound.
    /// </summary>
    private RuntimeRemoteAnimationState? ResolveAnimation()
    {
        uint player = _localPlayerGuid();
        if (player == 0u
            || !_entityObjects.Entities.TryGetActive(
                player,
                out RuntimeEntityRecord record))
        {
            return null;
        }
        if (record.RemoteAnimation is { } existing)
            return existing;

        RuntimePhysicsState physics = _entityObjects.Physics;
        if (physics.MotionStates is not { } builder)
            return null;

        physics.SetRemoteAnimation(
            record,
            builder.CreateFromSpawn(
                record.Snapshot,
                physics.EntityObjectScale(record)));
        return record.RemoteAnimation;
    }
}
