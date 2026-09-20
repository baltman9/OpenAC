using System.Numerics;
using AcDream.Content;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Physics;

/// <summary>
/// The few things a host knows about another creature's body that the shared
/// arming cannot work out for itself, and that change the answer.
/// </summary>
/// <remarks>
/// There are only two such things, and they are here because they are the only
/// two: what a host is DRAWING, and the centre it measures a landblock-local
/// place from. Everything else about a body — its girth, its shape, its cell,
/// its clock, the component it carries — the arming reads from the one record,
/// so that two hosts arming the same body arm it the same way.
///
/// A host that draws nothing supplies <see cref="FromRecords"/>, which answers
/// all of it from the record, and arms the same bodies with the same bindings.
/// </remarks>
/// <param name="DrawnBody">
/// The one body this host is in a position to arm under this id, or null. A
/// host with a window answers only for a body it is currently DRAWING, which
/// is a stronger test than "the record is live" and is what decides whether
/// the body gets a full physics host at all.
/// </param>
/// <param name="WireOriginToWorld">
/// Turns a place named as an offset inside a landblock into a world position,
/// using the centre THIS host measures from.
/// </param>
/// <param name="InteractionTargetPosition">
/// Where the host has a body it may be ordered to walk at or stick to, or null
/// when it has no such body. Null is also the refusal: an order naming
/// something this host cannot follow degrades to the place the order named.
/// </param>
internal readonly record struct RuntimeRemoteArmingHostFacts(
    Func<uint, RuntimeRemoteArmingDrawnBody?> DrawnBody,
    Func<uint, float, float, float, Vector3> WireOriginToWorld,
    Func<uint, Vector3?> InteractionTargetPosition)
{
    /// <summary>
    /// The answers a host with nothing drawn gives: every one of them off the
    /// one record and the shared world frame.
    /// </summary>
    internal static RuntimeRemoteArmingHostFacts FromRecords(
        RuntimeEntityObjectLifetime entityObjects)
    {
        ArgumentNullException.ThrowIfNull(entityObjects);
        return new RuntimeRemoteArmingHostFacts(
            DrawnBody: serverGuid =>
                entityObjects.Entities.TryGetActive(
                    serverGuid,
                    out RuntimeEntityRecord record)
                    ? new RuntimeRemoteArmingDrawnBody(
                        record,
                        () => record.PhysicsBody?.Position,
                        () => entityObjects.Entities.IsCurrent(record))
                    : null,
            WireOriginToWorld: (cellId, x, y, z) =>
                entityObjects.Physics.WireOriginToWorldFrame(cellId, x, y, z),
            InteractionTargetPosition: serverGuid =>
                entityObjects.Physics.InteractionTargetPosition(serverGuid));
    }
}

/// <summary>
/// One body a host is holding, and the two questions the arming asks about
/// THAT body rather than about whatever is under the id later.
/// </summary>
/// <remarks>
/// Both questions are bound to the same hold the record came from, which is
/// what makes the answers consistent: a physics host installed under this body
/// reports the pose of this hold and stops being current when this hold does,
/// not when some later one does.
/// </remarks>
/// <param name="Record">The record the body belongs to.</param>
/// <param name="Position">
/// Where this host has the body, or null to fall back to the body's own
/// position.
/// </param>
/// <param name="IsCurrent">Whether this hold is still the current one.</param>
internal readonly record struct RuntimeRemoteArmingDrawnBody(
    RuntimeEntityRecord Record,
    Func<Vector3?> Position,
    Func<bool> IsCurrent);

/// <summary>
/// What one accepted position did to a body: which of the four things the
/// arming can do to a body it did, and what came of it.
/// </summary>
internal enum RuntimeRemoteContactArm : byte
{
    /// <summary>A body with nothing under it takes the wire pose outright.</summary>
    AirborneSnap,

    /// <summary>A grounded body near its wire pose is asked to catch up.</summary>
    SteadyStateInterpolate,

    /// <summary>A body far from its wire pose is re-placed there.</summary>
    FarSnapPlacement,

    /// <summary>A body the server moved outright is re-placed there.</summary>
    TeleportPlacement,

    /// <summary>No classification claimed it; it catches up like any other.</summary>
    UnroutedCatchUp,
}

internal readonly record struct RuntimeRemoteContactRouting(
    RuntimeRemoteContactArm Arm,
    RuntimeRemotePlacementExecutionStatus? Placement,
    RuntimeRemoteSteadyStatePosition.Action? Interpolation = null);

/// <summary>
/// Arms another creature's body from the server's word about it: gives the
/// body the bindings it needs to move itself, hands an inbound movement to the
/// interpreter, and decides what an accepted position does to the body.
/// </summary>
/// <remarks>
/// This is one implementation for every client, because arming is a decision
/// about the body and not about the window. A client that draws the body hangs
/// its own facts off the narrow seam above; a client that draws nothing answers
/// the same questions from the record. Both then reach the same bindings, the
/// same destination and the same one of the four position arms.
/// </remarks>
internal sealed class RuntimeRemoteArming
{
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly RuntimeRemotePlacementDriveController _placementDrive;
    private readonly RuntimeRemoteArmingHostFacts _facts;
    private readonly RemoteInboundMotionDispatcher _inboundMotion;

    /// <summary>
    /// The one clock a body is timed by. Everything a body does -- how often
    /// it tells its watchers where it has got to, how long it has been
    /// walking -- is measured against the runtime's own clock rather than
    /// against the wall, because the wall runs at a different rate on a
    /// client that is drawing and one that is not, and a throttle read off
    /// two different clocks is two different throttles.
    /// </summary>
    private readonly IGameRuntimeClock _clock;

    /// <summary>
    /// Builds the arming and, with it, the drive that carries out an accepted
    /// re-placement of a remote body. The drive is built here rather than at a
    /// host because a body re-placed by the server has to land in the same
    /// place on every client.
    /// </summary>
    internal static RuntimeRemoteArming Create(
        RuntimeEntityObjectLifetime entityObjects,
        IGameRuntimeClock clock,
        IPreparedCollisionSource collisionSource,
        IRuntimeRemotePlacementServiceWindow serviceWindow,
        RuntimeRemoteArmingHostFacts? hostFacts = null)
    {
        ArgumentNullException.ThrowIfNull(entityObjects);
        ArgumentNullException.ThrowIfNull(clock);
        return new RuntimeRemoteArming(
            entityObjects,
            new RuntimeRemotePlacementDriveController(
                entityObjects,
                clock,
                collisionSource,
                serviceWindow),
            clock,
            hostFacts
                ?? RuntimeRemoteArmingHostFacts.FromRecords(entityObjects));
    }

    internal RuntimeRemoteArming(
        RuntimeEntityObjectLifetime entityObjects,
        RuntimeRemotePlacementDriveController placementDrive,
        IGameRuntimeClock clock,
        RuntimeRemoteArmingHostFacts facts)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _placementDrive = placementDrive
            ?? throw new ArgumentNullException(nameof(placementDrive));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(facts.DrawnBody);
        ArgumentNullException.ThrowIfNull(facts.WireOriginToWorld);
        ArgumentNullException.ThrowIfNull(facts.InteractionTargetPosition);
        _facts = facts;
        _inboundMotion = new RemoteInboundMotionDispatcher(
            RouteServerMoveTo,
            StickToObjectFromWire);
    }

    internal RuntimeRemotePlacementDriveController PlacementDrive =>
        _placementDrive;

    /// <summary>The dispatcher that hands an inbound movement to a body.</summary>
    internal RemoteInboundMotionDispatcher InboundMotion => _inboundMotion;

    private RuntimePhysicsState Physics => _entityObjects.Physics;

    /// <summary>
    /// Gives a body everything it needs to move itself: the sink its cycles
    /// are announced through, the callbacks its interpreter reaches for, and —
    /// once — the physics host that answers where it is and what it is chasing.
    /// </summary>
    /// <remarks>
    /// The host is installed at most once per body and rebound rather than
    /// replaced afterwards, so anything already holding it keeps working. The
    /// host's own position answer prefers where the calling host has the body,
    /// because on a client with a window that is the pose the world was drawn
    /// from and the one a walk must be measured against.
    /// </remarks>
    internal MotionTableDispatchSink? EnsureRemoteMotionBindings(
        RemoteMotion remote,
        AnimationSequencer? sequencer,
        uint serverGuid)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (sequencer is not null && remote.Sink is null)
        {
            remote.Sink = new MotionTableDispatchSink(sequencer);
            remote.Motion.DefaultSink = remote.Sink;
        }
        if (sequencer is not null)
        {
            remote.Motion.RemoveLinkAnimations =
                () => sequencer.Manager.HandleEnterWorld();
            remote.Motion.InitializeMotionTables =
                () => sequencer.Manager.InitializeState();
            remote.Motion.CheckForCompletedMotions =
                sequencer.Manager.CheckForCompletedMotions;
        }

        if (remote.Host is not null)
            return remote.Sink;
        if (_facts.DrawnBody(serverGuid) is not { } drawn
            || !ReferenceEquals(drawn.Record.RemoteMotion, remote))
        {
            return remote.Sink;
        }

        RuntimeEntityRecord hostRecord = drawn.Record;
        PhysicsBody body = remote.Body;
        EntityPhysicsHost host = null!;
        remote.Movement.MoveToFactory = () =>
        {
            var moveTo = new MoveToManager(
                remote.Motion,
                stopCompletely: () => remote.Movement.PerformMovement(
                    new MovementStruct
                    {
                        Type = MovementType.StopCompletely,
                    }),
                getPosition: () => new Position(
                    remote.CellId, body.Position, body.Orientation),
                getHeading: () => MoveToMath.GetHeading(body.Orientation),
                setHeading: (heading, _) => body.Orientation =
                    MoveToMath.SetHeading(body.Orientation, heading),
                getOwnRadius: () => BodyShape(serverGuid).Radius,
                getOwnHeight: () => BodyShape(serverGuid).Height,
                contact: () => body.OnWalkable,
                isInterpolating: () => remote.Interp.IsActive,
                getVelocity: () => body.Velocity,
                getSelfId: () => serverGuid,
                setTarget: (context, topLevelId, radius, quantum) =>
                    host.SetTarget(context, topLevelId, radius, quantum),
                clearTarget: () => host.ClearTarget(),
                getTargetQuantum: () => host.TargetManager.GetTargetQuantum(),
                setTargetQuantum: quantum =>
                    host.TargetManager.SetTargetQuantum(quantum),
                curTime: NowSeconds);
            moveTo.StickTo = (topLevelId, radius, height) =>
                host.PositionManager.StickTo(topLevelId, radius, height);
            moveTo.Unstick = host.PositionManager.UnStick;
            return moveTo;
        };
        remote.Motion.InterruptCurrentMovement =
            () => remote.Movement.CancelMoveTo(WeenieError.ActionCancelled);

        var configuredHost = new EntityPhysicsHost(
            serverGuid,
            getPosition: () => new Position(
                hostRecord.FullCellId,
                drawn.Position() ?? body.Position,
                body.Orientation),
            getVelocity: () => body.Velocity,
            getRadius: () => BodyShape(serverGuid).Radius,
            inContact: () => body.OnWalkable,
            minterpMaxSpeed: () => remote.Motion.GetAdjustedMaxSpeed(),
            curTime: NowSeconds,
            physicsTimerTime: NowSeconds,
            getObjectA: Physics.ResolveObjectTableHost,
            handleUpdateTarget: info => remote.Movement.HandleUpdateTarget(info),
            interruptCurrentMovement: () => remote.Movement.CancelMoveTo(
                WeenieError.ActionCancelled));
        host = Physics.InstallOrRebindPhysicsHost(
            hostRecord,
            configuredHost,
            drawn.IsCurrent);
        remote.MarkFullPhysicsHostBound();

        remote.Movement.MakeMoveToManager();
        remote.Motion.UnstickFromObject = host.PositionManager.UnStick;
        return remote.Sink;
    }

    /// <summary>
    /// Settles a body that has nothing under it yet onto the ground beneath
    /// the place the server named, so that its first step is taken from the
    /// floor rather than from mid-air.
    /// </summary>
    /// <remarks>
    /// A body arrives knowing where it is and nothing about what it is
    /// standing on. Until it has been settled once, everything downstream
    /// reads it as airborne: its own travel is thrown away, its accepted
    /// positions are written straight onto it instead of being caught up to,
    /// and it never plays a walk. So the first word the server says about a
    /// body's place is also when it is put on the ground. A body already in
    /// contact is left exactly as it is; the settle is a one-off, not a
    /// correction.
    /// </remarks>
    internal void SeatBodyIfUnseated(
        RuntimeEntityRecord record,
        RemoteMotion remote,
        Vector3 worldPosition,
        uint cellId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(remote);
        if (remote.Body.InContact)
            return;

        (float radius, float height) = BodyShape(record.ServerGuid);
        if (radius < MinimumSettleRadius)
        {
            radius = FallbackSettleRadius;
            height = FallbackSettleHeight;
        }

        ObjectInfoState moverFlags = IsPlayerGuid(record.ServerGuid)
            ? ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide
            : ObjectInfoState.EdgeSlide;
        if (!SpawnPlacementSettler.TrySettle(
                Physics.Engine,
                remote.Body,
                worldPosition,
                cellId,
                radius,
                height,
                moverFlags,
                record.LocalEntityId ?? 0u,
                remote.Movement.HitGround,
                remote.Motion.LeaveGround))
        {
            return;
        }
        remote.Airborne = !remote.Body.OnWalkable;
    }

    /// <summary>
    /// Below this the authored shape is not to hand at all, and a body
    /// settled as a point would fall through anything it should stand on.
    /// </summary>
    private const float MinimumSettleRadius = 0.05f;

    /// <summary>The girth and height of an ordinary person on two legs.</summary>
    private const float FallbackSettleRadius = 0.48f;

    private const float FallbackSettleHeight = 1.835f;

    private static bool IsPlayerGuid(uint guid) =>
        (guid & 0xFF000000u) == 0x50000000u;

    /// <summary>
    /// Sticks a body to the thing the server named, at that thing's own girth
    /// and height. An order naming something with no body to follow sticks to
    /// nothing, which is the same refusal a walk at it makes.
    /// </summary>
    internal void StickToObjectFromWire(
        IPhysicsObjHost? host,
        uint targetGuid)
    {
        if (host is not EntityPhysicsHost entityHost)
            return;
        if (_facts.InteractionTargetPosition(targetGuid) is null)
            return;
        (float radius, float height) = BodyShape(targetGuid);
        entityHost.PositionManager.StickTo(targetGuid, radius, height);
    }

    /// <summary>
    /// Turns a movement the server is driving into the walk or the turn it
    /// stands for. Returns false when the movement is not one of those, which
    /// leaves the ordinary interpreted-state path to handle it.
    /// </summary>
    /// <remarks>
    /// An order that names something to walk at or face becomes a walk at that
    /// body, but only when there is a body to follow; naming something this
    /// client cannot follow degrades to the place, or the heading, the order
    /// also carried. Both branches make the same test, because an order that
    /// half-succeeds is worse than one that degrades.
    /// </remarks>
    internal bool RouteServerMoveTo(
        MovementManager movement,
        uint cellId,
        WorldSession.EntityMotionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (update.MotionState.IsServerControlledMoveTo
            && update.MotionState.MoveToPath is { } path)
        {
            if (update.MotionState.MoveToRunRate is { } runRate)
                movement.Minterp.MyRunRate = runRate;

            Vector3 destination = _facts.WireOriginToWorld(
                path.OriginCellId, path.OriginX, path.OriginY, path.OriginZ);
            MovementParameters parameters = MovementParameters.FromWire(
                path.Bitfield,
                path.DistanceToObject,
                path.MinDistance,
                path.FailDistance,
                update.MotionState.MoveToSpeed ?? 1f,
                path.WalkRunThreshold,
                path.DesiredHeading);

            var walk = new MovementStruct { Params = parameters };
            if (update.MotionState.MovementType == 6
                && path.TargetGuid is { } targetGuid
                && _facts.InteractionTargetPosition(targetGuid)
                    is { } targetPosition
                && Physics.ResolveObjectTableHost(targetGuid) is not null)
            {
                walk.Type = MovementType.MoveToObject;
                walk.ObjectId = targetGuid;
                walk.TopLevelId = targetGuid;
                (walk.Radius, walk.Height) = BodyShape(targetGuid);
                walk.Pos = new Position(
                    cellId, targetPosition, Quaternion.Identity);
            }
            else
            {
                walk.Type = MovementType.MoveToPosition;
                walk.Pos = new Position(
                    cellId, destination, Quaternion.Identity);
            }
            movement.PerformMovement(walk);
            return true;
        }

        if (update.MotionState.IsServerControlledTurnTo
            && update.MotionState.TurnToPath is { } turnPath)
        {
            MovementParameters parameters =
                MovementParameters.FromWireTurnTo(
                    turnPath.Bitfield,
                    turnPath.Speed,
                    turnPath.DesiredHeading);

            var turn = new MovementStruct { Params = parameters };
            if (update.MotionState.MovementType == 8
                && turnPath.TargetGuid is { } turnTarget
                && _facts.InteractionTargetPosition(turnTarget)
                    is { } turnTargetPosition
                && Physics.ResolveObjectTableHost(turnTarget) is not null)
            {
                turn.Type = MovementType.TurnToObject;
                turn.ObjectId = turnTarget;
                turn.TopLevelId = turnTarget;
                turn.Pos = new Position(
                    cellId, turnTargetPosition, Quaternion.Identity);
            }
            else
            {
                turn.Type = MovementType.TurnToHeading;
                if (update.MotionState.MovementType == 8
                    && turnPath.WireHeading is { } wireHeading)
                {
                    parameters.DesiredHeading = wireHeading;
                }
            }
            movement.PerformMovement(turn);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Does to the body what an accepted position says to do: move it outright
    /// where the server moved it, take the pose outright when nothing is under
    /// the body, re-place it when it is far from where the server has it, and
    /// otherwise ask it to catch up on its own.
    /// </summary>
    internal RuntimeRemoteContactRouting ApplyRemoteContactRouting(
        RuntimeEntityRecord canonical,
        RemoteMotion remote,
        RuntimeAuthoritativePositionRoute? route,
        Vector3 worldPos,
        Quaternion rotation,
        bool willBeAdvanced,
        Func<bool> runTeleportHook) =>
        ApplyRemoteContactRouting(
            _placementDrive,
            canonical,
            remote,
            route,
            worldPos,
            rotation,
            willBeAdvanced,
            runTeleportHook);

    internal static RuntimeRemoteContactRouting ApplyRemoteContactRouting(
        RuntimeRemotePlacementDriveController placementDrive,
        RuntimeEntityRecord canonical,
        RemoteMotion remote,
        RuntimeAuthoritativePositionRoute? route,
        Vector3 worldPos,
        Quaternion rotation,
        bool willBeAdvanced,
        Func<bool> runTeleportHook)
    {
        ArgumentNullException.ThrowIfNull(placementDrive);
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(runTeleportHook);

        if (RuntimeRemoteTeleportPosition.OwnsTeleportPlacement(route))
        {
            bool hookRan = runTeleportHook();
            RuntimeRemotePlacementExecutionStatus teleportStatus =
                placementDrive.ApplyAcceptedRemoteTeleport(
                    canonical,
                    remote,
                    route!.Value);
            if (PhysicsDiagnostics.ProbeRemoteTeleportEnabled)
            {
                PhysicsDiagnostics.LogRemoteTeleport(
                    canonical.ServerGuid,
                    cause: route.Value.Authority.TeleportAdvanced
                        ? "teleport-ts"
                        : "cellless",
                    hookRan,
                    teleportStatus.ToString());
            }
            return new RuntimeRemoteContactRouting(
                RuntimeRemoteContactArm.TeleportPlacement,
                teleportStatus);
        }

        PhysicsDiagnostics.BeginRemoteSlideAttribution(canonical.ServerGuid);
        if (!remote.Body.InContact)
        {
            remote.Body.Position = worldPos;
            remote.Body.Orientation = rotation;
            return new RuntimeRemoteContactRouting(
                RuntimeRemoteContactArm.AirborneSnap, Placement: null);
        }

        switch (RuntimeRemoteFarSnapPosition.ResolveArm(route))
        {
            case RuntimeRemoteAcceptedPositionArm.FarSnapPlacement:
                return new RuntimeRemoteContactRouting(
                    RuntimeRemoteContactArm.FarSnapPlacement,
                    placementDrive.ApplyAcceptedRemoteFarSnap(
                        canonical,
                        remote,
                        route!.Value));

            case RuntimeRemoteAcceptedPositionArm.NearInterpolate:
                return new RuntimeRemoteContactRouting(
                    RuntimeRemoteContactArm.SteadyStateInterpolate,
                    Placement: null,
                    Interpolation: Interpolate(
                        canonical, remote, worldPos, rotation, willBeAdvanced));

            case RuntimeRemoteAcceptedPositionArm.AirborneNoOperation:
                throw new InvalidOperationException(
                    "A NoPositionOperation (airborne no-op) classification "
                    + "must be handled by the caller's own early return "
                    + "before routing; the authoritative move writes nothing "
                    + "at all on that branch.");

            default:
                return new RuntimeRemoteContactRouting(
                    RuntimeRemoteContactArm.UnroutedCatchUp,
                    Placement: null,
                    Interpolation: Interpolate(
                        canonical, remote, worldPos, rotation, willBeAdvanced));
        }
    }

    private static RuntimeRemoteSteadyStatePosition.Action Interpolate(
        RuntimeEntityRecord canonical,
        RemoteMotion remote,
        Vector3 worldPos,
        Quaternion rotation,
        bool willBeAdvanced) =>
        RuntimeRemoteSteadyStatePosition.ApplyInterpolate(
            remote,
            worldPos,
            rotation,
            isMovingTo: remote.Movement.IsMovingTo(),
            willBeAdvanced,
            (canonical.Snapshot.Physics?.Position
                ?? canonical.Snapshot.Position)?.LandblockId ?? 0u);

    /// <summary>
    /// Adopts the wire cell as the body's cell only when the routing moved the
    /// body to the wire pose. A re-placement owns its own cell commit, and a
    /// queued catch-up changes nothing physical yet: the body keeps its
    /// committed cell until its own sweeps carry it across.
    /// </summary>
    internal static bool TryAdoptWireCellAfterRouting(
        RemoteMotion remote,
        RuntimeRemoteContactRouting routing,
        uint wireCellId)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (routing.Arm is RuntimeRemoteContactArm.FarSnapPlacement
            or RuntimeRemoteContactArm.TeleportPlacement)
            return false;
        if (routing.Interpolation
            is RuntimeRemoteSteadyStatePosition.Action.Enqueued)
            return false;
        remote.CellId = wireCellId;
        return true;
    }

    internal static bool TryAdoptWireCellAfterRouting(
        RemoteMotion remote,
        RuntimeRemoteContactArm arm,
        uint wireCellId)
        => TryAdoptWireCellAfterRouting(
            remote,
            new RuntimeRemoteContactRouting(arm, Placement: null),
            wireCellId);

    /// <summary>
    /// How a body's declared girth and height are read: from the one place
    /// that knows the authored shape and the scale the server gave this
    /// particular body, so both clients measure the same gap.
    /// </summary>
    private (float Radius, float Height) BodyShape(uint serverGuid) =>
        Physics.EntityBodyShape(serverGuid) ?? (0f, 0f);

    private double NowSeconds() => _clock.SimulationTimeSeconds;
}
