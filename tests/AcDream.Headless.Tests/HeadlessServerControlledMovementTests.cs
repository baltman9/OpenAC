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
using AcDream.Runtime.Physics;
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
        // the characters watching it. That pass is the last stage of carrying
        // that thing's own body forward -- the one driver, and the same one on
        // a client with a window -- so the walk only follows if the body
        // really was carried. Without it the character walks to where the
        // thing used to be and stops there, eight metres out.
        Assert.True(
            world.AdvancedBodies > 0,
            "The creature's own body was never carried forward, so this "
            + "proves nothing about what re-aims the walk.");
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
        private readonly RemoteMotion _targetMotion;
        private readonly RuntimeRemoteBodyDrive _remoteBodies;
        private ushort _targetPositionSequence = 1;

        internal Fixture(
            float targetGirth = 0f,
            IRuntimeMotionContentSource? animationContent = null)
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
            _targetMotion = ArmTargetBody();
            // A session that holds a lease on the content files has cycles
            // for its character to move itself by. This stands in for that
            // lease with content authored by hand.
            if (animationContent is not null)
            {
                Runtime.EntityObjects.Physics.BindMotionContentSource(
                    animationContent);
            }

            _inertSession = HeadlessSessionHostTests
                .CreateInertLiveSessionHost();
            _frame = Runtime.CreateLocalPlayerFrameController(
                new HeadlessLocalPlayerFrameHost(Runtime, _inertSession),
                new HeadlessMovementInputSource(Runtime.MovementOwner));
            _remoteBodies = new RuntimeRemoteBodyDrive(
                Runtime.EntityObjects,
                () => Runtime.PlayerIdentity.ServerGuid,
                () => Runtime.MovementOwner.Controller?.Position);
        }

        /// <summary>
        /// Gives the thing being walked at a body and the bindings that let it
        /// move itself, the way the shared arming gives it them off an accepted
        /// movement. Without them there is nothing for the walk to follow and
        /// nothing to run the step that reports where it has got to.
        /// </summary>
        private RemoteMotion ArmTargetBody()
        {
            // No installed content here, so the animation content is a part
            // layout with no cycles: enough for a body to be carried and
            // swept, with nothing to play.
            Runtime.EntityObjects.Physics.BindMotionContentSource(
                new PartLayoutOnlyContent(SetupId));

            var body = new PhysicsBody
            {
                State = PhysicsStateFlags.ReportCollisions
                    | PhysicsStateFlags.EdgeSlide,
                InWorld = true,
                Orientation = Quaternion.Identity,
            };
            body.SnapToCell(Cell, TargetStart, TargetStart);
            Runtime.EntityObjects.Entities.SetPhysicsBody(_target, body);
            Runtime.EntityObjects.Physics.AcknowledgeSpatialProjection(
                _target,
                spatial: true);
            RemoteMotion remote =
                Runtime.EntityObjects.Physics.GetOrCreateRemoteMotion(_target);
            remote.CellId = Cell;
            remote.LastServerPos = TargetStart;
            remote.LastServerPosTime = 1.0;
            Runtime.EntityObjects.Physics.AcknowledgeSpatialProjection(
                _target,
                spatial: true);

            RuntimeRemoteArming arming = RuntimeRemoteArming.Create(
                Runtime.EntityObjects,
                Runtime.Clock,
                new OneAuthoredShape(SetupId, 0.5f),
                new AnyDestination());
            _ = arming.EnsureRemoteMotionBindings(remote, null, Target);
            return remote;
        }

        /// <summary>Any destination is serviceable in this fixture.</summary>
        private sealed class AnyDestination
            : IRuntimeRemotePlacementServiceWindow
        {
            public bool IsWithinServiceWindow(uint landblockId) => true;
        }

        internal GameRuntime Runtime { get; }

        internal PlayerMovementController Controller { get; }

        internal int AdvancedBodies { get; private set; }

        /// <summary>
        /// Runs the session for a few steps, which is how long it takes the
        /// character to take hold of its own body: its cycles are built from
        /// content, and it is given them on the first step it takes. Taking
        /// hold clears whatever the character was told to do before it had a
        /// body, so a movement asked for earlier than this is dropped -- the
        /// same on either host, and the reason these tests ask afterwards.
        /// </summary>
        internal void TakeHoldOfItsOwnBody() => Advance(ticks: 3);

        /// <summary>
        /// How many points a cycle has reached are being held, waiting for
        /// somebody to take them.
        /// </summary>
        internal int PendingCyclePoints =>
            Runtime.EntityObjects.Physics.EntityRemoteAnimation(Player)
                ?.Sequencer?.PendingHooks.Count ?? 0;

        /// <summary>
        /// Walks the character to the thing in the world, the way asking to
        /// act on something out of arm's reach walks it there.
        /// </summary>
        internal bool BeginWalkToTarget()
        {
            Controller.Movement.MakeMoveToManager();
            Controller.SetLastMoveWasAutonomous(false);
            return Controller.Movement.PerformMovement(new MovementStruct
            {
                Type = MovementType.MoveToObject,
                ObjectId = Target,
                TopLevelId = Target,
                Pos = new Position(Cell, TargetStart, Quaternion.Identity),
                Params = new MovementParameters
                {
                    DistanceToObject = DistanceToObject,
                },
                // The thing has no girth of its own authored here, which
                // is what the server-driven walk resolves for it too.
                Radius = 0f,
                Height = 0f,
            }) == WeenieError.None;
        }

        internal void Advance(float seconds) =>
            Advance(ticks: (int)(seconds * 30f));

        internal void Advance(int ticks)
        {
            const float step = 1f / 30f;
            for (int tick = 0; tick < ticks; tick++)
            {
                // The same statements the windowless host's own tick makes,
                // in the same order: the character's own step, then every
                // other creature's body, then the close of the frame.
                _ = Runtime.Clock.Advance(step);
                _frame.AdvanceBeforeNetwork(step);
                _remoteBodies.Tick(step);
                AdvancedBodies += _remoteBodies.LastAdvancedCount;
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
            // The server moved the creature, and this client's body for it is
            // put where the server says. What the routing of an accepted
            // position does to a body has its own tests; what matters here is
            // that the body really is somewhere else.
            _targetMotion.Body.SnapToCell(Cell, position, position);
            _targetMotion.LastServerPos = position;
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

    /// <summary>
    /// A client with no installed content still knows a part layout: enough
    /// for a body to be carried and swept, with no cycles to play.
    /// </summary>
    /// <summary>A fortieth of a turn a frame, thirty frames a second.</summary>
    private const float AuthoredTurnPerFrame = MathF.Tau / 40f;

    /// <summary>
    /// The fixed rate a character with no cycles is turned at, per unit of
    /// the turn speed the command carries. It is here to be compared against,
    /// not to be met.
    /// </summary>
    private const float RateWithNoCycles = 1.5f;

    [Fact]
    public void AWindowlessCharacterTurnsAtItsOwnCyclesRateAndNotAFixedOne()
    {
        using var world = new Fixture(
            animationContent: new TurningContent(SetupId));

        world.Runtime.MovementOwner.SetCommandInput(
            new MovementInput(TurnRight: true, Run: true));
        float before = world.Controller.Yaw;
        // Half a second: long enough to measure, short enough that the
        // character has not come round far enough for the angle to wrap.
        world.Advance(0.5f);
        float turnedPerSecond = MathF.Abs(world.Controller.Yaw - before) * 2f;

        // The character turned by its own cycle: a fortieth of a turn a frame
        // at thirty frames a second, taken at the speed the turn command
        // carries. The first steps go on finding the cycle, so this is within
        // a fifth of that and not to the last radian.
        float carried = world.Controller.Motion.InterpretedState.TurnSpeed;
        float fromTheCycle = AuthoredTurnPerFrame * 30f * carried;
        Assert.True(
            MathF.Abs(turnedPerSecond - fromTheCycle) < fromTheCycle * 0.2f,
            $"turned {turnedPerSecond:0.000} rad/s, not the cycle's "
            + $"{fromTheCycle:0.000} rad/s.");
        // And not at the fixed rate a character with no cycles is turned at,
        // which is what a windowless session used to do: the two are far
        // enough apart to leave the character off its bearing through a
        // corner, which is the whole difference.
        Assert.True(
            MathF.Abs(turnedPerSecond - (RateWithNoCycles * carried)) > 1f,
            $"turned {turnedPerSecond:0.000} rad/s, which is the fixed rate "
            + $"{RateWithNoCycles * carried:0.000} rad/s and not the cycle's.");
    }

    /// <summary>
    /// Animation content with one part layout and one table: standing still,
    /// and turning to the right by a hundredth of a turn a frame.
    /// </summary>
    private sealed class TurningContent : IRuntimeMotionContentSource
    {
        private const uint MotionTableId = 0x09000001u;
        private const uint StandingAnimation = 0x03000001u;
        private const uint TurningAnimation = 0x03000002u;
        private const uint NonCombat = 0x8000003Du;

        private readonly uint _setupId;
        private readonly Loader _loader = new();
        private readonly DatReaderWriter.DBObjs.MotionTable _table;

        internal TurningContent(uint setupId)
        {
            _setupId = setupId;
            _loader.Add(StandingAnimation, Authored(0f));
            _loader.Add(TurningAnimation, Authored(-AuthoredTurnPerFrame));
            _table = new DatReaderWriter.DBObjs.MotionTable
            {
                DefaultStyle =
                    (DatReaderWriter.Enums.MotionCommand)NonCombat,
            };
            _table.StyleDefaults[
                (DatReaderWriter.Enums.MotionCommand)NonCombat] =
                (DatReaderWriter.Enums.MotionCommand)MotionCommand.Ready;
            Cycle(MotionCommand.Ready, StandingAnimation);
            Cycle(MotionCommand.TurnRight, TurningAnimation);
            Cycle(MotionCommand.TurnLeft, TurningAnimation);
        }

        public IAnimationLoader AnimationLoader => _loader;

        public DatReaderWriter.DBObjs.MotionTable? TryGetMotionTable(
            uint motionTableId) =>
            motionTableId == MotionTableId ? _table : null;

        public DatReaderWriter.DBObjs.Setup? TryGetSetup(uint id)
        {
            if (id != _setupId)
                return null;
            var setup = new DatReaderWriter.DBObjs.Setup
            {
                DefaultMotionTable =
                    (DatReaderWriter.Types.QualifiedDataId<
                        DatReaderWriter.DBObjs.MotionTable>)MotionTableId,
            };
            setup.Parts.Add(0x01000000u);
            setup.DefaultScale.Add(Vector3.One);
            return setup;
        }

        private void Cycle(uint command, uint animationId)
        {
            var data = new DatReaderWriter.Types.MotionData();
            data.Anims.Add(new DatReaderWriter.Types.AnimData
            {
                AnimId = (DatReaderWriter.Types.QualifiedDataId<
                    DatReaderWriter.DBObjs.Animation>)animationId,
                LowFrame = 0,
                HighFrame = -1,
                Framerate = 30f,
            });
            _table.Cycles[(int)((NonCombat << 16) | (command & 0xFFFFFFu))] =
                data;
        }

        private static DatReaderWriter.DBObjs.Animation Authored(float turn)
        {
            var animation = new DatReaderWriter.DBObjs.Animation
            {
                Flags = DatReaderWriter.Enums.AnimationFlags.PosFrames,
            };
            for (int frame = 0; frame < 4; frame++)
            {
                var partFrame = new DatReaderWriter.Types.AnimationFrame(1);
                partFrame.Frames.Add(new DatReaderWriter.Types.Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
                animation.PartFrames.Add(partFrame);
                animation.PosFrames.Add(new DatReaderWriter.Types.Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = turn == 0f
                        ? Quaternion.Identity
                        : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn),
                });
            }
            return animation;
        }

        private sealed class Loader : IAnimationLoader
        {
            private readonly Dictionary<uint, DatReaderWriter.DBObjs.Animation>
                _animations = new();

            internal void Add(
                uint id,
                DatReaderWriter.DBObjs.Animation animation) =>
                _animations[id] = animation;

            public DatReaderWriter.DBObjs.Animation? LoadAnimation(
                uint requested) =>
                _animations.TryGetValue(
                    requested,
                    out DatReaderWriter.DBObjs.Animation? animation)
                    ? animation
                    : null;
        }
    }

    private sealed class PartLayoutOnlyContent(uint setupId)
        : IRuntimeMotionContentSource
    {
        public IAnimationLoader AnimationLoader { get; } =
            new NoAnimations();

        public DatReaderWriter.DBObjs.MotionTable? TryGetMotionTable(
            uint motionTableId) => null;

        public DatReaderWriter.DBObjs.Setup? TryGetSetup(uint id)
        {
            if (id != setupId)
                return null;
            var setup = new DatReaderWriter.DBObjs.Setup();
            setup.Parts.Add(0x01000000u);
            setup.DefaultScale.Add(Vector3.One);
            return setup;
        }

        private sealed class NoAnimations : IAnimationLoader
        {
            public DatReaderWriter.DBObjs.Animation? LoadAnimation(uint id) =>
                null;
        }
    }

    // -- a character that has cycles of its own ----------------------------

    /// <summary>How close the arrival tests insist the character gets.</summary>
    private const float ArrivalTolerance = 0.5f;

    /// <summary>
    /// A walk to a thing, on a session that holds the content its character's
    /// cycles are built from.
    /// <para>
    /// This is the whole of the difference. A dispatched cycle stays
    /// outstanding until something reports it finished, and every walk refuses
    /// to take a step while one is. A character with no cycles has none
    /// outstanding and walks; a character that has them, with nothing to
    /// report them finished, stands on the spot forever.
    /// </para>
    /// <para>
    /// Mutation check: take the windowless loop closure back out -- either the
    /// place finished cycles are reported to, or the taking of the points a
    /// step reached -- and all of these go red, the character never having
    /// left where it stood.
    /// </para>
    /// </summary>
    [Fact]
    public void AWalkToSomethingArrivesWhenTheCharacterHasItsOwnCycles()
    {
        using var world = new Fixture(
            animationContent: new WalkingAndTurningContent(SetupId));
        world.TakeHoldOfItsOwnBody();

        Assert.True(world.BeginWalkToTarget());
        float before = world.DistanceToTarget();
        world.Advance(seconds: 10f);
        float after = world.DistanceToTarget();

        Assert.True(
            after < before - 1f,
            $"the character stood still: {before:0.00} m -> {after:0.00} m");
        Assert.True(
            after <= DistanceToObject + ArrivalTolerance,
            $"the character stopped {after:0.00} m short of arm's reach");
        Assert.False(world.Controller.Movement.MoveTo!.IsMovingTo());
    }

    /// <summary>The same walk, ordered by the server and aimed at the thing.</summary>
    [Fact]
    public void AServerWalkToSomethingArrivesWhenTheCharacterHasItsOwnCycles()
    {
        using var world = new Fixture(
            animationContent: new WalkingAndTurningContent(SetupId));
        world.TakeHoldOfItsOwnBody();

        Assert.True(RuntimeServerControlledLocalMovement.TryApply(
            world.Runtime,
            MoveToObjectOrder()));
        float before = world.DistanceToTarget();
        world.Advance(seconds: 10f);
        float after = world.DistanceToTarget();

        Assert.True(
            after < before - 1f,
            $"the character stood still: {before:0.00} m -> {after:0.00} m");
        Assert.True(
            after <= DistanceToObject + ArrivalTolerance,
            $"the character stopped {after:0.00} m short of arm's reach");
    }

    /// <summary>
    /// A server walk that names a place rather than a thing. It goes through
    /// the same outstanding-cycle gate and stalls in the same way.
    /// </summary>
    [Fact]
    public void AServerWalkToAPlaceArrivesWhenTheCharacterHasItsOwnCycles()
    {
        using var world = new Fixture(
            animationContent: new WalkingAndTurningContent(SetupId));
        world.TakeHoldOfItsOwnBody();

        Assert.True(RuntimeServerControlledLocalMovement.TryApply(
            world.Runtime,
            MoveToPlaceOrder()));
        Assert.Equal(
            MovementType.MoveToPosition,
            world.Controller.Movement.MoveTo!.MovementTypeState);

        world.Advance(seconds: 10f);

        float away = Vector2.Distance(
            new Vector2(
                world.Controller.Position.X,
                world.Controller.Position.Y),
            new Vector2(TargetStart.X, TargetStart.Y));
        Assert.True(
            away <= 1f,
            $"the character stopped {away:0.00} m from the place it was sent");
    }

    /// <summary>
    /// Facing a heading is the smallest movement there is, and it shows the
    /// stall on its own: it turns on the spot, so a character that never comes
    /// round has not merely gone the wrong way, it has done nothing at all.
    /// </summary>
    [Fact]
    public void ATurnToAHeadingFinishesWhenTheCharacterHasItsOwnCycles()
    {
        using var world = new Fixture(
            animationContent: new WalkingAndTurningContent(SetupId));
        world.TakeHoldOfItsOwnBody();

        Assert.True(world.Controller.RequestTurnToHeading(90f));
        world.Advance(seconds: 8f);

        float heading = MoveToMath.HeadingFromYaw(world.Controller.Yaw);
        heading = ((heading % 360f) + 360f) % 360f;
        Assert.True(
            MathF.Abs(heading - 90f) < 5f,
            $"the character came round to {heading:0.0} degrees, not 90");
        Assert.False(world.Controller.Movement.MoveTo!.IsMovingTo());
    }

    /// <summary>
    /// The points one step of a cycle reaches are handed out on the step that
    /// reaches them and not kept. A session that never took them held every
    /// one it had ever reached for as long as it ran.
    /// </summary>
    [Fact]
    public void ReachedCyclePointsAreTakenEveryStepAndNotPiledUp()
    {
        const int Ticks = 10_000;
        using var world = new Fixture(
            animationContent: new WalkingAndTurningContent(SetupId));

        int highWater = 0;
        for (int tick = 0; tick < Ticks; tick++)
        {
            // Start and stop turning over and over, so the character crosses
            // between cycles hundreds of times and reaches the point at the
            // end of each crossing cycle every time.
            if (tick % 40 == 0)
            {
                world.Runtime.MovementOwner.SetCommandInput(
                    new MovementInput(
                        TurnRight: (tick / 40) % 2 == 0,
                        Run: true));
            }
            world.Advance(ticks: 1);
            highWater = Math.Max(highWater, world.PendingCyclePoints);
        }

        Assert.True(
            highWater <= 8,
            $"{highWater} reached cycle points were held at once over "
            + $"{Ticks} steps.");
    }

    private static WorldSession.EntityMotionUpdate MoveToPlaceOrder() =>
        new(
            Guid: Player,
            MotionState: new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: null,
                MovementType: 6,
                MoveToSpeed: 1f,
                MoveToRunRate: 1.75f,
                MoveToPath: new CreateObject.MoveToPathData(
                    TargetGuid: null,
                    OriginCellId: Cell,
                    OriginX: TargetStart.X,
                    OriginY: TargetStart.Y,
                    OriginZ: TargetStart.Z,
                    DistanceToObject: 0f,
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

    /// <summary>
    /// Animation content a character can really walk and turn by: one part
    /// layout, cycles for standing, running and turning, and a short crossing
    /// cycle between every pair of them.
    /// </summary>
    /// <remarks>
    /// The crossing cycles are what make this content say anything about
    /// completion. A cycle that loops forever never reaches its end, so a
    /// character playing only loops never reaches a point that has to be
    /// reported; a cycle that is crossed through does, every time, and that
    /// report is what lets the next step of a walk be taken.
    /// </remarks>
    private sealed class WalkingAndTurningContent : IRuntimeMotionContentSource
    {
        private const uint MotionTableId = 0x09000001u;
        private const uint StandingAnimation = 0x03000001u;
        private const uint RunningAnimation = 0x03000002u;
        private const uint TurningLeftAnimation = 0x03000003u;
        private const uint TurningRightAnimation = 0x03000004u;
        private const uint CrossingAnimation = 0x03000005u;
        private const uint NonCombat = 0x8000003Du;

        /// <summary>A tenth of a metre a frame, thirty frames a second.</summary>
        private const float RunMetresPerFrame = 0.1f;

        private static readonly uint[] Substates =
        [
            MotionCommand.Ready,
            MotionCommand.RunForward,
            MotionCommand.WalkForward,
            MotionCommand.TurnLeft,
            MotionCommand.TurnRight,
        ];

        private readonly uint _setupId;
        private readonly Loader _loader = new();
        private readonly DatReaderWriter.DBObjs.MotionTable _table;

        internal WalkingAndTurningContent(uint setupId)
        {
            _setupId = setupId;
            _loader.Add(StandingAnimation, Authored(Vector3.Zero, 0f));
            _loader.Add(
                RunningAnimation,
                Authored(new Vector3(0f, RunMetresPerFrame, 0f), 0f));
            _loader.Add(
                TurningLeftAnimation,
                Authored(Vector3.Zero, AuthoredTurnPerFrame));
            _loader.Add(
                TurningRightAnimation,
                Authored(Vector3.Zero, -AuthoredTurnPerFrame));
            _loader.Add(CrossingAnimation, Authored(Vector3.Zero, 0f));

            _table = new DatReaderWriter.DBObjs.MotionTable
            {
                DefaultStyle =
                    (DatReaderWriter.Enums.MotionCommand)NonCombat,
            };
            _table.StyleDefaults[
                (DatReaderWriter.Enums.MotionCommand)NonCombat] =
                (DatReaderWriter.Enums.MotionCommand)MotionCommand.Ready;
            Cycle(MotionCommand.Ready, StandingAnimation);
            Cycle(MotionCommand.RunForward, RunningAnimation);
            Cycle(MotionCommand.WalkForward, RunningAnimation);
            Cycle(MotionCommand.TurnLeft, TurningLeftAnimation);
            Cycle(MotionCommand.TurnRight, TurningRightAnimation);
            foreach (uint from in Substates)
            {
                var crossings = new DatReaderWriter.Types.MotionCommandData();
                foreach (uint to in Substates)
                {
                    if (to != from)
                    {
                        crossings.MotionData[(int)to] =
                            Motion(CrossingAnimation);
                    }
                }
                _table.Links[(int)((NonCombat << 16) | (from & 0xFFFFFFu))] =
                    crossings;
            }
        }

        public IAnimationLoader AnimationLoader => _loader;

        public DatReaderWriter.DBObjs.MotionTable? TryGetMotionTable(
            uint motionTableId) =>
            motionTableId == MotionTableId ? _table : null;

        public DatReaderWriter.DBObjs.Setup? TryGetSetup(uint id)
        {
            if (id != _setupId)
                return null;
            var setup = new DatReaderWriter.DBObjs.Setup
            {
                DefaultMotionTable =
                    (DatReaderWriter.Types.QualifiedDataId<
                        DatReaderWriter.DBObjs.MotionTable>)MotionTableId,
            };
            setup.Parts.Add(0x01000000u);
            setup.DefaultScale.Add(Vector3.One);
            return setup;
        }

        private void Cycle(uint command, uint animationId) =>
            _table.Cycles[(int)((NonCombat << 16) | (command & 0xFFFFFFu))] =
                Motion(animationId);

        private static DatReaderWriter.Types.MotionData Motion(
            uint animationId)
        {
            var data = new DatReaderWriter.Types.MotionData();
            data.Anims.Add(new DatReaderWriter.Types.AnimData
            {
                AnimId = (DatReaderWriter.Types.QualifiedDataId<
                    DatReaderWriter.DBObjs.Animation>)animationId,
                LowFrame = 0,
                HighFrame = -1,
                Framerate = 30f,
            });
            return data;
        }

        private static DatReaderWriter.DBObjs.Animation Authored(
            Vector3 travel,
            float turn)
        {
            var animation = new DatReaderWriter.DBObjs.Animation
            {
                Flags = DatReaderWriter.Enums.AnimationFlags.PosFrames,
            };
            for (int frame = 0; frame < 4; frame++)
            {
                var partFrame = new DatReaderWriter.Types.AnimationFrame(1);
                partFrame.Frames.Add(new DatReaderWriter.Types.Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
                animation.PartFrames.Add(partFrame);
                animation.PosFrames.Add(new DatReaderWriter.Types.Frame
                {
                    Origin = travel,
                    Orientation = turn == 0f
                        ? Quaternion.Identity
                        : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn),
                });
            }
            return animation;
        }

        private sealed class Loader : IAnimationLoader
        {
            private readonly Dictionary<uint, DatReaderWriter.DBObjs.Animation>
                _animations = new();

            internal void Add(
                uint id,
                DatReaderWriter.DBObjs.Animation animation) =>
                _animations[id] = animation;

            public DatReaderWriter.DBObjs.Animation? LoadAnimation(
                uint requested) =>
                _animations.TryGetValue(
                    requested,
                    out DatReaderWriter.DBObjs.Animation? animation)
                    ? animation
                    : null;
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
