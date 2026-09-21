using System;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using AnimationFlags = DatReaderWriter.Enums.AnimationFlags;
using WireMotionCommand = DatReaderWriter.Enums.MotionCommand;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// When the character is given its own locomotion, when it is given it
/// again, and when it is not given it at all.
/// </summary>
/// <remarks>
/// <para>
/// None of this used to be written down anywhere, and all of it is load
/// bearing. Setting the locomotion up is retried on every step rather than
/// done once, because the cycles the character moves itself by are built from
/// content that need not be to hand the moment the body is; a client that
/// stopped retrying would leave a character that logged in ahead of its
/// content standing still for the rest of the session.
/// </para>
/// <para>
/// Setting it up again clears whatever the character was told to do
/// beforehand. That is deliberate and it is what the client with a window has
/// always done at the one moment it did this at all -- taking hold of a body
/// it has just been handed. It is done here on a change of the character's
/// cycles as well, because those outstanding instructions name the cycles
/// that have just been thrown away and no new cycle can ever finish them: a
/// character that kept them would stand still forever, which is the same
/// fault as having nowhere to report a finished cycle to.
/// </para>
/// </remarks>
public sealed class RuntimeLocalPlayerMotionArmingTests
{
    private const uint Player = 0x50000001u;
    private const uint SetupId = 0x02000001u;
    private static readonly Vector3 Start = new(96f, 96f, 50f);

    /// <summary>
    /// A character whose cycles are not to hand yet is not given any, and is
    /// asked again every step until they are. This is a session that reached
    /// the world before it finished reading its content.
    /// </summary>
    [Fact]
    public void TheCharacterIsAskedAgainUntilItsCyclesAreToHand()
    {
        using var world = new Fixture(content: null);

        Assert.False(world.Arming.EnsureArmed(world.Controller));
        Assert.False(world.Arming.IsArmed);
        Assert.False(world.Arming.EnsureArmed(world.Controller));

        world.BindContent(new TurningContent(SetupId, withCycles: true));

        Assert.True(world.Arming.EnsureArmed(world.Controller));
        Assert.True(world.Arming.IsArmed);
        Assert.NotNull(world.Controller.Motion.DefaultSink);
    }

    /// <summary>
    /// A character whose content describes no cycles at all has nothing to
    /// move itself by and is never given any, however many times it is asked.
    /// It stands where the server puts it, which is the honest answer.
    /// </summary>
    [Fact]
    public void ACharacterWithNoCyclesAtAllIsNeverGivenAny()
    {
        using var world = new Fixture(
            content: new TurningContent(SetupId, withCycles: false));

        for (int step = 0; step < 5; step++)
            Assert.False(world.Arming.EnsureArmed(world.Controller));

        Assert.False(world.Arming.IsArmed);
        Assert.Null(world.Controller.Motion.DefaultSink);
    }

    /// <summary>
    /// Once it is set up, asking again changes nothing: the same character
    /// with the same cycles keeps the locomotion it has, rather than being
    /// torn down and rebuilt on every step.
    /// </summary>
    [Fact]
    public void AskingAgainForTheSameCyclesLeavesThemAlone()
    {
        using var world = new Fixture(
            content: new TurningContent(SetupId, withCycles: true));
        Assert.True(world.Arming.EnsureArmed(world.Controller));
        object sink = world.Controller.Motion.DefaultSink!;

        for (int step = 0; step < 5; step++)
            Assert.True(world.Arming.EnsureArmed(world.Controller));

        Assert.Same(sink, world.Controller.Motion.DefaultSink);
    }

    /// <summary>
    /// Told to forget what it has -- which is what a client says when the
    /// character has just taken hold of a body it was handed -- the character
    /// is set up again from the beginning, and what it was told to do before
    /// is cleared.
    /// </summary>
    [Fact]
    public void ForgettingWhatIsSetUpSetsItUpAgainAndClearsWhatWasOutstanding()
    {
        using var world = new Fixture(
            content: new TurningContent(SetupId, withCycles: true));
        Assert.True(world.Arming.EnsureArmed(world.Controller));
        object sink = world.Controller.Motion.DefaultSink!;
        world.Controller.Motion.AddToQueue(0u, MotionCommand.RunForward, 0u);
        Assert.True(world.Controller.Motion.MotionsPending());

        world.Arming.Rearm();
        Assert.False(world.Arming.IsArmed);
        Assert.True(world.Arming.EnsureArmed(world.Controller));

        Assert.NotSame(sink, world.Controller.Motion.DefaultSink);
        Assert.False(world.Controller.Motion.MotionsPending());
    }

    /// <summary>
    /// The character's cycles being rebuilt sets its locomotion up again on
    /// its own, without anybody saying so, and clears what was outstanding
    /// against the cycles that have gone.
    /// </summary>
    [Fact]
    public void RebuildingTheCharactersCyclesSetsItUpAgainOnItsOwn()
    {
        using var world = new Fixture(
            content: new TurningContent(SetupId, withCycles: true));
        Assert.True(world.Arming.EnsureArmed(world.Controller));
        object sink = world.Controller.Motion.DefaultSink!;
        world.Controller.Motion.AddToQueue(0u, MotionCommand.RunForward, 0u);

        world.RebuildCycles();

        Assert.True(world.Arming.EnsureArmed(world.Controller));
        Assert.NotSame(sink, world.Controller.Motion.DefaultSink);
        Assert.False(world.Controller.Motion.MotionsPending());
    }

    /// <summary>
    /// A character the session no longer has -- logged out, or about to be
    /// handed a fresh body -- has nothing to set up, and the one that comes
    /// back is set up from the beginning with its own cycles.
    /// </summary>
    [Fact]
    public void ACharacterThatGoesAndComesBackIsSetUpFromTheBeginning()
    {
        using var world = new Fixture(
            content: new TurningContent(SetupId, withCycles: true));
        Assert.True(world.Arming.EnsureArmed(world.Controller));
        object sink = world.Controller.Motion.DefaultSink!;

        world.RetireTheCharacter();
        Assert.False(world.Arming.EnsureArmed(world.Controller));

        world.BringTheCharacterBack();

        Assert.True(world.Arming.EnsureArmed(world.Controller));
        Assert.NotSame(sink, world.Controller.Motion.DefaultSink);
    }

    /// <summary>
    /// A session with no character at all asks for nothing, and a caller with
    /// no body to set up is told so rather than throwing.
    /// </summary>
    [Fact]
    public void NoCharacterAndNoBodyAreBothAnswered()
    {
        using var world = new Fixture(
            content: new TurningContent(SetupId, withCycles: true));

        Assert.False(world.Arming.EnsureArmed(controller: null));

        world.ForgetWhoTheCharacterIs();
        Assert.False(world.Arming.EnsureArmed(world.Controller));
        Assert.False(world.Arming.IsArmed);
    }

    // -- the fixture -------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private readonly RuntimeEntityObjectLifetime _lifetime = new();
        private uint _player = Player;

        internal Fixture(IRuntimeMotionContentSource? content)
        {
            if (content is not null)
                _lifetime.Physics.BindMotionContentSource(content);
            _ = _lifetime.Entities.AddActive(Spawn());
            Controller = new PlayerMovementController(FlatGround());
            Controller.SeedPlacementForTest(Start, 0x0001, Start);
            Arming = new RuntimeLocalPlayerMotionArming(
                _lifetime,
                () => _player);
        }

        internal PlayerMovementController Controller { get; }

        internal RuntimeLocalPlayerMotionArming Arming { get; }

        internal void BindContent(IRuntimeMotionContentSource content) =>
            _lifetime.Physics.BindMotionContentSource(content);

        /// <summary>
        /// Builds the character's cycles afresh, the way a client does when
        /// the description it holds of the character is replaced.
        /// </summary>
        internal void RebuildCycles()
        {
            Assert.True(
                _lifetime.Entities.TryGetActive(
                    _player,
                    out RuntimeEntityRecord record));
            _lifetime.Physics.SetRemoteAnimation(
                record,
                _lifetime.Physics.MotionStates!.CreateFromSpawn(
                    record.Snapshot,
                    scale: 1f));
        }

        internal void RetireTheCharacter() =>
            Assert.True(_lifetime.Entities.RemoveActive(_player, out _));

        internal void BringTheCharacterBack() =>
            _ = _lifetime.Entities.AddActive(Spawn());

        internal void ForgetWhoTheCharacterIs() => _player = 0u;

        public void Dispose() => _lifetime.Dispose();

        private static PhysicsEngine FlatGround()
        {
            var engine = new PhysicsEngine();
            var heights = new byte[81];
            Array.Fill(heights, (byte)50);
            var table = new float[256];
            for (int i = 0; i < 256; i++)
                table[i] = i * 1f;
            engine.AddLandblock(
                0xA9B4FFFFu,
                new TerrainSurface(heights, table),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            return engine;
        }

        private static WorldSession.EntitySpawn Spawn()
        {
            const PhysicsStateFlags state = PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions;
            var position = new CreateObject.ServerPosition(
                0xA9B40001u,
                Start.X,
                Start.Y,
                Start.Z,
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
                Player,
                position,
                SetupId,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "arming fixture",
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
    /// One part layout and, when asked for, one table with a standing and a
    /// turning cycle. Asked without them it is a part layout and nothing
    /// else, which is a character with nothing to move itself by.
    /// </summary>
    private sealed class TurningContent : IRuntimeMotionContentSource
    {
        private const uint MotionTableId = 0x09000001u;
        private const uint StandingAnimation = 0x03000001u;
        private const uint TurningAnimation = 0x03000002u;
        private const uint NonCombat = 0x8000003Du;

        private readonly uint _setupId;
        private readonly bool _withCycles;
        private readonly MotionTable _table = new();
        private readonly Loader _loader = new();

        internal TurningContent(uint setupId, bool withCycles)
        {
            _setupId = setupId;
            _withCycles = withCycles;
            _loader.Add(StandingAnimation, Authored());
            _loader.Add(TurningAnimation, Authored());
            _table.DefaultStyle = (WireMotionCommand)NonCombat;
            _table.StyleDefaults[
                (WireMotionCommand)NonCombat] =
                (WireMotionCommand)MotionCommand.Ready;
            Cycle(MotionCommand.Ready, StandingAnimation);
            Cycle(MotionCommand.TurnRight, TurningAnimation);
            Cycle(MotionCommand.TurnLeft, TurningAnimation);
        }

        public IAnimationLoader AnimationLoader => _loader;

        public MotionTable? TryGetMotionTable(uint motionTableId) =>
            _withCycles && motionTableId == MotionTableId ? _table : null;

        public Setup? TryGetSetup(uint setupId)
        {
            if (setupId != _setupId)
                return null;
            var setup = new Setup();
            setup.Parts.Add(0x0100AA01u);
            setup.DefaultScale.Add(Vector3.One);
            if (_withCycles)
            {
                setup.DefaultMotionTable =
                    (QualifiedDataId<MotionTable>)MotionTableId;
            }
            return setup;
        }

        private void Cycle(uint command, uint animationId)
        {
            var data = new MotionData();
            data.Anims.Add(new AnimData
            {
                AnimId = (QualifiedDataId<Animation>)animationId,
                LowFrame = 0,
                HighFrame = -1,
                Framerate = 30f,
            });
            _table.Cycles[(int)((NonCombat << 16) | (command & 0xFFFFFFu))] =
                data;
        }

        private static Animation Authored()
        {
            var animation = new Animation { Flags = AnimationFlags.PosFrames };
            for (int frame = 0; frame < 4; frame++)
            {
                var partFrame = new AnimationFrame(1);
                partFrame.Frames.Add(new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
                animation.PartFrames.Add(partFrame);
                animation.PosFrames.Add(new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
            }
            return animation;
        }

        private sealed class Loader : IAnimationLoader
        {
            private readonly Dictionary<uint, Animation> _animations = new();

            internal void Add(uint id, Animation animation) =>
                _animations[id] = animation;

            public Animation? LoadAnimation(uint requested) =>
                _animations.TryGetValue(requested, out Animation? animation)
                    ? animation
                    : null;
        }
    }
}
