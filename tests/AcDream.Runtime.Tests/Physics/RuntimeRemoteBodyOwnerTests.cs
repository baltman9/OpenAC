using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Physics;

/// <summary>
/// A body carried forward by the shared owner moves by the same amount whether
/// or not anything is drawing it. These tests run the same creature twice —
/// once with a host that builds its part poses every step, once with a host
/// that builds none — and compare where it ended up.
/// </summary>
public sealed class RuntimeRemoteBodyOwnerTests
{
    /// <summary>
    /// Half a second of a cycle authored at a tenth of a metre per frame and
    /// thirty frames a second is one and a half metres, in three steps of two
    /// tenths, two tenths and one tenth of a second.
    /// </summary>
    [Fact]
    public void ABodyTravelsTheSameDistanceWithAndWithoutPartPoses()
    {
        using var drawn = Fixture.Create();
        using var undrawn = Fixture.Create();

        RuntimeRemoteBodyAdvance drawnAdvance = drawn.Advance(0.5f, buildPoses: true);
        RuntimeRemoteBodyAdvance undrawnAdvance = undrawn.Advance(0.5f, buildPoses: false);

        Assert.True(drawnAdvance.Advanced);
        Assert.True(undrawnAdvance.Advanced);
        Assert.Equal(3, drawnAdvance.Steps);
        Assert.Equal(3, undrawnAdvance.Steps);
        Assert.Equal(
            drawn.Remote.Body.Position,
            undrawn.Remote.Body.Position);
        Assert.Equal(
            drawn.Remote.Body.Orientation,
            undrawn.Remote.Body.Orientation);
        Assert.Equal(drawn.Remote.CellId, undrawn.Remote.CellId);
        Assert.Equal(
            drawn.Remote.Body.OnWalkable,
            undrawn.Remote.Body.OnWalkable);
        Assert.Equal(
            drawn.Remote.MaxRootMotionSpeedSinceLastUP,
            undrawn.Remote.MaxRootMotionSpeedSinceLastUP);

        // The travel is the authored cycle's own, not a number this test made
        // up: a tenth of a metre a frame at thirty frames a second is three
        // metres a second, and half a second of it is one and a half metres.
        Assert.Equal(
            Fixture.StartX + 1.5f,
            drawn.Remote.Body.Position.X,
            3);
        Assert.Equal(3f, drawn.Remote.MaxRootMotionSpeedSinceLastUP, 3);
    }

    /// <summary>
    /// A host that draws the body gets one pose per part every step; a host
    /// that does not gets none, and pays nothing for them.
    /// </summary>
    [Fact]
    public void OnlyAHostThatDrawsTheBodyIsHandedItsPartPoses()
    {
        using var drawn = Fixture.Create();
        using var undrawn = Fixture.Create();

        RuntimeRemoteBodyAdvance drawnAdvance = drawn.Advance(0.5f, buildPoses: true);
        RuntimeRemoteBodyAdvance undrawnAdvance = undrawn.Advance(0.5f, buildPoses: false);

        Assert.Equal(3, drawn.PoseBuilds);
        Assert.Equal(0, undrawn.PoseBuilds);
        Assert.NotNull(drawnAdvance.PartPoses);
        Assert.Single(drawnAdvance.PartPoses!);
        Assert.Null(undrawnAdvance.PartPoses);
    }

    /// <summary>
    /// The same total time delivered one frame at a time ends within one short
    /// authored frame's travel of where one long frame ends, because the time is carried
    /// on the body's own clock rather than spent as it arrives. At three
    /// metres a second one short step is ten centimetres, and that is the whole
    /// disagreement a host's frame length can cause.
    /// </summary>
    [Fact]
    public void ManySmallFramesLandWithinOneAuthoredFrameOfOneLongFrame()
    {
        using var oneFrame = Fixture.Create();
        using var manyFrames = Fixture.Create();

        oneFrame.Advance(0.5f, buildPoses: false);
        int steps = 0;
        for (int i = 0; i < 50; i++)
            steps += manyFrames.Advance(0.01f, buildPoses: false).Steps;

        Assert.True(steps > 3);
        Assert.InRange(
            manyFrames.UnspentSeconds,
            0.0,
            PhysicsBody.MinQuantum + 0.001);

        // The whole disagreement is one authored frame of travel. Travel is
        // applied a whole authored frame at a time, and a short step does not
        // always reach the next frame; at a tenth of a metre a frame that is
        // ten centimetres, whatever frame length a host runs at.
        const float OneAuthoredFrame = 0.1f;
        Assert.InRange(
            oneFrame.Remote.Body.Position.X - manyFrames.Remote.Body.Position.X,
            0f,
            OneAuthoredFrame + 0.001f);
    }

    /// <summary>
    /// A body whose clock the host says is not running is not advanced at all,
    /// and neither is one further off than the bubble the client keeps alive.
    /// </summary>
    [Fact]
    public void ABodyThatIsNotRunningIsNotAdvanced()
    {
        using var suspended = Fixture.Create();
        using var distant = Fixture.Create();

        RuntimeRemoteBodyAdvance suspendedAdvance =
            suspended.Advance(0.5f, buildPoses: false, rootClockAdvances: false);
        RuntimeRemoteBodyAdvance distantAdvance = distant.Advance(
            0.5f,
            buildPoses: false,
            playerPosition: new Vector3(Fixture.StartX + 500f, Fixture.StartY, 0f));

        Assert.False(suspendedAdvance.Advanced);
        Assert.False(distantAdvance.Advanced);
        Assert.Equal(Fixture.StartX, suspended.Remote.Body.Position.X, 4);
        Assert.Equal(Fixture.StartX, distant.Remote.Body.Position.X, 4);
    }

    private sealed class Fixture : IDisposable
    {
        internal const float StartX = 96f;
        internal const float StartY = 96f;
        private const uint LandblockId = 0x0101FFFFu;
        private const uint AnimationId = 0x0300AA01u;
        private const uint Guid = 0x70000001u;

        private readonly RuntimeEntityObjectLifetime _lifetime;
        private readonly RuntimeEntityRecord _record;
        private readonly RuntimeRemoteBodyOwner _owner;
        private readonly RuntimeRemoteAnimationState _animation;

        private Fixture(
            RuntimeEntityObjectLifetime lifetime,
            RuntimeEntityRecord record,
            RemoteMotion remote,
            RuntimeRemoteAnimationState animation)
        {
            _lifetime = lifetime;
            _record = record;
            Remote = remote;
            _animation = animation;
            _owner = new RuntimeRemoteBodyOwner(lifetime.Physics);
        }

        internal RemoteMotion Remote { get; }

        internal int PoseBuilds { get; private set; }

        /// <summary>
        /// Time the body has accumulated but not yet spent, because it is not
        /// yet worth a step.
        /// </summary>
        internal double UnspentSeconds => _record.ObjectClock.PendingSeconds;

        internal static Fixture Create()
        {
            var lifetime = new RuntimeEntityObjectLifetime();
            var surface = new TerrainSurface(new byte[81], new float[256]);
            lifetime.Physics.Engine.AddLandblock(
                LandblockId,
                surface,
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);

            RuntimeEntityRecord record = lifetime.Entities.AddActive(Spawn());
            var body = new PhysicsBody
            {
                State = PhysicsStateFlags.Gravity
                    | PhysicsStateFlags.ReportCollisions
                    | PhysicsStateFlags.EdgeSlide,
                InWorld = true,
            };
            var remote = new RemoteMotion(body);
            lifetime.Entities.SetPhysicsBody(record, body);
            lifetime.Entities.SetRemoteMotion(record, remote);
            lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
            record.HasPartArray = true;

            body.Position = new Vector3(
                StartX,
                StartY,
                surface.SampleZ(StartX, StartY));
            body.Orientation = Quaternion.Identity;
            body.TransientState = TransientStateFlags.Active
                | TransientStateFlags.Contact
                | TransientStateFlags.OnWalkable;
            remote.CellId = TerrainSurface.ComputeOutdoorCellId(
                LandblockId,
                StartX,
                StartY);
            remote.LastServerPos = body.Position;
            remote.LastServerPosTime = 1.0;

            RuntimeRemoteAnimationState animation = BuildAnimation();
            lifetime.Physics.SetRemoteAnimation(record, animation);
            return new Fixture(lifetime, record, remote, animation);
        }

        internal RuntimeRemoteBodyAdvance Advance(
            float elapsedSeconds,
            bool buildPoses,
            bool rootClockAdvances = true,
            Vector3? playerPosition = null)
        {
            var facts = new RuntimeRemoteBodyFacts(
                elapsedSeconds,
                Remote.Body.Position,
                playerPosition ?? Remote.Body.Position,
                rootClockAdvances,
                ObjectScale: 1f,
                _record.ObjectClockEpoch,
                LiveCenterX: 1,
                LiveCenterY: 1);
            var presentation = buildPoses
                ? new RuntimeRemoteBodyPresentation(
                    BuildPartPoses: (sequencer, dt, rootFrame) =>
                    {
                        PoseBuilds++;
                        return sequencer.Advance(dt, rootFrame);
                    })
                : default;
            return _owner.Advance(
                _record,
                Remote,
                _animation,
                facts,
                presentation);
        }

        public void Dispose() => _lifetime.Dispose();

        private static RuntimeRemoteAnimationState BuildAnimation()
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
            return new RuntimeRemoteAnimationState
            {
                Scale = 1f,
                Sequencer = sequencer,
            };
        }

        private static WorldSession.EntitySpawn Spawn()
        {
            const PhysicsStateFlags state = PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.EdgeSlide;
            var position = new CreateObject.ServerPosition(
                TerrainSurface.ComputeOutdoorCellId(LandblockId, StartX, StartY),
                StartX,
                StartY,
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
                Guid,
                position,
                0x02000001u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "remote body owner fixture",
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
}
