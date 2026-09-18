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
// stored for the lifestone link, which on the emulated servers is beside or inside
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

    private const PhysicsSetPositionFlags TeleportFlags =
        PhysicsSetPositionFlags.Teleport | PhysicsSetPositionFlags.Slide
        | PhysicsSetPositionFlags.SendPositionEvent;

    // Holtburg lifestone instance (the server's spawn record for it) and the
    // character location the server saved after a Lifestone Recall to it.
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
        => AssertCommits(TeleportFlags, "teleport");

    [Fact]
    public void LoginPlacementAtTheLifestoneItself_Commits()
    {
        PhysicsSetPositionResult result = Place(
            LifestonePosition, PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide);
        _output.WriteLine($"inside requested={LifestonePosition} actual={result.Position} error={result.Error} residence={result.Residence} shapes={_scene.LifestoneShapeCount}");
        Assert.True(result.IsCommitted, $"placement at the lifestone failed: {result.Error}");
    }

    // The order a recall actually runs in. The server sends the destination
    // first and the objects standing in that cell after it, so the arrival is
    // placed against a cell that is not there yet, parks, and is placed again
    // once the cell and its objects have arrived. The three below pin the whole
    // ordering at the placement level: parked while the cell is missing, inside
    // the lifestone if the placement runs before it arrives, beside it once the
    // wake re-places the parked pose.
    [Fact]
    public void ArrivalBeforeTheCellIsResident_Parks()
    {
        ArrivalWorld world = _scene.CreateWorld(
            cellResident: false, lifestonePresent: false);

        PhysicsSetPositionResult parked = Place(world, LifestonePosition, TeleportFlags);

        _output.WriteLine($"parked residence={parked.Residence} cell=0x{parked.CellId:X8} pos={parked.Position}");
        Assert.Equal(PhysicsResidenceDisposition.DeferredCell, parked.Residence);
        Assert.Equal(ArrivalCell, parked.CellId);
        Assert.Equal(LifestonePosition, parked.Position);
    }

    [Fact]
    public void ArrivalPlacedBeforeTheLifestoneArrives_LandsInsideIt()
    {
        ArrivalWorld world = _scene.CreateWorld(
            cellResident: true, lifestonePresent: false);

        PhysicsSetPositionResult early = Place(world, LifestonePosition, TeleportFlags);

        _output.WriteLine($"early residence={early.Residence} pos={early.Position}");
        Assert.True(early.IsCommitted, $"early placement failed: {early.Error}");
        Assert.Equal(LifestonePosition.X, early.Position.X, 3);
        Assert.Equal(LifestonePosition.Y, early.Position.Y, 3);
    }

    [Fact]
    public void ArrivalWokenAfterTheLifestoneArrives_LandsBesideIt()
    {
        ArrivalWorld world = _scene.CreateWorld(
            cellResident: false, lifestonePresent: false);
        PhysicsSetPositionResult parked = Place(world, LifestonePosition, TeleportFlags);
        Assert.Equal(PhysicsResidenceDisposition.DeferredCell, parked.Residence);

        // The cell finishes streaming and the server's objects for it arrive.
        _scene.AdmitCell(world);
        _scene.RegisterLifestone(world);

        // The wake re-places the parked pose against the finished cell.
        PhysicsSetPositionResult woken = Place(
            world, parked.Position, TeleportFlags, parked.CellId);

        _output.WriteLine($"woken residence={woken.Residence} pos={woken.Position}");
        Assert.True(woken.IsCommitted, $"woken placement failed: {woken.Error}");
        Assert.Equal(81.3304f, woken.Position.X, 3);
        Assert.Equal(14.019623f, woken.Position.Y, 3);

        // ...and exactly where one placement against the finished cell lands.
        ArrivalWorld finished = _scene.CreateWorld(
            cellResident: true, lifestonePresent: true);
        Assert.Equal(
            Place(finished, LifestonePosition, TeleportFlags).Position,
            woken.Position);
    }

    private void AssertCommits(PhysicsSetPositionFlags flags, string label)
    {
        PhysicsSetPositionResult result = Place(SavedPosition, flags);
        _output.WriteLine($"{label} requested={SavedPosition} actual={result.Position} error={result.Error} residence={result.Residence} shapes={_scene.LifestoneShapeCount}");
        Assert.True(result.IsCommitted, $"{label} placement failed: {result.Error}");
        Assert.InRange(Vector3.Distance(result.Position, SavedPosition), 0f, 4f);
    }

    private PhysicsSetPositionResult Place(Vector3 position, PhysicsSetPositionFlags flags)
        => Place(_scene.World, position, flags);

    private PhysicsSetPositionResult Place(
        ArrivalWorld world,
        Vector3 position,
        PhysicsSetPositionFlags flags,
        uint cellId = ArrivalCell)
        => world.Engine.SetPosition(new PhysicsSetPositionRequest(
            position, Heading, cellId, position, HumanSpheres,
            Scale: 1f,
            StepUpHeight: _scene.Mover.StepUpHeight,
            StepDownHeight: _scene.Mover.StepDownHeight,
            MoverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            MovingEntityId: 0x000F4243u,
            Flags: flags));

    /// <summary>
    /// One collision world. Each one owns its own cache because a cache owns
    /// the collision roots every engine attached to it shares.
    /// </summary>
    public sealed class ArrivalWorld
    {
        public required PhysicsDataCache Cache { get; init; }
        public required PhysicsEngine Engine { get; init; }
    }

    public sealed class Scene : IDisposable
    {
        private readonly BoundedTestDatCollection _dat;
        private readonly PakPreparedAssetSource _prepared;
        private readonly IDatReaderWriter _bounded;
        private readonly LandblockBuild _build;
        private readonly LandblockCollisionBuild _collisions;
        private readonly TerrainSurface _terrain;
        private readonly Setup _lifestone;
        private readonly IReadOnlyList<ShadowShape> _lifestoneShapes;
        public ArrivalWorld World { get; }
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
            _bounded = _dat;
            _prepared = new PakPreparedAssetSource(package!, _bounded);
            PreparedCollisionReadResult<FlatSetupCollision> mover = _prepared.ReadSetupCollision(HumanSetup);
            Assert.Equal(PreparedAssetReadStatus.Loaded, mover.Status);
            Mover = Assert.IsType<FlatSetupCollision>(mover.Data);

            float[] heights = Assert.IsType<Region>(_bounded.Get<Region>(0x13000000u)).LandDefs.LandHeightTable;
            var factory = new LandblockBuildFactory(_bounded, _prepared, new object(), heights);
            _build = Assert.IsType<LandblockBuild>(factory.Build(new LandblockBuildRequest(
                0xA9B4FFFFu, LandblockStreamJobKind.LoadNear, Generation: 1,
                new LandblockBuildOrigin(0xA9, 0xB4))));
            _collisions = Assert.IsType<LandblockCollisionBuild>(_build.Collisions);
            _terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(_build.Landblock, heights);

            // The lifestone weenie: its setup's part collision at the instance pose,
            // with the weenie's authored physics state (Gravity | IgnoreCollisions).
            _lifestone = Assert.IsType<Setup>(_bounded.Get<Setup>(LifestoneSetup));
            PhysicsDataCache shapeCache = PhysicsDataCache.CreateProduction();
            CacheLifestoneGeometry(shapeCache);
            _lifestoneShapes = BuildLifestoneShapes(shapeCache);
            LifestoneShapeCount = _lifestoneShapes.Count;

            World = CreateWorld(cellResident: true, lifestonePresent: true);
        }

        public PhysicsDataCache Cache => World.Cache;
        public PhysicsEngine Engine => World.Engine;

        /// <summary>
        /// A collision world in one of the states an arrival can meet: the cell
        /// absent (the arrival has to park), present but without the objects the
        /// server spawns in it, or finished.
        /// </summary>
        public ArrivalWorld CreateWorld(bool cellResident, bool lifestonePresent)
        {
            PhysicsDataCache cache = PhysicsDataCache.CreateProduction();
            CacheLifestoneGeometry(cache);
            var world = new ArrivalWorld
            {
                Cache = cache,
                Engine = new PhysicsEngine { DataCache = cache },
            };
            if (cellResident)
                AdmitCell(world);
            if (lifestonePresent)
                RegisterLifestone(world);
            return world;
        }

        public void AdmitCell(ArrivalWorld world)
        {
            var surfaces = new List<CellSurface>();
            var portals = new List<PortalPlane>();
            LandblockPhysicsContentBuilder.PublishPreparedCells(
                world.Cache, _build.Landblock, _collisions, Vector3.Zero, surfaces, portals);
            LandblockPhysicsContentBuilder.CacheBuildings(
                world.Cache, _build.Landblock, _terrain, Vector3.Zero);
            LandblockPhysicsContentBuilder.CachePreparedObjects(world.Cache, _collisions);
            world.Engine.AddLandblock(
                _build.LandblockId, _terrain, surfaces, portals, 0f, 0f);
            _ = LandblockPhysicsContentBuilder.PublishStaticCollision(
                world.Engine, world.Cache, _build.Landblock, _collisions, Vector3.Zero);
        }

        public void RegisterLifestone(ArrivalWorld world) =>
            world.Engine.ShadowObjects.RegisterMultiPart(
                LifestoneEntityId, LifestonePosition, Heading, _lifestoneShapes,
                state: 0x410u,
                flags: EntityCollisionFlags.HasWeenie,
                0f, 0f, _build.LandblockId,
                seedCellId: ArrivalCell,
                isStatic: false);

        private void CacheLifestoneGeometry(PhysicsDataCache cache)
        {
            foreach (var part in _lifestone.Parts)
            {
                uint gfxId = part.DataId;
                GfxObj gfx = Assert.IsType<GfxObj>(_bounded.Get<GfxObj>(gfxId));
                PreparedCollisionReadResult<FlatGfxObjCollisionAsset> prepared =
                    _prepared.ReadGfxObjCollision(gfxId);
                cache.CacheGfxObj(gfxId, gfx, prepared.Data);
            }
        }

        private IReadOnlyList<ShadowShape> BuildLifestoneShapes(PhysicsDataCache cache)
        {
            ShadowPartGeometry? Bounds(uint id)
            {
                FlatGfxObjCollisionAsset? asset = cache.GetFlatGfxObj(id);
                FlatPhysicsBsp? flat = asset?.PhysicsBsp;
                return flat is { RootIndex: >= 0 }
                    ? ShadowPartGeometry.Create(flat.Nodes[flat.RootIndex].BoundingSphere, asset!.VisualBounds)
                    : null;
            }
            return ShadowShapeBuilder.FromSetup(
                _lifestone, 1f, id => Bounds(id) is not null, physicsBspBounds: Bounds);
        }

        public void Dispose()
        {
            _prepared.Dispose();
            _dat.Dispose();
        }
    }
}
