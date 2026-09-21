using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// A body's motion simulation state — the sequencer that plays its cycles, the
/// scale its travel is measured in, and the scratch frames one advance writes
/// into — has to have ONE home, and every reader has to see the same values in
/// it. These pin what the windowed client observes about that state, so that
/// moving where it is held cannot quietly change how a body moves or how big
/// it is.
/// </summary>
public sealed class MotionSimulationOwnershipTests
{
    private const uint Guid = 0x70000330u;
    private const uint CellId = 0x01010001u;

    [Fact]
    public void TheScaleAnAppearanceRebindWritesIsTheScaleEveryReaderSees()
    {
        Fixture fixture = Build(spawnScale: 1f);

        LiveEntityAppearanceBinding.RebindAnimation(
            fixture.Animation,
            fixture.Entity,
            fixture.Animation.Setup,
            2.5f,
            Array.Empty<LiveAnimationPartTemplate>(),
            Array.Empty<bool>());

        Assert.Equal(2.5f, fixture.Animation.Scale);
        Assert.Equal(
            2.5f,
            LiveEntityObjectScale.Resolve(
                fixture.Animation,
                fixture.Record,
                fixture.Entity));
    }

    [Fact]
    public void AnAnimatingBodysOwnScaleWinsOverTheCreationSnapshot()
    {
        // The creation snapshot says four; the animating body says one and a
        // half. Everything that measures the body has to agree with the body.
        Fixture fixture = Build(spawnScale: 4f);
        fixture.Animation.Scale = 1.5f;

        Assert.Equal(
            1.5f,
            LiveEntityObjectScale.Resolve(
                fixture.Animation,
                fixture.Record,
                fixture.Entity));
        Assert.Equal(
            1.5f,
            LiveEntityObjectScale.Resolve(fixture.Record, fixture.Entity));
    }

    [Fact]
    public void WithoutAnAnimatingBodyTheCreationSnapshotsScaleIsUsed()
    {
        Fixture fixture = Build(spawnScale: 4f);

        Assert.Equal(
            4f,
            LiveEntityObjectScale.Resolve(
                animation: null,
                fixture.Record,
                fixture.Entity));
    }

    [Fact]
    public void EachBodyKeepsItsOwnScratchFramesAcrossAdvances()
    {
        Fixture first = Build(spawnScale: 1f);
        LiveEntityAnimationState second = State(first.Entity, scale: 1f);

        // One body, one pair of scratch frames, handed back unchanged every
        // time: an advance writes this quantum's travel into them and the
        // physics step reads it straight back.
        Assert.Same(
            first.Animation.RootMotionScratch,
            first.Animation.RootMotionScratch);
        Assert.Same(
            first.Animation.RootMotionDeltaScratch,
            first.Animation.RootMotionDeltaScratch);
        Assert.NotSame(
            first.Animation.RootMotionScratch,
            second.RootMotionScratch);
        Assert.NotSame(
            first.Animation.RootMotionDeltaScratch,
            second.RootMotionDeltaScratch);

        first.Animation.RootMotionScratch.Origin = new Vector3(0f, 0.1f, 0f);
        first.Animation.RootMotionDeltaScratch.Origin = new Vector3(0f, 0.2f, 0f);

        Assert.Equal(
            new Vector3(0f, 0.1f, 0f),
            first.Animation.RootMotionScratch.Origin);
        Assert.Equal(
            new Vector3(0f, 0.2f, 0f),
            first.Animation.RootMotionDeltaScratch.Origin);
        Assert.Equal(Vector3.Zero, second.RootMotionScratch.Origin);
    }

    [Fact]
    public void TheSequencerABodyIsGivenIsTheOneItHandsBackAndTheMotionItReports()
    {
        AnimationSequencer sequencer = WalkingSequencer();
        sequencer.InitializeState();
        sequencer.SetCycle(NonCombat, Walk);
        Assert.NotEqual(0u, sequencer.CurrentMotion);
        Fixture fixture = Build(spawnScale: 1f, sequencer: sequencer);

        Assert.Same(sequencer, fixture.Animation.Sequencer);
        Assert.Equal(
            sequencer.CurrentMotion,
            ((ILiveEntityAnimationRuntime)fixture.Animation).CurrentMotion);
    }

    [Fact]
    public void ABodyWithNoSequencerReportsNoMotion()
    {
        Fixture fixture = Build(spawnScale: 1f);

        Assert.Null(fixture.Animation.Sequencer);
        Assert.Equal(
            0u,
            ((ILiveEntityAnimationRuntime)fixture.Animation).CurrentMotion);
    }

    [Fact]
    public void ASequencerExchangedAfterTheBodyExistsIsTheOneReadBack()
    {
        Fixture fixture = Build(spawnScale: 1f);
        var replacement = new AnimationSequencer(
            fixture.Animation.Setup,
            new MotionTable(),
            new NullLoader());

        fixture.Animation.Sequencer = replacement;
        Assert.Same(replacement, fixture.Animation.Sequencer);

        fixture.Animation.Sequencer = null;
        Assert.Null(fixture.Animation.Sequencer);
    }

    [Fact]
    public void TheSharedOwnerHoldsTheSameSimulationStateTheDrawnBodyDoes()
    {
        Fixture fixture = Build(spawnScale: 1f);

        Assert.Same(
            fixture.Animation.Simulation,
            fixture.Runtime.Physics.EntityRemoteAnimation(Guid));
    }

    [Fact]
    public void ABodyBuiltAfreshReplacesTheStateTheSharedOwnerHeld()
    {
        Fixture fixture = Build(spawnScale: 1f);
        RuntimeRemoteAnimationState first = fixture.Animation.Simulation;
        LiveEntityAnimationState replacement = State(fixture.Entity, scale: 1f);

        fixture.Runtime.SetAnimationRuntime(Guid, replacement);

        // A body built afresh starts its cycles afresh, so the previous state
        // is let go rather than carried over.
        Assert.NotSame(first, replacement.Simulation);
        Assert.Same(
            replacement.Simulation,
            fixture.Runtime.Physics.EntityRemoteAnimation(Guid));
    }

    [Fact]
    public void LettingTheDrawnBodyGoLeavesTheSharedOwnerHoldingNothing()
    {
        Fixture fixture = Build(spawnScale: 1f);

        Assert.True(fixture.Runtime.ClearAnimationRuntime(Guid));

        Assert.Null(fixture.Runtime.Physics.EntityRemoteAnimation(Guid));
    }

    // -- fixture -------------------------------------------------------------

    private sealed record Fixture(
        LiveEntityRuntime Runtime,
        LiveEntityRecord Record,
        WorldEntity Entity,
        LiveEntityAnimationState Animation);

    private static Fixture Build(
        float spawnScale,
        AnimationSequencer? sequencer = null)
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            CellId & 0xFFFF0000u,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        LiveEntityRuntime runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(
            Spawn(spawnScale),
            Entity);
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        LiveEntityAnimationState animation = State(entity, 1f, sequencer);
        runtime.SetAnimationRuntime(Guid, animation);
        return new Fixture(runtime, record, entity, animation);
    }

    private static LiveEntityAnimationState State(
        WorldEntity entity,
        float scale,
        AnimationSequencer? sequencer = null) =>
        new()
        {
            Entity = entity,
            Setup = new Setup(),
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Simulation = new RuntimeRemoteAnimationState { Scale = scale },
            PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
            PartAvailability = Array.Empty<bool>(),
            Sequencer = sequencer,
        };

    private static WorldSession.EntitySpawn Spawn(float scale) =>
        new WorldSession.EntitySpawn(
            Guid,
            new CreateObject.ServerPosition(CellId, 10f, 10f, 5f, 1f, 0f, 0f, 0f),
            SetupTableId: 0x02000042u,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: scale,
            Name: "motion simulation subject",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            InstanceSequence: 1).WithConsistentPhysics();

    private static WorldEntity Entity(uint id) => new()
    {
        Id = id,
        ServerGuid = Guid,
        SourceGfxObjOrSetupId = 0x02000042u,
        Position = new Vector3(10f, 10f, 5f),
        Rotation = Quaternion.Identity,
        MeshRefs = [new MeshRef(0x01000001u, Matrix4x4.Identity)],
        ParentCellId = CellId,
    };

    // -- a body that can actually play a cycle --------------------------------

    private const uint NonCombat = 0x8000003Du;
    private const uint Ready = 0x41000003u;
    private const uint Walk = 0x45000005u;

    private static AnimationSequencer WalkingSequencer()
    {
        var setup = new Setup();
        setup.Parts.Add(0x01000000u);
        setup.DefaultScale.Add(Vector3.One);

        var loader = new RegisteredLoader();
        loader.Register(0x300u, FlatAnimation(4));
        loader.Register(0x301u, FlatAnimation(6));

        var table = new MotionTable
        {
            DefaultStyle = (DatReaderWriter.Enums.MotionCommand)NonCombat,
        };
        table.StyleDefaults[(DatReaderWriter.Enums.MotionCommand)NonCombat] =
            (DatReaderWriter.Enums.MotionCommand)Ready;
        table.Cycles[(int)((NonCombat << 16) | (Ready & 0xFFFFFFu))] =
            Cycle(0x300u);
        table.Cycles[(int)((NonCombat << 16) | (Walk & 0xFFFFFFu))] =
            Cycle(0x301u);
        return new AnimationSequencer(setup, table, loader);
    }

    private static DatReaderWriter.Types.MotionData Cycle(uint animationId)
    {
        var data = new DatReaderWriter.Types.MotionData();
        DatReaderWriter.Types.QualifiedDataId<Animation> id = animationId;
        data.Anims.Add(new DatReaderWriter.Types.AnimData
        {
            AnimId = id,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = 30f,
        });
        return data;
    }

    private static Animation FlatAnimation(int frames)
    {
        var animation = new Animation();
        for (int f = 0; f < frames; f++)
        {
            var frame = new DatReaderWriter.Types.AnimationFrame(1);
            frame.Frames.Add(new DatReaderWriter.Types.Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            });
            animation.PartFrames.Add(frame);
        }
        return animation;
    }

    private sealed class RegisteredLoader : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _animations = new();
        public void Register(uint id, Animation animation) =>
            _animations[id] = animation;
        public Animation? LoadAnimation(uint id) =>
            _animations.TryGetValue(id, out Animation? found) ? found : null;
    }

    private sealed class NullLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
