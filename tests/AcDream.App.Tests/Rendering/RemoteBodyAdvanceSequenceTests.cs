using System.Globalization;
using System.Numerics;
using System.Text;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// What one windowed frame does to a creature's body, step by step: how far it
/// travels per step, whether it is standing on anything, which cell it ends up
/// in, where in each step its cycle's hooks come out, and which part poses the
/// presenter is handed.
///
/// Each test compares one report of the whole sequence rather than a handful of
/// separate numbers, because the thing worth pinning is the ORDER as much as
/// the values: a step that integrates before it advances the cycle, or hands
/// out hooks before it has moved, would still land in the right place on a
/// straight walk and be wrong everywhere else.
/// </summary>
public sealed class RemoteBodyAdvanceSequenceTests
{
    private const uint LocalGuid = 0x50000001u;
    private const uint RemoteGuid = 0x70000001u;
    private const uint Cell = 0x01010001u;
    private const uint Landblock = 0x0101FFFFu;

    /// <summary>
    /// A creature walking on a world with no collision loaded at all: the
    /// sweep is skipped, so this is the travel the authored cycle asks for and
    /// nothing else. Half a second of a cycle authored at a tenth of a metre
    /// per frame and thirty frames a second is one and a half metres, spent in
    /// three steps.
    /// </summary>
    [Fact]
    public void AWalkingCreatureSpendsTheFrameInStepsOfItsOwnCycle()
    {
        Fixture fixture = Fixture.Create(withCollision: false);

        string report = fixture.TickAndReport(0.5f);

        Assert.Equal(
            """
            step 1 hook: owner=1000000 body=(10.6000, 10.0000, 5.0000) walkable=True cell=01010001
            step 2 hook: owner=1000000 body=(11.2000, 10.0000, 5.0000) walkable=True cell=01010001
            step 3 hook: owner=1000000 body=(11.5000, 10.0000, 5.0000) walkable=True cell=01010001
            after: entity=(11.5000, 10.0000, 5.0000) cell=01010001
            after: body=(11.5000, 10.0000, 5.0000) walkable=True cell=01010001
            after: rootPosePublishes=1 fastestCycleTravel=3.0000
            schedule: composeParts=True partPoses=1 unsequenced=0.0000
            """,
            report);
    }

    /// <summary>
    /// The same walk over ground that is actually there. The body is swept
    /// against the world each step and the cell it is committed to is the one
    /// the sweep resolved, which is what makes the difference between walking
    /// and being dragged through a wall.
    /// </summary>
    [Fact]
    public void AWalkingCreatureIsSweptAgainstTheGroundItStandsOn()
    {
        Fixture fixture = Fixture.Create(withCollision: true);

        string report = fixture.TickAndReport(0.5f);

        Assert.Equal(
            """
            step 1 hook: owner=1000000 body=(10.6000, 10.0000, 5.0000) walkable=True cell=01010001
            step 2 hook: owner=1000000 body=(11.2000, 10.0000, 5.0000) walkable=True cell=01010001
            step 3 hook: owner=1000000 body=(11.5000, 10.0000, 5.0000) walkable=True cell=01010001
            after: entity=(11.5000, 10.0000, 5.0000) cell=01010001
            after: body=(11.5000, 10.0000, 5.0000) walkable=True cell=01010001
            after: rootPosePublishes=1 fastestCycleTravel=3.0000
            schedule: composeParts=True partPoses=1 unsequenced=0.0000
            """,
            report);
    }

    /// <summary>
    /// A frame short enough to be worth less than one step leaves the body
    /// exactly where it was: the time is carried on the body's own clock, not
    /// spent early.
    /// </summary>
    [Fact]
    public void AFrameTooShortToBeWorthAStepLeavesTheBodyWhereItWas()
    {
        Fixture fixture = Fixture.Create(withCollision: true);

        string report = fixture.TickAndReport(0.01f);

        Assert.Equal(
            """
            after: entity=(10.0000, 10.0000, 5.0000) cell=01010001
            after: body=(10.0000, 10.0000, 5.0000) walkable=True cell=01010001
            after: rootPosePublishes=0 fastestCycleTravel=0.0000
            no schedule
            """,
            report);
    }

    /// <summary>
    /// A creature the window cannot see still catches up to where the server
    /// last put it, and its cycle is not advanced while it is out of sight.
    /// </summary>
    [Fact]
    public void AnUnseenCreatureCatchesUpWithoutAdvancingItsCycle()
    {
        Fixture fixture = Fixture.Create(
            withCollision: true,
            hidden: true,
            catchUpTo: new Vector3(11f, 10f, 5f));

        string report = fixture.TickAndReport(0.5f);

        Assert.Equal(
            """
            step 1 hook: owner=1000000 body=(10.4000, 10.0000, 5.0000) walkable=True cell=01010001
            step 2 hook: owner=1000000 body=(10.8000, 10.0000, 5.0000) walkable=True cell=01010001
            step 3 hook: owner=1000000 body=(11.0000, 10.0000, 5.0000) walkable=True cell=01010001
            after: entity=(11.0000, 10.0000, 5.0000) cell=01010001
            after: body=(11.0000, 10.0000, 5.0000) walkable=True cell=01010001
            after: rootPosePublishes=1 fastestCycleTravel=0.0000
            schedule: composeParts=True partPoses=1 unsequenced=0.0000
            """,
            report);
    }

    /// <summary>
    /// A creature further away than the bubble the client keeps alive is not
    /// advanced at all, however much time has passed.
    /// </summary>
    [Fact]
    public void ACreatureBeyondTheLiveBubbleIsNotAdvanced()
    {
        Fixture fixture = Fixture.Create(withCollision: true);

        string report = fixture.TickAndReport(
            0.5f,
            playerPosition: new Vector3(10_000f, 10f, 5f));

        Assert.Equal(
            """
            after: entity=(10.0000, 10.0000, 5.0000) cell=01010001
            after: body=(10.0000, 10.0000, 5.0000) walkable=True cell=01010001
            after: rootPosePublishes=0 fastestCycleTravel=0.0000
            no schedule
            """,
            report);
    }

    private sealed class Fixture
    {
        private const uint AnimationId = 0x0300AA01u;

        private readonly LiveEntityRuntime _live;
        private readonly LiveEntityRecord _record;
        private readonly WorldEntity _entity;
        private readonly RemoteMotion _remote;
        private readonly LiveEntityAnimationState _animation;
        private readonly LiveEntityAnimationScheduler _scheduler;
        private readonly List<string> _log = new();
        private int _rootPosePublishes;
        private int _steps;

        private Fixture(
            LiveEntityRuntime live,
            LiveEntityRecord record,
            WorldEntity entity,
            RemoteMotion remote,
            LiveEntityAnimationState animation)
        {
            _live = live;
            _record = record;
            _entity = entity;
            _remote = remote;
            _animation = animation;
            var identity = new LocalPlayerIdentityState { ServerGuid = LocalGuid };
            var poses = new EntityEffectPoseRegistry();
            foreach (LiveEntityRecord materialized in live.MaterializedRecords)
                poses.PublishMeshRefs(materialized.WorldEntity!);
            _scheduler = new LiveEntityAnimationScheduler(
                live,
                identity,
                new RuntimeRemoteBodyOwner(live.Physics),
                new LiveEntityOrdinaryPhysicsUpdater(
                    live.Physics,
                    (_, _) => (0.48f, 1.835f),
                    (_, _) => (
                        System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty,
                        1f,
                        0.4f,
                        0.4f)),
                new ProjectileController(live),
                new LoggingRootPosePublisher(poses, () => _rootPosePublishes++),
                new LoggingHookSink(LogHook));
        }

        internal static Fixture Create(
            bool withCollision,
            bool hidden = false,
            Vector3? catchUpTo = null)
        {
            PhysicsStateFlags state = hidden
                ? PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions
                : PhysicsStateFlags.ReportCollisions;
            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                Landblock,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            LiveEntityRuntime live = LiveEntityRuntimeFixture.Create(
                spatial,
                new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
            if (withCollision)
                AddFlatGround(live.Physics.Engine);
            LiveEntityRecord record = live.RegisterAndMaterializeProjection(
                Spawn(state),
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = RemoteGuid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(10f, 10f, 5f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = Cell,
                });
            WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
            LiveEntityAnimationState animation = BuildAnimation(entity);
            record.HasPartArray = true;
            live.SetAnimationRuntime(RemoteGuid, animation);

            var remote = new RemoteMotion { CellId = Cell };
            remote.Body.Position = entity.Position;
            remote.Body.Orientation = entity.Rotation;
            remote.Body.InWorld = true;
            remote.Body.TransientState = TransientStateFlags.Active
                | TransientStateFlags.Contact
                | TransientStateFlags.OnWalkable;
            if (catchUpTo is { } target)
            {
                remote.Interp.Enqueue(
                    target,
                    heading: 0f,
                    isMovingTo: false,
                    currentBodyPosition: entity.Position);
            }

            live.SetRemoteMotionRuntime(RemoteGuid, remote);
            return new Fixture(live, record, entity, remote, animation);
        }

        internal string TickAndReport(
            float elapsedSeconds,
            Vector3? playerPosition = null)
        {
            _log.Clear();
            _steps = 0;
            _rootPosePublishes = 0;
            _remote.MaxRootMotionSpeedSinceLastUP = 0f;
            bool scheduled = _scheduler
                .Tick(
                    elapsedSeconds,
                    playerPosition ?? _entity.Position,
                    localHiddenPartPoseDirty: false,
                    liveCenterX: 0,
                    liveCenterY: 0)
                .TryGetValue(
                    _record.ProjectionKey!.Value,
                    out LiveEntityAnimationSchedule schedule);

            _log.Add($"after: entity={Show(_entity.Position)} cell={_entity.ParentCellId:X8}");
            _log.Add(
                $"after: body={Show(_remote.Body.Position)} "
                + $"walkable={_remote.Body.OnWalkable} cell={_remote.CellId:X8}");
            _log.Add(
                $"after: rootPosePublishes={_rootPosePublishes} "
                + $"fastestCycleTravel={Show(_remote.MaxRootMotionSpeedSinceLastUP)}");
            _log.Add(scheduled
                ? $"schedule: composeParts={schedule.ComposeParts} "
                    + $"partPoses={schedule.SequenceFrames?.Count ?? -1} "
                    + $"unsequenced={Show(schedule.LegacyAdvanceSeconds)}"
                : "no schedule");
            return string.Join('\n', _log);
        }

        private void LogHook(uint ownerLocalId, AnimationSequencer sequencer)
        {
            _steps++;
            _log.Add(
                $"step {_steps} hook: owner={ownerLocalId} "
                + $"body={Show(_remote.Body.Position)} "
                + $"walkable={_remote.Body.OnWalkable} cell={_remote.CellId:X8}");
        }

        private static string Show(Vector3 value) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"({value.X:F4}, {value.Y:F4}, {value.Z:F4})");

        private static string Show(float value) =>
            value.ToString("F4", CultureInfo.InvariantCulture);

        private static void AddFlatGround(PhysicsEngine engine)
        {
            var heights = new byte[81];
            var table = new float[256];
            Array.Fill(table, 5f);
            engine.AddLandblock(
                Landblock,
                new TerrainSurface(heights, table),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                0f,
                0f);
        }

        private static LiveEntityAnimationState BuildAnimation(WorldEntity entity)
        {
            var setup = new Setup();
            setup.Parts.Add(0x0100AA01u);
            setup.DefaultScale.Add(Vector3.One);
            var authored = new Animation { Flags = AnimationFlags.PosFrames };
            for (int i = 0; i < 4; i++)
            {
                var partFrame = new AnimationFrame(1);
                partFrame.Frames.Add(new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
                authored.PartFrames.Add(partFrame);
                authored.PosFrames.Add(new Frame
                {
                    Origin = new Vector3(0.1f, 0f, 0f),
                    Orientation = Quaternion.Identity,
                });
            }

            var sequencer = new AnimationSequencer(
                setup,
                new MotionTable(),
                new SingleAnimationLoader(AnimationId, authored));
            Assert.True(sequencer.InitializeSetupDefaultAnimation(AnimationId));
            return new LiveEntityAnimationState
            {
                Entity = entity,
                Setup = setup,
                Animation = authored,
                LowFrame = 0,
                HighFrame = 3,
                Framerate = 30f,
                Simulation = new RuntimeRemoteAnimationState { Scale = 1f },
                PartTemplate = new[]
                {
                    new LiveAnimationPartTemplate(
                        0x0100AA01u,
                        SurfaceOverrides: null,
                        IsDrawable: true),
                },
                PartAvailability = new[] { true },
                Sequencer = sequencer,
            };
        }

        private static WorldSession.EntitySpawn Spawn(PhysicsStateFlags state)
        {
            var position = new CreateObject.ServerPosition(
                Cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
            var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
            var physics = new PhysicsSpawnData(
                RawState: (uint)state,
                Position: position,
                Movement: null,
                AnimationFrame: null,
                SetupTableId: 0x02000001u,
                MotionTableId: 0x09000001u,
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
                RemoteGuid,
                position,
                0x02000001u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "remote body advance fixture",
                null,
                null,
                0x09000001u,
                PhysicsState: (uint)state,
                InstanceSequence: 1,
                MovementSequence: 1,
                ServerControlSequence: 1,
                PositionSequence: 1,
                Physics: physics);
        }
    }

    private sealed class SingleAnimationLoader(uint id, Animation animation)
        : IAnimationLoader
    {
        public Animation? LoadAnimation(uint requested) =>
            requested == id ? animation : null;
    }

    private sealed class LoggingHookSink : IAnimationHookCaptureSink
    {
        private readonly Action<uint, AnimationSequencer> _log;

        public LoggingHookSink(Action<uint, AnimationSequencer> log)
        {
            _log = log;
            CaptureCallback = Capture;
        }

        public Action<uint, AnimationSequencer> CaptureCallback { get; }

        public void Capture(uint ownerLocalId, AnimationSequencer sequencer) =>
            _log(ownerLocalId, sequencer);
    }

    private sealed class LoggingRootPosePublisher(
        EntityEffectPoseRegistry poses,
        Action published) : IEntityRootPosePublisher
    {
        public void UpdateRoot(WorldEntity entity)
        {
            poses.UpdateRoot(entity);
            published();
        }
    }
}
