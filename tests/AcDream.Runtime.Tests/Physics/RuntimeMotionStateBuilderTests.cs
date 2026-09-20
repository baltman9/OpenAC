using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Physics;

/// <summary>
/// A body's motion simulation state is now built in one place, from whatever
/// animation content the host bound. These pin that the state it builds is the
/// same state the windowed host used to build for itself, cycle for cycle, and
/// that a host with no content for an object gets a body that plays nothing
/// rather than a body that throws.
/// </summary>
public sealed class RuntimeMotionStateBuilderTests
{
    private const uint TableId = 0x09000001u;
    private const uint SetupId = 0x02000001u;
    private const uint NonCombat = 0x8000003Du;
    private const uint Ready = 0x41000003u;
    private const uint Walk = 0x45000005u;

    [Fact]
    public void AStateBuiltFromATableMatchesWhatTheSpawnDescriptionAsksFor()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x003D,
            ForwardCommand: 0x0005);

        RuntimeRemoteAnimationState state = builder.CreateFromMotionTable(
            setup, TableId, scale: 2.5f, wire);

        // The same sequencer the spawn description alone would have produced.
        AnimationSequencer expected = SpawnMotionInitializer.Create(
            setup, table, loader, wire);
        Assert.NotNull(state.Sequencer);
        Assert.Equal(expected.CurrentStyle, state.Sequencer!.CurrentStyle);
        Assert.Equal(expected.CurrentMotion, state.Sequencer.CurrentMotion);
        Assert.Equal(expected.HasCurrentNode, state.Sequencer.HasCurrentNode);
        Assert.Equal(2.5f, state.Scale);
    }

    [Fact]
    public void WithNoWireStateATableDefaultIsUsedJustAsBefore()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));

        RuntimeRemoteAnimationState state = builder.CreateFromMotionTable(
            setup, TableId, scale: 1f, wireState: null);

        AnimationSequencer expected = SpawnMotionInitializer.Create(
            setup, table, loader, null);
        Assert.NotNull(state.Sequencer);
        Assert.Equal(expected.CurrentStyle, state.Sequencer!.CurrentStyle);
        Assert.Equal(expected.CurrentMotion, state.Sequencer.CurrentMotion);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x09009999u)]
    public void AnIdThisHostHasNoTableForGivesABodyThatPlaysNothing(uint id)
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));

        RuntimeRemoteAnimationState state = builder.CreateFromMotionTable(
            setup, id, scale: 3f, wireState: null);

        Assert.Null(state.Sequencer);
        Assert.Equal(3f, state.Scale);
    }

    [Fact]
    public void ABodyWithNoTableAtAllStillKeepsItsScaleAndScratchFrames()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));

        RuntimeRemoteAnimationState state =
            builder.CreateWithoutSequencer(scale: 0.75f);

        Assert.Null(state.Sequencer);
        Assert.Equal(0.75f, state.Scale);
        Assert.NotNull(state.RootMotionScratch);
        Assert.NotNull(state.RootMotionDeltaScratch);
    }

    [Fact]
    public void APartLayoutsOwnDefaultAnimationBuildsASequencerOverAnEmptyTable()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));

        RuntimeRemoteAnimationState state =
            builder.CreateFromDefaultAnimation(setup, scale: 1f);

        // Built over an empty table, so nothing from the real table can be
        // asked of it — exactly what the drawn host built for such a body.
        Assert.NotNull(state.Sequencer);
        Assert.Equal(0u, state.Sequencer!.CurrentStyle);
        Assert.Equal(0u, state.Sequencer.CurrentMotion);
    }

    [Fact]
    public void PuttingASequencerBackToTheWiresStanceMatchesABuildFromScratch()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x003D,
            ForwardCommand: 0x0005);
        RuntimeRemoteAnimationState state = builder.CreateFromMotionTable(
            setup, TableId, scale: 1f, wireState: null);
        AnimationSequencer sequencer = Assert.IsType<AnimationSequencer>(
            state.Sequencer);

        Assert.True(builder.TryReinitialize(sequencer, TableId, wire));

        AnimationSequencer expected = SpawnMotionInitializer.Create(
            setup, table, loader, wire);
        Assert.Equal(expected.CurrentStyle, sequencer.CurrentStyle);
        Assert.Equal(expected.CurrentMotion, sequencer.CurrentMotion);
    }

    [Fact]
    public void AnIdWithNoTableLeavesASequencerExactlyAsItWas()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));
        RuntimeRemoteAnimationState state = builder.CreateFromMotionTable(
            setup, TableId, scale: 1f, wireState: null);
        AnimationSequencer sequencer = Assert.IsType<AnimationSequencer>(
            state.Sequencer);
        uint styleBefore = sequencer.CurrentStyle;
        uint motionBefore = sequencer.CurrentMotion;

        Assert.False(builder.TryReinitialize(sequencer, 0x09009999u, null));

        Assert.Equal(styleBefore, sequencer.CurrentStyle);
        Assert.Equal(motionBefore, sequencer.CurrentMotion);
    }

    [Fact]
    public void TheResolvedStanceAndCommandComeBackForDiagnostics()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader));
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x003D,
            ForwardCommand: 0x0005);

        SpawnMotionInitializer.Plan expected =
            SpawnMotionInitializer.ResolvePlan(table, wire);
        SpawnMotionInitializer.Plan? resolved =
            builder.TryResolvePlan(TableId, wire);

        Assert.Equal(expected, resolved);
        Assert.Null(builder.TryResolvePlan(0x09009999u, wire));
    }

    /// <summary>
    /// The whole of what a client with nothing to present has to go on: the
    /// creation description. It has to reach the same body a client with a
    /// window builds from the same description.
    /// </summary>
    [Fact]
    public void ABodyBuiltFromNothingButTheCreationDescriptionMatchesATableBuild()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader, setup));
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x003D,
            ForwardCommand: 0x0005);

        RuntimeRemoteAnimationState state = builder.CreateFromSpawn(
            Spawn(SetupId, TableId, wire),
            scale: 2.5f);

        RuntimeRemoteAnimationState expected = builder.CreateFromMotionTable(
            setup, TableId, scale: 2.5f, wire);
        Assert.NotNull(state.Sequencer);
        Assert.Equal(
            expected.Sequencer!.CurrentStyle, state.Sequencer!.CurrentStyle);
        Assert.Equal(
            expected.Sequencer.CurrentMotion, state.Sequencer.CurrentMotion);
        Assert.Equal(2.5f, state.Scale);
    }

    /// <summary>
    /// The description names the table; the part layout's own default is the
    /// fallback, exactly as a client with a window falls back.
    /// </summary>
    [Fact]
    public void ADescriptionWithNoTableOfItsOwnFallsBackToThePartLayoutsDefault()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        setup.DefaultAnimation = (DatReaderWriter.Types.QualifiedDataId<Animation>)0x300u;
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader, setup));

        RuntimeRemoteAnimationState state = builder.CreateFromSpawn(
            Spawn(SetupId, motionTableId: 0u, wireState: null),
            scale: 1f);

        Assert.NotNull(state.Sequencer);
        Assert.Equal(0u, state.Sequencer!.CurrentStyle);
    }

    /// <summary>
    /// No part layout to hand means a body that plays nothing and still wears
    /// its scale: that is what a client with no content for an object has.
    /// </summary>
    [Fact]
    public void ALayoutThisClientHasNoContentForGivesABodyThatPlaysNothing()
    {
        (Setup setup, MotionTable table, Loader loader) = Content();
        var builder = new RuntimeMotionStateBuilder(
            new FixedContent(table, loader, setup));

        RuntimeRemoteAnimationState state = builder.CreateFromSpawn(
            Spawn(setupId: 0x02009999u, TableId, wireState: null),
            scale: 0.5f);

        Assert.Null(state.Sequencer);
        Assert.Equal(0.5f, state.Scale);
    }

    private static AcDream.Core.Net.WorldSession.EntitySpawn Spawn(
        uint setupId,
        uint motionTableId,
        CreateObject.ServerMotionState? wireState) =>
        new(
            0x70000001u,
            null,
            setupId,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "motion state builder fixture",
            null,
            wireState,
            motionTableId == 0u ? null : motionTableId);

    // -- content -------------------------------------------------------------

    private static (Setup Setup, MotionTable Table, Loader Loader) Content()
    {
        var setup = new Setup();
        setup.Parts.Add(0x01000000u);
        setup.DefaultScale.Add(Vector3.One);

        var loader = new Loader();
        loader.Add(0x300u, FlatAnimation(4));
        loader.Add(0x301u, FlatAnimation(6));

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
        return (setup, table, loader);
    }

    private static MotionData Cycle(uint animationId)
    {
        var data = new MotionData();
        QualifiedDataId<Animation> id = animationId;
        data.Anims.Add(new AnimData
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
            var frame = new AnimationFrame(1);
            frame.Frames.Add(new Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            });
            animation.PartFrames.Add(frame);
        }
        return animation;
    }

    private sealed class FixedContent(
        MotionTable table,
        IAnimationLoader loader,
        Setup? setup = null) : IRuntimeMotionContentSource
    {
        public IAnimationLoader AnimationLoader => loader;

        public MotionTable? TryGetMotionTable(uint motionTableId) =>
            motionTableId == TableId ? table : null;

        public Setup? TryGetSetup(uint setupId) =>
            setupId == SetupId ? setup : null;
    }

    private sealed class Loader : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _animations = [];
        public void Add(uint id, Animation animation) =>
            _animations[id] = animation;
        public Animation? LoadAnimation(uint id) =>
            _animations.GetValueOrDefault(id);
    }
}
