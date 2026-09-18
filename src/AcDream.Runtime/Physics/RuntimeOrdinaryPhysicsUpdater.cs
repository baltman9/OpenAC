using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Physics;

internal readonly record struct RuntimePhysicsFrameSnapshot(
    Vector3 Position,
    Quaternion Orientation,
    uint FullCellId);

/// <summary>Names one begun ordinary-physics step. The step's commit is
/// reused from one step to the next, so the ticket, not the object, says
/// which step a caller is holding.</summary>
internal readonly record struct RuntimeOrdinaryPhysicsTicket(ulong Value);

/// <summary>
/// The ordinary-physics step an updater has begun and not yet completed.
///
/// One of these belongs to each updater and describes one step at a time:
/// an entity's step is begun and completed inside a single call, and a
/// commit object per entity per frame was the largest per-frame allocation
/// left at the two towns. Reuse on its own would weaken the guard that a
/// commit completes once, because a caller holding a commit from an earlier
/// step would find an object describing the current one, so each step is
/// stamped with a ticket and completing takes the ticket back.
/// </summary>
internal sealed class RuntimeOrdinaryPhysicsCommit(
    RuntimeOrdinaryPhysicsUpdater owner)
{
    private ulong _ticket;

    internal RuntimeOrdinaryPhysicsUpdater Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));

    internal RuntimeEntityRecord Record { get; private set; } = null!;
    internal PhysicsBody Body { get; private set; } = null!;
    internal ulong ObjectClockEpoch { get; private set; }
    internal bool FrameChanged { get; private set; }
    internal Func<bool>? ExternalOwnerValid { get; private set; }
    internal bool Completed { get; set; }
    internal RuntimePhysicsFrameSnapshot Snapshot { get; private set; }

    /// <summary>Describes a newly begun step and returns its ticket. The
    /// step this object described before is over: its ticket no longer
    /// matches, so nothing still holding it can complete it.</summary>
    internal RuntimeOrdinaryPhysicsTicket Begin(
        RuntimeEntityRecord record,
        PhysicsBody body,
        ulong objectClockEpoch,
        bool frameChanged,
        Func<bool>? externalOwnerValid,
        in RuntimePhysicsFrameSnapshot snapshot)
    {
        Record = record;
        Body = body;
        ObjectClockEpoch = objectClockEpoch;
        FrameChanged = frameChanged;
        ExternalOwnerValid = externalOwnerValid;
        Snapshot = snapshot;
        Completed = false;
        _ticket++;
        return new RuntimeOrdinaryPhysicsTicket(_ticket);
    }

    /// <summary>Whether <paramref name="ticket"/> is the ticket of the step
    /// described now. A ticket of an earlier step, or of no step at all,
    /// is not.</summary>
    internal bool Holds(RuntimeOrdinaryPhysicsTicket ticket) =>
        _ticket != 0ul && ticket.Value == _ticket;
}

internal sealed class RuntimeOrdinaryPhysicsUpdater
{
    private readonly RuntimePhysicsState _physics;

    // One step is begun and completed at a time, so one commit describes
    // them all in turn; see the ticket on RuntimeOrdinaryPhysicsCommit.
    private readonly RuntimeOrdinaryPhysicsCommit _commit;

    internal RuntimeOrdinaryPhysicsUpdater(RuntimePhysicsState physics)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _commit = new RuntimeOrdinaryPhysicsCommit(this);
    }

    internal bool TryBegin(
        RuntimeEntityRecord record,
        Frame rootFrame,
        float objectScale,
        float quantum,
        float radius,
        float height,
        ulong objectClockEpoch,
        AnimationSequencer? sequencer,
        Action<uint, AnimationSequencer> captureAnimationHooks,
        Func<bool>? externalOwnerValid,
        out RuntimeOrdinaryPhysicsCommit commit,
        out RuntimeOrdinaryPhysicsTicket ticket,
        System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>
            sphereList = default,
        float sphereScale = 1f,
        float stepUpHeight = 0.4f,
        float stepDownHeight = 0.4f,
        ObjectInfoState moverPvpState = ObjectInfoState.None)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rootFrame);
        ArgumentNullException.ThrowIfNull(captureAnimationHooks);
        if (record.PhysicsBody is not { } body
            || !IsCurrent(
                record,
                body,
                objectClockEpoch,
                externalOwnerValid))
        {
            commit = null!;
            ticket = default;
            return false;
        }

        body.State = record.FinalPhysicsState;
        Vector3 priorPosition = body.Position;
        Quaternion priorOrientation = body.Orientation;
        bool previousContact = body.InContact;
        bool previousOnWalkable = body.OnWalkable;

        Vector3 candidatePosition = priorPosition;
        if (body.OnWalkable && rootFrame.Origin != Vector3.Zero)
        {
            candidatePosition += Vector3.Transform(
                rootFrame.Origin * objectScale,
                priorOrientation);
        }

        Quaternion candidateOrientation = priorOrientation;
        if (!rootFrame.Orientation.IsIdentity)
        {
            candidateOrientation = FrameOps.SetRotate(
                candidatePosition,
                priorOrientation,
                priorOrientation * rootFrame.Orientation);
        }

        body.SetFrameInCurrentCell(candidatePosition, candidateOrientation);
        body.calc_acceleration();
        body.UpdatePhysicsInternal(quantum);
        body.SetFrameInCurrentCell(body.Position, body.Orientation);

        if (sequencer is not null)
        {
            uint localId = record.LocalEntityId
                ?? throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} has no local identity.");
            captureAnimationHooks(localId, sequencer);
        }
        if (!IsCurrent(
                record,
                body,
                objectClockEpoch,
                externalOwnerValid))
        {
            commit = null!;
            ticket = default;
            return false;
        }

        Vector3 integratedPosition = body.Position;
        uint sourceCellId = record.FullCellId;
        uint resolvedCellId = sourceCellId;
        uint movingEntityId = record.LocalEntityId ?? 0u;
        bool hasSweepShape = !sphereList.IsDefaultOrEmpty || radius >= 0.05f;

        // An object only moves through a sweep of its collision shape. With
        // nothing to sweep, or when the sweep is refused, it keeps its
        // origin and only its orientation advances; its velocity keeps
        // integrating, so it stays put rather than sinking through the
        // world (the sign hung on a wall carries no collision spheres).
        void HoldOrigin(bool deactivateOnWalkable)
        {
            body.SetFrameInCurrentCell(priorPosition, body.Orientation);
            body.CachedVelocity = Vector3.Zero;
            if (deactivateOnWalkable && body.OnWalkable)
                body.TransientState &= ~TransientStateFlags.Active;
        }

        if (integratedPosition != priorPosition
            && sourceCellId != 0
            && hasSweepShape
            && _physics.Engine.LandblockCount > 0)
        {
            ResolveResult resolved = _physics.Engine.ResolveWithTransition(
                priorPosition,
                integratedPosition,
                sourceCellId,
                radius,
                height,
                stepUpHeight: stepUpHeight,
                stepDownHeight: stepDownHeight,
                isOnGround: previousOnWalkable,
                body: body,
                moverFlags: (IsPlayerGuid(record.ServerGuid)
                    ? ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide
                    : ObjectInfoState.EdgeSlide)
                    | moverPvpState,
                movingEntityId: movingEntityId,
                sphereList: sphereList,
                sphereScale: sphereScale);

            if (resolved.Ok)
            {
                resolvedCellId = resolved.CellId != 0
                    ? resolved.CellId
                    : sourceCellId;
                body.CommitTransitionPosition(
                    resolvedCellId,
                    resolved.Position);
                PhysicsObjUpdate.CommitSetPositionTransition(
                    body,
                    resolved.InContact,
                    resolved.OnWalkable,
                    resolved.CollisionNormalValid,
                    resolved.CollisionNormal,
                    previousContact,
                    previousOnWalkable);
                body.CachedVelocity = quantum > 0f
                    ? (body.Position - priorPosition) / quantum
                    : Vector3.Zero;
            }
            else
            {
                HoldOrigin(deactivateOnWalkable: false);
            }
        }
        else if (integratedPosition != priorPosition && !hasSweepShape)
        {
            HoldOrigin(deactivateOnWalkable: true);
        }
        else
        {
            body.CachedVelocity = Vector3.Zero;
        }
        bool frameChanged = body.Position != priorPosition
            || body.Orientation != priorOrientation;

        if (!IsCurrent(
                record,
                body,
                objectClockEpoch,
                externalOwnerValid))
        {
            commit = null!;
            ticket = default;
            return false;
        }

        commit = _commit;
        ticket = _commit.Begin(
            record,
            body,
            objectClockEpoch,
            frameChanged,
            externalOwnerValid,
            new RuntimePhysicsFrameSnapshot(
                body.Position,
                body.Orientation,
                resolvedCellId));
        return true;
    }

    internal bool Complete(
        RuntimeOrdinaryPhysicsCommit commit,
        RuntimeOrdinaryPhysicsTicket ticket,
        int liveCenterX,
        int liveCenterY,
        Func<RuntimePhysicsFrameSnapshot, bool> acknowledgeProjection)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(acknowledgeProjection);
        if (!ReferenceEquals(commit.Owner, this))
        {
            throw new InvalidOperationException(
                "An ordinary-physics commit belongs to another Runtime owner.");
        }
        if (!commit.Holds(ticket))
        {
            throw new InvalidOperationException(
                "An ordinary-physics commit has begun another step since"
                + " this ticket was issued.");
        }
        if (commit.Completed)
        {
            throw new InvalidOperationException(
                "An ordinary-physics commit has already completed.");
        }
        commit.Completed = true;

        if (!IsCurrent(
                commit.Record,
                commit.Body,
                commit.ObjectClockEpoch,
                commit.ExternalOwnerValid)
            || !_physics.CommitOrdinaryCell(
                commit.Record,
                commit.Body,
                commit.ObjectClockEpoch,
                commit.Snapshot.FullCellId,
                commit.ExternalOwnerValid)
            || !acknowledgeProjection(commit.Snapshot)
            || !IsCurrent(
                commit.Record,
                commit.Body,
                commit.ObjectClockEpoch,
                commit.ExternalOwnerValid))
        {
            return false;
        }

        if (commit.FrameChanged
            && commit.Record.FullCellId != 0)
        {
            ShadowPositionSynchronizer.Sync(
                _physics.Engine.ShadowObjects,
                commit.Record.LocalEntityId ?? 0u,
                commit.Body.Position,
                commit.Body.Orientation,
                commit.Record.FullCellId,
                liveCenterX,
                liveCenterY);
        }

        return IsCurrent(
            commit.Record,
            commit.Body,
            commit.ObjectClockEpoch,
            commit.ExternalOwnerValid);
    }

    private bool IsCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        ulong objectClockEpoch,
        Func<bool>? externalOwnerValid) =>
        _physics.IsSpatialRoot(record)
        && record.ObjectClockEpoch == objectClockEpoch
        && ReferenceEquals(record.PhysicsBody, body)
        && record.RemoteMotion is null
        && (externalOwnerValid?.Invoke() ?? true);

    private static bool IsPlayerGuid(uint guid) =>
        (guid & 0xFF000000u) == 0x50000000u;
}
