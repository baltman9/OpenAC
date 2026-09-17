using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Streaming;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Physics;

// OpenAC #127: a lifestone recall lands the player at the position the server
// stored for the lifestone link, which on ACE-based servers is beside or inside
// the lifestone itself. The original client's arrival placement (edge-anchored
// placement search with sliding) resolves that to a free spot; ours must too, or
// the login and the recall never materialize and the client sits in portal space.
[Trait("Lane", "InstalledDat")]
public sealed class LifestoneArrivalPlacementInstalledDatTests
    : IClassFixture<LifestoneArrivalPlacementInstalledDatTests.Scene>
{
    private const uint ArrivalCell = 0xA9B40019u;
    private const uint LifestoneSetup = 0x020002EEu;
    private const uint HumanSetup = 0x02000001u;
    private const uint LifestoneEntityId = 0x7AA0C34Fu;

    // Holtburg lifestone instance (world db landblock_instance 2056994895) and the
    // character location ACE saved after a Lifestone Recall to it.
    private static readonly Vector3 LifestonePosition = new(81.3304f, 11.7974f, 94.005f);
    private static readonly Vector3 SavedPosition = new(81.3304f, 14.0196f, 94.005f);
    private static readonly Quaternion Heading = new(0f, 0f, -0.543991f, 0.839091f);

    private readonly Scene _scene;
    private readonly ITestOutputHelper _output;

    public LifestoneArrivalPlacementInstalledDatTests(Scene scene, ITestOutputHelper output)
        => (_scene, _output) = (scene, output);

    private ImmutableArray<FlatCollisionSphere> HumanSpheres => _scene.Mover.Spheres;

    [Fact]
    public void LoginPlacementBesideTheLifestone_Commits()
        => AssertCommits(PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide, "login");

    [Fact]
    public void TeleportArrivalBesideTheLifestone_Commits()
        => AssertCommits(
            PhysicsSetPositionFlags.Teleport | PhysicsSetPositionFlags.Slide
                | PhysicsSetPositionFlags.SendPositionEvent,
            "teleport");

    [Fact]
    public void LoginPlacementAtTheLifestoneItself_Commits()
    {
        PhysicsSetPositionResult result = Place(
            LifestonePosition, PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide);
        _output.WriteLine($"inside requested={LifestonePosition} actual={result.Position} error={result.Error} residence={result.Residence} shapes={_scene.LifestoneShapeCount}");
        Assert.True(result.IsCommitted, $"placement at the lifestone failed: {result.Error}");
    }

    private void AssertCommits(PhysicsSetPositionFlags flags, string label)
    {
        PhysicsSetPositionResult result = Place(SavedPosition, flags);
        _output.WriteLine($"{label} requested={SavedPosition} actual={result.Position} error={result.Error} residence={result.Residence} shapes={_scene.LifestoneShapeCount}");
        Assert.True(result.IsCommitted, $"{label} placement failed: {result.Error}");
        Assert.InRange(Vector3.Distance(result.Position, SavedPosition), 0f, 4f);
    }

    private PhysicsSetPositionResult Place(Vector3 position, PhysicsSetPositionFlags flags)
        => _scene.Engine.SetPosition(new PhysicsSetPositionRequest(
            position, Heading, ArrivalCell, position, HumanSpheres,
            Scale: 1f,
            StepUpHeight: _scene.Mover.StepUpHeight,
            StepDownHeight: _scene.Mover.StepDownHeight,
            MoverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            MovingEntityId: 0x000F4243u,
            Flags: flags));

    public sealed class Scene : IDisposable
    {
        private readonly BoundedTestDatCollection _dat;
        private readonly PakPreparedAssetSource _prepared;
        public PhysicsDataCache Cache { get; }
        public PhysicsEngine Engine { get; }
        public FlatSetupCollision Mover { get; }
        public int LifestoneShapeCount { get; }

        public Scene()
        {
            Assert.Equal("1", System.Environment.GetEnvironmentVariable("ACDREAM_RUN_INSTALLED_DAT_TESTS"));
            string? directory = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
            string? package = System.Environment.GetEnvironmentVariable("ACDREAM_PAK_PATH");
            Assert.True(Directory.Exists(directory), "An explicit installed ACDREAM_DAT_DIR is required.");
            Assert.True(File.Exists(package), "An explicit validated ACDREAM_PAK_PATH is required.");
            _dat = new BoundedTestDatCollection(directory!);
            var bounded = (IDatReaderWriter)_dat;
            _prepared = new PakPreparedAssetSource(package!, bounded);
            PreparedCollisionReadResult<FlatSetupCollision> mover = _prepared.ReadSetupCollision(HumanSetup);
            Assert.Equal(PreparedAssetReadStatus.Loaded, mover.Status);
            Mover = Assert.IsType<FlatSetupCollision>(mover.Data);

            float[] heights = Assert.IsType<Region>(bounded.Get<Region>(0x13000000u)).LandDefs.LandHeightTable;
            var factory = new LandblockBuildFactory(bounded, _prepared, new object(), heights);
            LandblockBuild build = Assert.IsType<LandblockBuild>(factory.Build(new LandblockBuildRequest(
                0xA9B4FFFFu, LandblockStreamJobKind.LoadNear, Generation: 1,
                new LandblockBuildOrigin(0xA9, 0xB4))));
            LandblockCollisionBuild collisions = Assert.IsType<LandblockCollisionBuild>(build.Collisions);
            Cache = PhysicsDataCache.CreateProduction();
            Engine = new PhysicsEngine { DataCache = Cache };
            TerrainSurface terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(build.Landblock, heights);
            var surfaces = new List<CellSurface>();
            var portals = new List<PortalPlane>();
            LandblockPhysicsContentBuilder.PublishPreparedCells(Cache, build.Landblock, collisions,
                Vector3.Zero, surfaces, portals);
            LandblockPhysicsContentBuilder.CacheBuildings(Cache, build.Landblock, terrain, Vector3.Zero);
            LandblockPhysicsContentBuilder.CachePreparedObjects(Cache, collisions);
            Engine.AddLandblock(build.LandblockId, terrain, surfaces, portals, 0f, 0f);
            _ = LandblockPhysicsContentBuilder.PublishStaticCollision(Engine, Cache,
                build.Landblock, collisions, Vector3.Zero);

            // The lifestone weenie: its setup's part collision at the instance pose,
            // with the weenie's authored physics state (Gravity | IgnoreCollisions).
            Setup lifestone = Assert.IsType<Setup>(bounded.Get<Setup>(LifestoneSetup));
            foreach (var part in lifestone.Parts)
            {
                uint gfxId = part.DataId;
                GfxObj gfx = Assert.IsType<GfxObj>(bounded.Get<GfxObj>(gfxId));
                PreparedCollisionReadResult<FlatGfxObjCollisionAsset> prepared = _prepared.ReadGfxObjCollision(gfxId);
                Cache.CacheGfxObj(gfxId, gfx, prepared.Data);
            }
            ShadowPartGeometry? Bounds(uint id)
            {
                FlatGfxObjCollisionAsset? asset = Cache.GetFlatGfxObj(id);
                FlatPhysicsBsp? flat = asset?.PhysicsBsp;
                return flat is { RootIndex: >= 0 }
                    ? ShadowPartGeometry.Create(flat.Nodes[flat.RootIndex].BoundingSphere, asset!.VisualBounds)
                    : null;
            }
            IReadOnlyList<ShadowShape> shapes = ShadowShapeBuilder.FromSetup(
                lifestone, 1f, id => Bounds(id) is not null, physicsBspBounds: Bounds);
            LifestoneShapeCount = shapes.Count;
            Engine.ShadowObjects.RegisterMultiPart(
                LifestoneEntityId, LifestonePosition, Heading, shapes,
                state: 0x410u,
                flags: EntityCollisionFlags.HasWeenie,
                0f, 0f, build.LandblockId,
                seedCellId: ArrivalCell,
                isStatic: false);
        }

        public void Dispose()
        {
            _prepared.Dispose();
            _dat.Dispose();
        }
    }
}
