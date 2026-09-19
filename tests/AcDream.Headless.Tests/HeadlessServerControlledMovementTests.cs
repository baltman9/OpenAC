using System.Collections.Immutable;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

/// <summary>
/// A movement the server drives at the local character, on a host with no
/// window. Ask to use — or swing at — something a few metres off and the
/// server does not refuse: it orders the character to walk in and waits. A
/// character that never walks is told, seconds later, that the action is
/// "done", with nothing done and no error; from four to eight metres that is
/// every single swing.
/// <para>
/// The order names a thing, not a place, and following a thing needs that
/// thing to have a body the walk can ask where it is. Both hosts share the
/// movement machinery, so both must be able to answer that question.
/// </para>
/// </summary>
public sealed class HeadlessServerControlledMovementTests
{
    private const uint Cell = 0xA9B40001u;
    private const uint Player = 0x50000101u;
    private const uint Target = 0x80000DADu;
    private const float DistanceToObject = 1.8f;

    /// <summary>The authored shape both fixture entities are built from.</summary>
    private const uint SetupId = 0x02000001u;

    /// <summary>The character's start, in metres inside its landblock.</summary>
    private static readonly Vector3 PlayerStart = new(96f, 97f, 0f);

    /// <summary>Six metres east: within a melee reach, beyond arm's reach.</summary>
    private static readonly Vector3 TargetStart = new(102f, 97f, 0f);

    [Fact]
    public void AnOrderToWalkToSomethingFollowsItsBodyAndClosesTheDistance()
    {
        using var world = new Fixture();

        Assert.True(RuntimeServerControlledLocalMovement.TryApply(
            world.Runtime,
            MoveToObjectOrder()));

        MoveToManager moveTo = world.Controller.Movement.MoveTo!;
        // The order named a thing; the walk must follow that thing, not the
        // place the order happened to be written at.
        Assert.Equal(MovementType.MoveToObject, moveTo.MovementTypeState);
        Assert.Equal(Target, moveTo.TopLevelObjectId);
        Assert.True(moveTo.Initialized);

        float before = world.DistanceToTarget();
        world.Advance(seconds: 6f);
        float after = world.DistanceToTarget();

        Assert.True(
            after < before - 1f,
            $"the character stood still: {before:0.00} m -> {after:0.00} m");
        Assert.True(
            after <= DistanceToObject + 0.5f,
            $"the character stopped {after:0.00} m short of arm's reach");
    }

    [Fact]
    public void AWalkToSomethingFollowsItWhenItMoves()
    {
        using var world = new Fixture();

        Assert.True(RuntimeServerControlledLocalMovement.TryApply(
            world.Runtime,
            MoveToObjectOrder()));

        // Half a second in, the thing steps eight metres aside - a creature
        // backing off mid-swing, the ordinary case.
        world.Advance(seconds: 0.5f);
        world.MoveTarget(TargetStart + new Vector3(0f, 8f, 0f));
        world.Advance(seconds: 8f);

        // The walk is steered by what the thing being followed reports about
        // itself, and it reports nothing unless it is given its own pass over
        // the characters watching it. Without that pass the character walks to
        // where the thing used to be and stops there, eight metres out.
        float after = world.DistanceToTarget();
        Assert.True(
            after <= DistanceToObject + 0.5f,
            $"the character followed the old position: {after:0.00} m out");
    }

    /// <summary>
    /// Asking the character to face a heading while the server is walking it
    /// somewhere ends that walk. This is not a window-less trait: the request
    /// takes control back from the server and cancels whatever it had running,
    /// in the one movement owner both hosts share, so a character driven by
    /// automation that re-faces its quarry loses the approach on either host.
    /// Recorded here because it is the obvious suspect for "the character
    /// never closed the distance" and it is NOT the reason.
    /// </summary>
    [Fact]
    public void FacingSomethingDuringAServerWalkEndsThatWalkOnEitherHost()
    {
        using var world = new Fixture();

        Assert.True(RuntimeServerControlledLocalMovement.TryApply(
            world.Runtime,
            MoveToObjectOrder()));
        world.Advance(seconds: 0.5f);
        Assert.True(world.Controller.Movement.MoveTo!.IsMovingTo());

        Assert.True(world.Controller.RequestTurnToHeading(90f));

        Assert.Equal(
            MovementType.TurnToHeading,
            world.Controller.Movement.MoveTo!.MovementTypeState);
    }

    /// <summary>
    /// The gap the order asks for is between the two bodies' SIDES, not
    /// between their middles: the server sets it from its own use radius and
    /// measures it cylinder to cylinder. A creature wider than that gap can
    /// therefore never be reached at all if its girth is taken as nothing —
    /// the character keeps walking into a thing it is already touching and the
    /// charge never finishes, which is what a swing at a big creature does.
    /// Mutation: report a flat zero girth for an entity's body and this fails,
    /// with the character stopped a little over half a metre from the middle
    /// of a creature one and a fifth metres wide.
    /// </summary>
    [Fact]
    public void AWalkToSomethingWiderThanTheGapStopsAtItsSide()
    {
        const float girth = 1.2f;
        const float gap = 0.6f;
        using var world = new Fixture(targetGirth: girth);

        Assert.True(RuntimeServerControlledLocalMovement.TryApply(
            world.Runtime,
            MoveToObjectOrder(distanceToObject: gap)));

        world.Advance(seconds: 8f);

        float after = world.DistanceToTarget();
        Assert.True(
            after >= girth,
            $"the character walked inside the creature: {after:0.00} m from "
                + "its middle");
        Assert.True(
            after <= OwnGirth + girth + gap + 0.5f,
            $"the character stopped {after:0.00} m out, well short of the gap "
                + "the order asked for");
    }

    /// <summary>The girth the windowless host gives the local character.</summary>
    private const float OwnGirth = 0.48f;

    // -- the order ---------------------------------------------------------

    private static WorldSession.EntityMotionUpdate MoveToObjectOrder(
        float distanceToObject = DistanceToObject) =>
        new(
            Guid: Player,
            MotionState: new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: null,
                MovementType: 6,
                MoveToSpeed: 1f,
                MoveToRunRate: 1.75f,
                MoveToPath: new CreateObject.MoveToPathData(
                    TargetGuid: Target,
                    OriginCellId: Cell,
                    OriginX: TargetStart.X,
                    OriginY: TargetStart.Y,
                    OriginZ: TargetStart.Z,
                    DistanceToObject: distanceToObject,
                    MinDistance: 0f,
                    FailDistance: 50f,
                    WalkRunThreshold: 15f,
                    DesiredHeading: 0f,
                    Bitfield: 0x403u),
                TurnToPath: null),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

    // -- a windowless session with a character and one thing to walk to ----

    private sealed class Fixture : IDisposable
    {
        private readonly HeadlessCredentialSecret _credential;
        private readonly HeadlessSessionHost _host;
        private readonly LiveSessionHost _inertSession;
        private readonly RuntimeLocalPlayerFrameController _frame;
        private readonly RuntimeEntityRecord _target;
        private readonly OneAuthoredShape? _shapes;
        private ushort _targetPositionSequence = 1;

        internal Fixture(float targetGirth = 0f)
        {
            _credential = new HeadlessCredentialSecret("fixture", "password");
            _host = new HeadlessSessionHost(
                HeadlessSessionHostTests.Descriptor(),
                _credential,
                new HeadlessDiagnosticWriter(TextWriter.Null),
                new HeadlessSessionHostTests.FixtureSessionOperations());
            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                _host.Start().Status);

            Runtime = _host.Runtime;
            Runtime.PlayerIdentity.ServerGuid = Player;
            if (targetGirth > 0f)
            {
                _shapes = new OneAuthoredShape(SetupId, targetGirth);
                Runtime.EntityObjects.Physics.BindSetupCollisionSource(_shapes);
            }

            RuntimeFirstEntryDriveController firstEntry =
                HeadlessSessionHostTests.CreateFirstEntryDrive(Runtime);
            RuntimeEntityRecord player = Register(
                Spawn(Player, PlayerStart),
                isLocalPlayer: true);
            var projection = new HeadlessSessionWorldProjection(
                Runtime,
                new HeadlessSessionHostTests
                    .CollisionGenerationCommittingNeighborhood(Runtime),
                firstEntry);
            projection.ProjectSpawn(player, isLocalPlayer: true);
            Controller = Assert.IsType<PlayerMovementController>(
                Runtime.MovementOwner.Controller);

            _target = Register(Spawn(Target, TargetStart), isLocalPlayer: false);

            _inertSession = HeadlessSessionHostTests
                .CreateInertLiveSessionHost();
            _frame = Runtime.CreateLocalPlayerFrameController(
                new HeadlessLocalPlayerFrameHost(Runtime, _inertSession),
                new HeadlessMovementInputSource(Runtime.MovementOwner));
        }

        internal GameRuntime Runtime { get; }

        internal PlayerMovementController Controller { get; }

        internal void Advance(float seconds)
        {
            const float step = 1f / 30f;
            int ticks = (int)(seconds / step);
            for (int tick = 0; tick < ticks; tick++)
            {
                // The same two statements the windowless host's own tick
                // makes, in the same order.
                _ = Runtime.Clock.Advance(step);
                _frame.AdvanceBeforeNetwork(step);
                _frame.RunPostNetworkCommandPhase();
            }
        }

        internal float DistanceToTarget()
        {
            Vector3 target = TargetPosition();
            return Vector2.Distance(
                new Vector2(Controller.Position.X, Controller.Position.Y),
                new Vector2(target.X, target.Y));
        }

        internal void MoveTarget(Vector3 position)
        {
            _targetPositionSequence++;
            Assert.True(Runtime.EntityObjects.TryApplyPosition(
                new WorldSession.EntityPositionUpdate(
                    Guid: Target,
                    Position: ServerPosition(position),
                    Velocity: null,
                    PlacementId: null,
                    IsGrounded: true,
                    InstanceSequence: 1,
                    PositionSequence: _targetPositionSequence,
                    TeleportSequence: 0,
                    ForcePositionSequence: 0),
                isLocalPlayer: false,
                forcePositionRotation: null,
                currentLocalVelocity: null,
                acknowledgeProjection: null,
                out _,
                out _,
                out _));
        }

        private Vector3 TargetPosition()
        {
            CreateObject.ServerPosition position =
                _target.Snapshot.Position!.Value;
            return new Vector3(
                position.PositionX,
                position.PositionY,
                position.PositionZ);
        }

        private RuntimeEntityRecord Register(
            WorldSession.EntitySpawn spawn,
            bool isLocalPlayer)
        {
            RuntimeEntityRecord record = (isLocalPlayer
                ? Runtime.EntityObjects.RegisterEntityWithInitialResidence(
                    spawn,
                    isLocalPlayer: true)
                : Runtime.EntityObjects.RegisterEntity(spawn)).Canonical!;
            Assert.True(Runtime.EntityObjects.ApplyAcceptedSpawn(
                record,
                record.CreateIntegrationVersion,
                record.Snapshot,
                replaceGeneration: false));
            return record;
        }

        public void Dispose()
        {
            _host.Dispose();
            _credential.Dispose();
            _shapes?.Dispose();
        }
    }

    /// <summary>One authored shape, for one id, and nothing else.</summary>
    private sealed class OneAuthoredShape : IPreparedCollisionSource
    {
        private readonly uint _id;
        private readonly FlatSetupCollision _setup;

        internal OneAuthoredShape(uint id, float radius)
        {
            _id = id;
            _setup = new FlatSetupCollision(
                ImmutableArray<FlatCollisionCylinder>.Empty,
                ImmutableArray<FlatCollisionSphere>.Empty,
                height: radius * 2f,
                radius,
                stepUpHeight: 0f,
                stepDownHeight: 0f);
        }

        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) =>
            sourceFileId == _id
                ? PreparedAssetPresence.Available
                : PreparedAssetPresence.Missing;

        public PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            sourceFileId == _id
                ? PreparedCollisionReadResult<FlatSetupCollision>.Loaded(_setup)
                : PreparedCollisionReadResult<FlatSetupCollision>.Missing;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatGfxObjCollisionAsset>.Missing;

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
                .Missing;

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatEnvCellTopology>.Missing;

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }

    private static CreateObject.ServerPosition ServerPosition(
        Vector3 position) =>
        new(Cell, position.X, position.Y, position.Z, 1f, 0f, 0f, 0f);

    private static WorldSession.EntitySpawn Spawn(uint guid, Vector3 position)
    {
        CreateObject.ServerPosition wire = ServerPosition(position);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: wire,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            wire,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            "Headless",
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
