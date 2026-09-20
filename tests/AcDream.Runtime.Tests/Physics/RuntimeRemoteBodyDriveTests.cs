using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Physics;

/// <summary>
/// What the drive does with a crowd of other creatures' bodies: how far each
/// one travels per frame against the cycle it was authored with, what it
/// costs, and that a warm frame allocates nothing.
/// </summary>
/// <remarks>
/// The distances here are not numbers anyone chose. The cycle is authored at
/// a tenth of a metre a frame and thirty frames a second, which is three
/// metres a second; a body spends elapsed time in steps of at most a fifth of
/// a second and carries anything below a thirtieth, so how far it gets in a
/// given frame follows from those two facts alone.
/// </remarks>
public sealed class RuntimeRemoteBodyDriveTests
{
    private const uint Landblock = 0x0101FFFFu;
    private const uint Player = 0x50000001u;
    private const float StartX = 96f;
    private const float StartY = 96f;

    /// <summary>A tenth of a metre a frame at thirty frames a second.</summary>
    private const float AuthoredMetresPerFrame = 0.1f;

    private const float AuthoredFramesPerSecond = 30f;

    /// <summary>
    /// Half a second of the authored cycle is one and a half metres, and it is
    /// spent in three steps: a fifth of a second, a fifth, and the tenth that
    /// is left. The whole of the answer is the authored cycle and the two
    /// bounds on a step.
    /// </summary>
    [Fact]
    public void ABodyTravelsTheDistanceItsAuthoredCycleAsksForInBoundedSteps()
    {
        using var world = Crowd.Create(bodies: 1, withGround: true);

        world.Tick(0.5f);

        const float expected = AuthoredMetresPerFrame
            * AuthoredFramesPerSecond
            * 0.5f;
        Assert.Equal(1, world.LastAdvancedCount);
        Assert.Equal(StartX + expected, world.Position(0).X, 3);
        Assert.Equal(StartY, world.Position(0).Y, 3);
    }

    /// <summary>
    /// The same total time handed over one short frame at a time ends within
    /// one authored frame of where one long frame ends: the time is carried on
    /// each body's own clock rather than spent as it arrives, and travel is
    /// applied a whole authored frame at a time.
    /// </summary>
    [Fact]
    public void ManyShortFramesEndWithinOneAuthoredFrameOfOneLongFrame()
    {
        using var oneFrame = Crowd.Create(bodies: 1, withGround: true);
        using var manyFrames = Crowd.Create(bodies: 1, withGround: true);

        oneFrame.Tick(0.9f);
        for (int frame = 0; frame < 60; frame++)
            manyFrames.Tick(0.015f);

        Assert.InRange(
            oneFrame.Position(0).X - manyFrames.Position(0).X,
            0f,
            AuthoredMetresPerFrame + 0.001f);
    }

    /// <summary>
    /// A body further off than the bubble this client keeps alive is not
    /// carried at all, however much time passes.
    /// </summary>
    [Fact]
    public void ABodyBeyondTheLiveBubbleIsNotCarried()
    {
        using var world = Crowd.Create(bodies: 1, withGround: true);
        world.MovePlayerTo(new Vector3(StartX + 500f, StartY, 0f));

        world.Tick(0.5f);

        Assert.Equal(0, world.LastAdvancedCount);
        Assert.Equal(StartX, world.Position(0).X, 4);
    }

    /// <summary>
    /// A crowd of bodies is carried once a frame with nothing allocated: the
    /// record list is reused, the facts are a value, and a client with nothing
    /// to present hands over no callbacks. A frame that allocates per body is
    /// a frame that collects, and a collection in the middle of a bot's tick
    /// is the difference between a steady walk and a stutter.
    /// </summary>
    /// <remarks>
    /// The world here has no collision loaded, so the sweep is skipped and
    /// what is measured is the loop and the owner alone. The sweep itself is
    /// the code the character's own body already runs every frame and is
    /// budgeted separately below.
    ///
    /// Mutation check (2026-09-20): building the body's stale-cycle callback
    /// per step again -- an object per body per frame -- turned this red at
    /// 648 kB over thirty frames. Reverting turned it green.
    /// </remarks>
    [Fact]
    public void TheAdvanceLoopItselfAllocatesNothing()
    {
        using var world = Crowd.Create(bodies: CrowdSize, withGround: false);

        // Warm: the first frames build each body's motion state and grow the
        // record list to its full size.
        for (int frame = 0; frame < 10; frame++)
            world.Tick(SteppingFrameSeconds);
        Assert.Equal(CrowdSize, world.LastAdvancedCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 30; frame++)
            world.Tick(SteppingFrameSeconds);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(CrowdSize, world.LastAdvancedCount);
        Assert.True(
            allocated == 0L,
            $"Carrying {CrowdSize} bodies for 30 frames allocated "
            + $"{allocated} bytes; a warm frame must allocate nothing.");
    }

    /// <summary>
    /// Carrying the same crowd over ground that is really there costs the
    /// sweep as well, and the sweep is not free. The budget is per body per
    /// frame so that it says something whatever the crowd size, and the
    /// measured figure is in the message so a run reports what it really was.
    /// </summary>
    [Fact]
    public void CarryingACrowdOverRealGroundStaysWithinItsAllocationBudget()
    {
        const long budgetBytesPerBodyPerFrame = 64L;
        const int frames = 30;
        using var world = Crowd.Create(bodies: CrowdSize, withGround: true);
        for (int frame = 0; frame < 10; frame++)
            world.Tick(SteppingFrameSeconds);
        Assert.Equal(CrowdSize, world.LastAdvancedCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < frames; frame++)
            world.Tick(SteppingFrameSeconds);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        long perBodyPerFrame = allocated / (frames * CrowdSize);
        Assert.True(
            perBodyPerFrame <= budgetBytesPerBodyPerFrame,
            $"Carrying a body over real ground allocated {perBodyPerFrame} "
            + $"bytes a frame, over the {budgetBytesPerBodyPerFrame}-byte "
            + "budget.");
    }

    /// <summary>
    /// What a frame of a crowd costs, measured rather than assumed. The bound
    /// is deliberately loose -- it is there to catch an order-of-magnitude
    /// regression, not to pin a machine's speed -- and the measured figure is
    /// reported so a run says what it really cost.
    /// </summary>
    [Fact]
    public void AFrameOverACrowdCostsWellUnderTheFrameItself()
    {
        using var world = Crowd.Create(bodies: CrowdSize, withGround: true);
        for (int frame = 0; frame < 20; frame++)
            world.Tick(TickSeconds);

        const int frames = 200;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; frame < frames; frame++)
            world.Tick(TickSeconds);
        watch.Stop();

        double perFrameMilliseconds =
            watch.Elapsed.TotalMilliseconds / frames;
        Assert.True(
            world.Position(CrowdSize - 1).X > StartX,
            "No body moved, so this measured nothing.");
        Assert.True(
            perFrameMilliseconds < TickSeconds * 1000d,
            $"Carrying {CrowdSize} bodies cost "
            + $"{perFrameMilliseconds:0.000} ms a frame, which does not fit "
            + $"in the {TickSeconds * 1000d:0} ms frame it runs in.");
    }

    /// <summary>
    /// Every body the drive carries is given somewhere to report a finished
    /// cycle to, and it is that body's own motion state.
    /// </summary>
    /// <remarks>
    /// A cycle a body has been told to play stays outstanding until something
    /// reports it finished, and the body's next movement is refused while one
    /// is. A client that presents the body has whatever presents it report
    /// that; a client that presents nothing has only the body, so the body
    /// reports to itself. Without it a creature ordered to walk somewhere
    /// stands where it is for the rest of the session -- the same fault the
    /// character itself had.
    /// </remarks>
    [Fact]
    public void EveryCarriedBodyIsGivenSomewhereToReportAFinishedCycle()
    {
        using var world = Crowd.Create(bodies: 3, withGround: true);
        Assert.Null(world.Sequencer(0));

        world.Tick(0.5f);

        for (int index = 0; index < 3; index++)
        {
            Assert.NotNull(world.Sequencer(index));
            Assert.NotNull(world.Sequencer(index)!.MotionDoneTarget);
            // And it is this body's own motion state, not another body's:
            // one report landing on the wrong creature would let one walk
            // through while every other stayed stopped.
            Assert.Same(
                world.Body(index).Motion,
                world.Sequencer(index)!.MotionDoneTarget!.Target);
        }
    }

    /// <summary>The crowd a bot is expected to stand in.</summary>
    private const int CrowdSize = 150;

    /// <summary>The frame the windowless client runs at.</summary>
    private const float TickSeconds = 0.015f;

    /// <summary>
    /// A frame long enough that every body is worth a step, so a measurement
    /// over it covers the whole crowd rather than whichever bodies happened
    /// to have carried enough time over from earlier frames.
    /// </summary>
    private const float SteppingFrameSeconds = 0.05f;

    /// <summary>
    /// A world with a character and a crowd of other creatures, each with a
    /// body, each playing the same authored cycle, carried by the real drive.
    /// </summary>
    private sealed class Crowd : IDisposable
    {
        private const uint AnimationId = 0x0300AA01u;
        private const uint SetupId = 0x02000001u;
        private const uint FirstCreature = 0x70000001u;

        private readonly RuntimeEntityObjectLifetime _lifetime;
        private readonly RuntimeRemoteBodyDrive _drive;
        private readonly List<RemoteMotion> _bodies = [];
        private Vector3 _playerPosition;

        private Crowd(
            RuntimeEntityObjectLifetime lifetime,
            Vector3 playerPosition)
        {
            _lifetime = lifetime;
            _playerPosition = playerPosition;
            _drive = new RuntimeRemoteBodyDrive(
                lifetime,
                static () => Player,
                () => _playerPosition);
        }

        internal int LastAdvancedCount => _drive.LastAdvancedCount;

        internal static Crowd Create(int bodies, bool withGround)
        {
            var lifetime = new RuntimeEntityObjectLifetime();
            var surface = new TerrainSurface(new byte[81], new float[256]);
            if (withGround)
            {
                lifetime.Physics.Engine.AddLandblock(
                    Landblock,
                    surface,
                    Array.Empty<CellSurface>(),
                    Array.Empty<PortalPlane>(),
                    worldOffsetX: 0f,
                    worldOffsetY: 0f);
            }
            lifetime.Physics.BindMotionContentSource(
                new OneCycleContent(SetupId, AnimationId));

            var crowd = new Crowd(
                lifetime,
                new Vector3(StartX, StartY, surface.SampleZ(StartX, StartY)));
            for (int index = 0; index < bodies; index++)
                crowd.AddCreature(index, surface);
            return crowd;
        }

        internal void MovePlayerTo(Vector3 position) =>
            _playerPosition = position;

        internal void Tick(float elapsedSeconds) =>
            _drive.Tick(elapsedSeconds);

        internal Vector3 Position(int index) => _bodies[index].Body.Position;

        /// <summary>The body's own motion state, as the drive left it.</summary>
        internal RemoteMotion Body(int index) => _bodies[index];

        /// <summary>The cycle the body is playing, once it has one.</summary>
        internal AcDream.Core.Physics.AnimationSequencer? Sequencer(int index)
            => _lifetime.Physics
                .EntityRemoteAnimation(FirstCreature + (uint)index)
                ?.Sequencer;

        public void Dispose() => _lifetime.Dispose();

        /// <summary>
        /// One creature, standing a little apart from the others so that a
        /// crowd is a crowd rather than one body many times over, with the
        /// body and the bindings the shared arming gives it.
        /// </summary>
        private void AddCreature(int index, TerrainSurface surface)
        {
            float y = StartY + (index * 0.5f);
            RuntimeEntityRecord record =
                _lifetime.Entities.AddActive(Spawn(index, y));
            var body = new PhysicsBody
            {
                State = PhysicsStateFlags.Gravity
                    | PhysicsStateFlags.ReportCollisions
                    | PhysicsStateFlags.EdgeSlide,
                InWorld = true,
                Orientation = Quaternion.Identity,
            };
            body.Position = new Vector3(
                StartX,
                y,
                surface.SampleZ(StartX, y));
            body.TransientState = TransientStateFlags.Active
                | TransientStateFlags.Contact
                | TransientStateFlags.OnWalkable;
            _lifetime.Entities.SetPhysicsBody(record, body);
            var remote = new RemoteMotion(body);
            _lifetime.Entities.SetRemoteMotion(record, remote);
            _lifetime.Physics.AcknowledgeSpatialProjection(
                record,
                spatial: true);
            record.HasPartArray = true;
            remote.CellId = TerrainSurface.ComputeOutdoorCellId(
                Landblock,
                StartX,
                y);
            remote.LastServerPos = body.Position;
            remote.LastServerPosTime = 1.0;
            _bodies.Add(remote);
        }

        private static WorldSession.EntitySpawn Spawn(int index, float y)
        {
            const PhysicsStateFlags state = PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.EdgeSlide;
            var position = new CreateObject.ServerPosition(
                TerrainSurface.ComputeOutdoorCellId(Landblock, StartX, y),
                StartX,
                y,
                0f,
                1f,
                0f,
                0f,
                0f);
            var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
            var physics = new PhysicsSpawnData(
                RawState: (uint)state,
                Position: position,
                Movement: null,
                AnimationFrame: null,
                SetupTableId: SetupId,
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
                FirstCreature + (uint)index,
                position,
                SetupId,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "crowd fixture",
                null,
                null,
                null,
                PhysicsState: (uint)state,
                InstanceSequence: 1,
                MovementSequence: 1,
                ServerControlSequence: 1,
                PositionSequence: 1,
                Physics: physics);
        }
    }

    /// <summary>
    /// Animation content with one part layout and one cycle: a walk authored
    /// at a tenth of a metre a frame, thirty frames a second.
    /// </summary>
    private sealed class OneCycleContent : IRuntimeMotionContentSource
    {
        private readonly uint _setupId;
        private readonly uint _animationId;
        private readonly Animation _authored;

        internal OneCycleContent(uint setupId, uint animationId)
        {
            _setupId = setupId;
            _animationId = animationId;
            _authored = new Animation { Flags = AnimationFlags.PosFrames };
            for (int frame = 0; frame < 4; frame++)
            {
                var partFrame = new AnimationFrame(1);
                partFrame.Frames.Add(new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
                _authored.PartFrames.Add(partFrame);
                _authored.PosFrames.Add(new Frame
                {
                    Origin = new Vector3(AuthoredMetresPerFrame, 0f, 0f),
                    Orientation = Quaternion.Identity,
                });
            }
            AnimationLoader = new SingleAnimation(animationId, _authored);
        }

        public IAnimationLoader AnimationLoader { get; }

        public MotionTable? TryGetMotionTable(uint motionTableId) => null;

        public Setup? TryGetSetup(uint setupId)
        {
            if (setupId != _setupId)
                return null;
            var setup = new Setup();
            setup.Parts.Add(0x0100AA01u);
            setup.DefaultScale.Add(Vector3.One);
            setup.DefaultAnimation =
                (QualifiedDataId<Animation>)_animationId;
            return setup;
        }

        private sealed class SingleAnimation(uint id, Animation animation)
            : IAnimationLoader
        {
            public Animation? LoadAnimation(uint requested) =>
                requested == id ? animation : null;
        }
    }
}
