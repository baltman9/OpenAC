using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Physics;

/// <summary>
/// How wide and how tall a thing is has to have ONE answer. The windowed host
/// used to work it out itself while the shared physics owner worked out the
/// same thing from the same authored shape and the same scale; nothing
/// compared the two. These tests pin that the two agree on every shape the
/// wire can describe, so the windowed helpers can simply ask.
/// </summary>
public sealed class EntityBodyShapeOneSourceTests
{
    private const uint Guid = 0x50000123u;
    private const uint CellId = 0x01010001u;
    private const uint SetupId = 0x02000042u;

    private sealed class Harness
    {
        public required LiveEntityRuntime Runtime { get; init; }
        public required LiveEntityMotionRuntimeController Controller { get; init; }
        public required WorldEntity Entity { get; init; }

        public (float Radius, float Height) WindowedCylinder() =>
            Controller.GetSetupCylinder(Guid, Entity);

        public (ImmutableArray<FlatCollisionSphere> Spheres, float Scale,
                float StepUpHeight, float StepDownHeight)
            WindowedMoverShape() =>
            Controller.GetSetupMoverShape(Guid, Entity);

        public (float Radius, float Height) SharedCylinder() =>
            Runtime.Physics.EntityBodyShape(Guid) ?? (0f, 0f);

        public (ImmutableArray<FlatCollisionSphere> Spheres, float Scale,
                float StepUpHeight, float StepDownHeight)
            SharedMoverShape() =>
            Runtime.Physics.EntityMoverShape(Guid);
    }

    private static Harness Build(FlatSetupCollision? setup, float? scale)
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            CellId & 0xFFFF0000u,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        LiveEntityRuntime runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));

        // Both answers read the SAME prepared-shape store: the windowed host's
        // cache IS the shared owner's cache in production.
        if (setup is not null)
            runtime.Physics.DataCache.CacheSetup(SetupId, setup);

        runtime.RegisterAndMaterializeProjection(Spawn(scale));
        Assert.True(runtime.TryGetWorldEntity(Guid, out WorldEntity entity));

        var origin = new LiveWorldOriginState();
        origin.Recenter(1, 1);
        var controller = new LiveEntityMotionRuntimeController(
            runtime,
            static () => null,
            new SelectionState(),
            origin);

        return new Harness
        {
            Runtime = runtime,
            Controller = controller,
            Entity = entity,
        };
    }

    private static void AssertOneAnswer(Harness harness)
    {
        Assert.Equal(harness.SharedCylinder(), harness.WindowedCylinder());

        var shared = harness.SharedMoverShape();
        var windowed = harness.WindowedMoverShape();
        Assert.Equal(shared.Scale, windowed.Scale);
        Assert.Equal(shared.StepUpHeight, windowed.StepUpHeight);
        Assert.Equal(shared.StepDownHeight, windowed.StepDownHeight);
        Assert.Equal(shared.Spheres, windowed.Spheres);
    }

    private static FlatSetupCollision MakeSetup(
        float radius,
        float height,
        IEnumerable<FlatCollisionSphere>? spheres = null,
        float stepUp = 0.4f,
        float stepDown = 0.4f) =>
        new(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            spheres is null
                ? ImmutableArray<FlatCollisionSphere>.Empty
                : [.. spheres],
            height: height,
            radius: radius,
            stepUpHeight: stepUp,
            stepDownHeight: stepDown);

    // ── the shapes the wire can describe ─────────────────────────────────────

    [Fact]
    public void APlainCreature_HasOneGirthAndHeight()
    {
        Harness harness = Build(
            MakeSetup(radius: 0.5f, height: 1.8f,
                spheres: [new FlatCollisionSphere(Vector3.Zero, 0.5f)]),
            scale: null);

        AssertOneAnswer(harness);
        Assert.Equal((0.5f, 1.8f), harness.SharedCylinder());
        Assert.Equal(1f, harness.SharedMoverShape().Scale);
    }

    [Theory]
    [InlineData(2.5f, 1.25f, 4.5f)]
    [InlineData(0.5f, 0.25f, 0.9f)]
    public void AScaledCreature_IsGrownOnceByTheScaleTheServerGave(
        float scale, float expectedRadius, float expectedHeight)
    {
        Harness harness = Build(
            MakeSetup(radius: 0.5f, height: 1.8f,
                spheres: [new FlatCollisionSphere(Vector3.Zero, 0.5f)]),
            scale: scale);

        AssertOneAnswer(harness);
        (float radius, float height) = harness.SharedCylinder();
        Assert.Equal(expectedRadius, radius, precision: 5);
        Assert.Equal(expectedHeight, height, precision: 5);
        Assert.Equal(scale, harness.SharedMoverShape().Scale);
    }

    [Fact]
    public void AShapeWithNoCylinderAndNoSpheres_IsZeroOnBothAnswers()
    {
        Harness harness = Build(MakeSetup(radius: 0f, height: 0f), scale: 3f);

        AssertOneAnswer(harness);
        Assert.Equal((0f, 0f), harness.SharedCylinder());
        Assert.Empty(harness.SharedMoverShape().Spheres);
    }

    [Fact]
    public void AShapeBuiltFromSeveralBalls_KeepsThemAllInOrder()
    {
        FlatCollisionSphere[] balls =
        [
            new(new Vector3(0f, 0f, 0.3f), 0.30f),
            new(new Vector3(0f, 0f, 0.9f), 0.35f),
            new(new Vector3(0f, 0f, 1.5f), 0.28f),
        ];
        Harness harness = Build(
            MakeSetup(radius: 0.35f, height: 1.8f, spheres: balls,
                stepUp: 0.55f, stepDown: 0.65f),
            scale: 2f);

        AssertOneAnswer(harness);
        var shape = harness.SharedMoverShape();
        Assert.Equal(balls, shape.Spheres);
        Assert.Equal(2f, shape.Scale);
        // The step allowance is grown by the same scale as the girth.
        Assert.Equal(1.10f, shape.StepUpHeight, precision: 5);
        Assert.Equal(1.30f, shape.StepDownHeight, precision: 5);
    }

    [Fact]
    public void AShapeWithNoDeclaredStepHeights_GetsTheOrdinaryAllowance()
    {
        Harness harness = Build(
            MakeSetup(radius: 0.5f, height: 1.8f, stepUp: 0f, stepDown: 0f),
            scale: 2f);

        AssertOneAnswer(harness);
        var shape = harness.SharedMoverShape();
        Assert.Equal(0.4f, shape.StepUpHeight);
        Assert.Equal(0.4f, shape.StepDownHeight);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-2f)]
    public void AScaleOfZeroOrLess_IsTakenAsUnscaled(float nonsenseScale)
    {
        Harness harness = Build(
            MakeSetup(radius: 0.5f, height: 1.8f), scale: nonsenseScale);

        AssertOneAnswer(harness);
        Assert.Equal((0.5f, 1.8f), harness.SharedCylinder());
        Assert.Equal(1f, harness.SharedMoverShape().Scale);
    }

    [Fact]
    public void AShapeThatIsNotToHand_IsZeroWithTheOrdinaryStepAllowance()
    {
        Harness harness = Build(setup: null, scale: 2f);

        AssertOneAnswer(harness);
        Assert.Equal((0f, 0f), harness.SharedCylinder());
        var shape = harness.SharedMoverShape();
        Assert.Empty(shape.Spheres);
        Assert.Equal(1f, shape.Scale);
        Assert.Equal(0.4f, shape.StepUpHeight);
        Assert.Equal(0.4f, shape.StepDownHeight);
    }

    [Fact]
    public void AnIdNothingLiveIsCarrying_IsZero()
    {
        Harness harness = Build(
            MakeSetup(radius: 0.5f, height: 1.8f), scale: null);

        Assert.Null(harness.Runtime.Physics.EntityBodyShape(0xDEADBEEFu));
        var shape = harness.Runtime.Physics.EntityMoverShape(0xDEADBEEFu);
        Assert.Empty(shape.Spheres);
        Assert.Equal(1f, shape.Scale);
        Assert.Equal(0.4f, shape.StepUpHeight);
        Assert.Equal(0.4f, shape.StepDownHeight);
    }

    // ── the spawn ────────────────────────────────────────────────────────────

    private static WorldSession.EntitySpawn Spawn(float? scale)
    {
        var position = new CreateObject.ServerPosition(
            CellId, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1, Movement: 1, State: 1, Vector: 1, Teleport: 0,
            ServerControlledMove: 1, ForcePosition: 0, ObjDesc: 1, Instance: 1);
        // One CreateObject writes the scale once: the physics description and
        // the spawn carry the same value, which is why the two answers can be
        // read off either.
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Static,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: SetupId,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: scale,
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
            Guid: Guid,
            Position: position,
            SetupTableId: SetupId,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: scale,
            Name: "shape subject",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: (uint)PhysicsStateFlags.Static,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
