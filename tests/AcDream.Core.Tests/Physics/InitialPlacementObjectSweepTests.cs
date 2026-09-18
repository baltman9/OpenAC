using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

// OpenAC #127: the initial-placement pass of a placement search tests cells,
// terrain and buildings only; objects are tested by the placement pass that
// follows and slides or rings out to a free spot. A mover starting inside an
// object must therefore pass the initial sweep and be resolved by the search.
public sealed class InitialPlacementObjectSweepTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;
    private const uint PlayerId = 0x50000001u;
    private const uint StoneId = 0x7AA0C34Fu;
    private const float Radius = 0.48f;
    private const float SphereHeight = 1.835f;

    private static readonly ImmutableArray<FlatCollisionSphere> HumanSpheres = ImmutableArray.Create(
        new FlatCollisionSphere(new Vector3(0f, 0f, Radius), Radius),
        new FlatCollisionSphere(new Vector3(0f, 0f, SphereHeight - Radius), Radius));

    [Fact]
    public void InitialPlacementSweep_IgnoresObjects_PlacementSweepTestsThem()
    {
        PhysicsEngine engine = NewEngine();
        var feet = new Vector3(10f, 10f, 0f);
        RegisterStone(engine, feet + new Vector3(0f, 0f, Radius));

        var transition = new Transition();
        transition.SpherePath.InitPath(feet, feet, Cell, HumanSpheres, 1f);

        transition.SpherePath.InsertType = InsertType.InitialPlacement;
        Assert.Equal(TransitionState.OK, transition.FindObjCollisionsInCell(engine, Cell));

        transition.SpherePath.InsertType = InsertType.Placement;
        Assert.NotEqual(TransitionState.OK, transition.FindObjCollisionsInCell(engine, Cell));
    }

    [Fact]
    public void PlayerArrivingInsideAStuckObject_IsPlacedBesideIt()
    {
        PhysicsEngine engine = NewEngine();
        var feet = new Vector3(10f, 10f, 0f);
        Vector3 stoneCenter = feet + new Vector3(0f, 0f, Radius);
        RegisterStone(engine, stoneCenter);

        PhysicsSetPositionResult result = engine.SetPosition(
            new PhysicsSetPositionRequest(
                Position: feet,
                Orientation: Quaternion.Identity,
                CellId: Cell,
                CellLocalPosition: feet,
                Spheres: HumanSpheres,
                Scale: 1f,
                StepUpHeight: 0.4f,
                StepDownHeight: 0.4f,
                MoverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                MovingEntityId: PlayerId,
                Flags: PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide));

        Assert.True(result.IsCommitted, $"placement failed: {result.Error}");
        float centerDistance = Vector3.Distance(
            result.Position + new Vector3(0f, 0f, Radius), stoneCenter);
        Assert.True(centerDistance >= Radius * 2f - PhysicsGlobals.EPSILON,
            $"placement must clear the object; centers remain {centerDistance:F3} m apart");
        Assert.True(Vector3.Distance(feet, result.Position) <= 4f);
    }

    private static PhysicsEngine NewEngine()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return engine;
    }

    private static void RegisterStone(PhysicsEngine engine, Vector3 center)
        => engine.ShadowObjects.Register(
            StoneId,
            gfxObjId: 0u,
            worldPos: center,
            rotation: Quaternion.Identity,
            radius: Radius,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Landblock,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f,
            scale: 1f,
            state: 0x1u,
            flags: EntityCollisionFlags.HasWeenie,
            seedCellId: Cell,
            isStatic: true);
}
