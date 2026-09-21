using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

/// <summary>
/// Whether another creature's body is going to be carried forward at all.
/// </summary>
/// <remarks>
/// Two callers need this answer and they must agree, or they disagree about
/// the same body: the inbound sink asks it to decide whether an accepted
/// position is queued for the body to catch up to or written straight onto
/// the body, and the drive asks it to decide whether to spend any time on the
/// body at all. So the test is stated once, here.
///
/// The one part a client answers for itself is whether the body's clock is
/// running, because on a client that presents the body that follows from what
/// it is presenting. Everything else — is the thing a fixture, is this still
/// the body the record carries, is the record still in the physics workset —
/// comes off the one record, so two clients cannot disagree about it.
/// </remarks>
internal static class RuntimeRemoteBodyDisposition
{
    /// <summary>
    /// Whether a body's own clock is running, answered from the record alone:
    /// it is in the physics workset under a cell, and nothing has frozen it.
    /// A client that presents nothing has no richer answer than this.
    /// </summary>
    internal static RetailObjectClockDisposition RootClock(
        RuntimePhysicsState physics,
        RuntimeEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(record);
        return physics.IsSpatialRoot(record)
            && (record.FinalPhysicsState & PhysicsStateFlags.Frozen) == 0
                ? RetailObjectClockDisposition.Advance
                : RetailObjectClockDisposition.Suspend;
    }

    /// <summary>
    /// Whether this body will be carried forward: it is not a fixture, its
    /// clock is running, and it is still the body this record carries.
    /// </summary>
    internal static bool WillAdvance(
        RuntimePhysicsState physics,
        RuntimeEntityRecord record,
        IRuntimeRemoteMotion remote,
        bool rootClockAdvances)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);
        return (record.FinalPhysicsState & PhysicsStateFlags.Static) == 0
            && rootClockAdvances
            && physics.IsSpatialRemote(record, remote);
    }

    /// <summary>
    /// The same question for a client that answers the clock off the record,
    /// which is every client with nothing to present.
    /// </summary>
    internal static bool WillAdvance(
        RuntimePhysicsState physics,
        RuntimeEntityRecord record,
        IRuntimeRemoteMotion remote) =>
        WillAdvance(
            physics,
            record,
            remote,
            RootClock(physics, record) is RetailObjectClockDisposition.Advance);
}

/// <summary>
/// Carries every other creature's body forward once per frame, for a client
/// that presents none of them.
/// </summary>
/// <remarks>
/// This is the windowless half of one owner, not a second owner: how far a
/// body moves is <see cref="RuntimeRemoteBodyOwner"/>'s decision, and all
/// this does is say WHEN, name the bodies, and hand over the few facts a
/// client holds. A client that presents its bodies hangs pose work off the
/// same owner from its own frame; the travel, the sweep and the managers are
/// identical either way.
///
/// The loop is deliberately serial. Every body shares one owner, and that
/// owner keeps one set of scratch frames for a body with no cycles of its
/// own, so two bodies advancing at once would overwrite each other's travel.
/// It also allocates nothing once it is warm: the record list is reused, the
/// facts are a value, and the one thing a client with nothing to present
/// hangs off a step is shared by every body and built once.
/// </remarks>
internal sealed class RuntimeRemoteBodyDrive
{
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly RuntimeRemoteBodyOwner _owner;
    private readonly Func<uint> _localPlayerGuid;
    private readonly Func<Vector3?> _playerPosition;
    private readonly List<RuntimeEntityRecord> _bodies = [];

    /// <summary>
    /// The one thing a client with nothing to present still hangs off a step:
    /// taking the points the step's cycle reached. Nobody else takes them
    /// here, and a cycle whose end is never reported leaves the body's next
    /// movement refused forever and the points piling up.
    /// </summary>
    private static readonly RuntimeRemoteBodyPresentation TakeReachedPoints =
        new(CaptureAnimationHooks: static (_, sequencer) =>
            RuntimeReachedCycleCompletion.TakeReachedPoints(sequencer));

    internal RuntimeRemoteBodyDrive(
        RuntimeEntityObjectLifetime entityObjects,
        Func<uint> localPlayerGuid,
        Func<Vector3?> playerPosition)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _localPlayerGuid = localPlayerGuid
            ?? throw new ArgumentNullException(nameof(localPlayerGuid));
        _playerPosition = playerPosition
            ?? throw new ArgumentNullException(nameof(playerPosition));
        _owner = new RuntimeRemoteBodyOwner(entityObjects.Physics);
    }

    /// <summary>How many bodies the last frame carried forward.</summary>
    internal int LastAdvancedCount { get; private set; }

    /// <summary>
    /// Carries every other creature's body forward by however much time has
    /// passed on this client's frame.
    /// </summary>
    internal void Tick(float elapsedSeconds)
    {
        LastAdvancedCount = 0;
        if (elapsedSeconds <= 0f)
            return;

        RuntimePhysicsState physics = _entityObjects.Physics;
        physics.CopySpatialRemotesTo(_bodies);
        if (_bodies.Count == 0)
            return;

        uint player = _localPlayerGuid();
        Vector3? playerPosition = _playerPosition();
        (int centerX, int centerY) = physics.WorldFrameCenter;
        int advanced = 0;

        for (int index = 0; index < _bodies.Count; index++)
        {
            RuntimeEntityRecord record = _bodies[index];
            if (record.ServerGuid == player
                || record.RemoteMotion is not RemoteMotion remote)
            {
                continue;
            }
            // Something in flight is carried by whatever is flying it, not by
            // this; a client that flies none leaves it where the server put
            // it, which is what it has always done.
            if (record.Projectile is not null)
                continue;

            // A body needs its motion state even when it has no cycles to
            // play, because that state is where the scale its travel is
            // measured in and its scratch frames live. A client with no
            // animation content builds none, and its bodies then stand where
            // the server put them.
            if (EnsureAnimation(physics, record) is not { } animation)
                continue;

            // A finished cycle has to arrive back at this body's own motion
            // state, or every movement it is given afterwards is refused. On
            // a client that presents the body, whatever presents it points
            // that report where it belongs; here nothing does, so the body
            // reports to itself.
            if (animation.Sequencer is { MotionDoneTarget: null } sequencer)
                sequencer.MotionDoneTarget = remote.Motion.MotionDone;

            var facts = new RuntimeRemoteBodyFacts(
                elapsedSeconds,
                remote.Body.Position,
                playerPosition,
                RuntimeRemoteBodyDisposition.RootClock(physics, record)
                    is RetailObjectClockDisposition.Advance,
                animation.Scale,
                record.ObjectClockEpoch,
                centerX,
                centerY);
            if (_owner.Advance(
                    record, remote, animation, facts, TakeReachedPoints)
                .Advanced)
            {
                advanced++;
            }
        }

        LastAdvancedCount = advanced;
    }

    /// <summary>
    /// The body's motion state, built once from the creation description the
    /// server sent. Null when this client has no animation content bound, in
    /// which case it has no bodies to carry and never has had.
    /// </summary>
    private static RuntimeRemoteAnimationState? EnsureAnimation(
        RuntimePhysicsState physics,
        RuntimeEntityRecord record)
    {
        if (record.RemoteAnimation is { } existing)
            return existing;
        if (physics.MotionStates is not { } builder)
            return null;

        RuntimeRemoteAnimationState built = builder.CreateFromSpawn(
            record.Snapshot,
            physics.EntityObjectScale(record));
        physics.SetRemoteAnimation(record, built);
        return record.RemoteAnimation;
    }
}
