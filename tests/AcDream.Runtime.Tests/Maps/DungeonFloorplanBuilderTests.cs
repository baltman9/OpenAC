using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.Core.Content;
using AcDream.Runtime.Maps;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Runtime.Tests.Maps;

/// <summary>
/// The floorplan builder over a hand-made landblock: two box rooms, one
/// turned a quarter turn with a doorway, the other a storey higher with a
/// wall authored in two pieces. Floors are the up-facing polygons, walls the
/// vertical ones with the doorway left out, and the two pieces come out as
/// one line.
/// </summary>
public sealed class DungeonFloorplanBuilderTests
{
    private const uint Landblock = 0x01230000u;
    private const ushort EnvironmentId = 0x0123;
    private const ushort BoxWithDoor = 1;
    private const ushort BoxWithSplitWall = 2;

    [Fact]
    public void TwoRoomsBecomeTwoLayersOfFloorsAndWalls()
    {
        var dats = new FakeDats();
        var builder = new DungeonFloorplanBuilder(dats, dats.Lock);

        DungeonFloorplan? plan = builder.TryBuild(Landblock | 0x0100u);

        Assert.NotNull(plan);
        Assert.Equal(Landblock, plan.LandblockId);
        Assert.Equal([0f, 6f], plan.Layers.Select(static layer => layer.Z).ToArray());

        DungeonFloorplanLayer ground = plan.Layers[0];
        Assert.Single(ground.Floors);
        Assert.Equal(4, ground.Floors[0].Length);
        // Six faces: the ceiling faces down and the east wall is the doorway.
        Assert.Equal(3, ground.Walls.Length);
        Assert.Equal(
            [8f, 10f, 10f],
            ground.Walls.Select(static wall => wall.Length).Order().ToArray(),
            new ApproximateFloatComparer());
        // The south wall, local y = 0 from x 0 to 10, turned a quarter turn
        // about (20, 30): local x runs along world +y.
        Assert.Contains(ground.Walls, wall =>
            Matches(wall, new Vector2(20f, 30f), new Vector2(20f, 40f)));

        DungeonFloorplanLayer upstairs = plan.Layers[1];
        // Five wall polygons, four wall lines: the split north wall merged.
        Assert.Equal(4, upstairs.Walls.Length);
        Assert.Equal(
            [8f, 8f, 10f, 10f],
            upstairs.Walls.Select(static wall => wall.Length).Order().ToArray(),
            new ApproximateFloatComparer());
        Assert.Contains(upstairs.Walls, wall =>
            Matches(wall, new Vector2(40f, 38f), new Vector2(50f, 38f)));

        Assert.Equal(
            [Landblock | 0x0100u, Landblock | 0x0101u],
            plan.Cells.Select(static cell => cell.CellId).ToArray());
        Assert.Equal(0f, plan.Cells[0].LayerZ);
        Assert.Equal(6f, plan.Cells[1].LayerZ);
        AssertVector(new Vector3(16f, 35f, 2f), plan.Cells[0].Center);

        AssertVector(new Vector3(12f, 30f, 0.5f), plan.Bounds.Min);
        AssertVector(new Vector3(50f, 40f, 10.2f), plan.Bounds.Max);
    }

    [Fact]
    public void ThePlanAndTheGeometryAreBuiltOnceAndReadUnderTheLock()
    {
        var dats = new FakeDats();
        var builder = new DungeonFloorplanBuilder(dats, dats.Lock);

        DungeonFloorplan? first = builder.TryBuild(Landblock);
        int readsForOne = dats.Reads;
        DungeonFloorplan? again = builder.TryBuild(Landblock | 0xFFFFu);

        Assert.NotNull(first);
        Assert.Same(first, again);
        Assert.Equal(readsForOne, dats.Reads);
        Assert.Equal(2, builder.CachedGeometryCount);
        Assert.Equal(0, dats.ReadsOutsideTheLock);
    }

    [Fact]
    public void ALandblockWithoutCellsHasNoPlan()
    {
        var dats = new FakeDats();
        var builder = new DungeonFloorplanBuilder(dats, dats.Lock);

        Assert.Null(builder.TryBuild(0x45670000u));
        Assert.Null(builder.TryBuild(FakeDats.EmptyLandblock));
    }

    private static bool Matches(DungeonFloorplanWall wall, Vector2 a, Vector2 b) =>
        (Near(wall.Start, a) && Near(wall.End, b))
        || (Near(wall.Start, b) && Near(wall.End, a));

    private static bool Near(Vector2 actual, Vector2 expected) =>
        Vector2.Distance(actual, expected) < 0.01f;

    private static void AssertVector(Vector3 expected, Vector3 actual) =>
        Assert.True(
            Vector3.Distance(expected, actual) < 0.01f,
            $"expected {expected}, got {actual}");

    private sealed class ApproximateFloatComparer : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => MathF.Abs(x - y) < 0.01f;
        public int GetHashCode(float value) => 0;
    }

    /// <summary>
    /// A landblock with two rooms and a third cell with no geometry, plus a
    /// landblock that says it has no cells. Every read has to happen with the
    /// lock held; one that does not is counted.
    /// </summary>
    private sealed class FakeDats : IDatObjectSource
    {
        public const uint EmptyLandblock = 0x89AB0000u;

        private readonly Dictionary<uint, DBObj> _objects = new();

        public object Lock { get; } = new();
        public int Reads { get; private set; }
        public int ReadsOutsideTheLock { get; private set; }

        public FakeDats()
        {
            Add(new LandBlockInfo { Id = Landblock | 0xFFFEu, NumCells = 3 });
            Add(new LandBlockInfo { Id = EmptyLandblock | 0xFFFEu, NumCells = 0 });
            var environment = new DatEnvironment { Id = 0x0D000000u | EnvironmentId };
            environment.Cells[BoxWithDoor] = Box(splitNorthWall: false);
            environment.Cells[BoxWithSplitWall] = Box(splitNorthWall: true);
            Add(environment);
            Add(Cell(
                Landblock | 0x0100u,
                BoxWithDoor,
                new Vector3(20f, 30f, 0.5f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
                doorway: true));
            Add(Cell(
                Landblock | 0x0101u,
                BoxWithSplitWall,
                new Vector3(40f, 30f, 7.2f),
                Quaternion.Identity,
                doorway: false));
            Add(new EnvCell { Id = Landblock | 0x0102u, EnvironmentId = 0 });
        }

        private void Add(DBObj value) => _objects[value.Id] = value;

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : DatReaderWriter.Lib.IO.IDBObj
        {
            Reads++;
            if (!Monitor.IsEntered(Lock))
                ReadsOutsideTheLock++;
            return _objects.TryGetValue(fileId, out DBObj? value) && value is T typed
                ? typed
                : default;
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : DatReaderWriter.Lib.IO.IDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        private static EnvCell Cell(
            uint id, ushort structure, Vector3 origin, Quaternion orientation, bool doorway)
        {
            var cell = new EnvCell
            {
                Id = id,
                EnvironmentId = EnvironmentId,
                CellStructure = structure,
                Position = new Frame { Origin = origin, Orientation = orientation },
            };
            if (doorway)
            {
                cell.CellPortals.Add(new CellPortal
                {
                    PolygonId = EastWall,
                    OtherCellId = 0x0101,
                    OtherPortalId = 0,
                });
            }
            return cell;
        }

        private const ushort Floor = 0;
        private const ushort Ceiling = 1;
        private const ushort SouthWall = 2;
        private const ushort NorthWall = 3;
        private const ushort WestWall = 4;
        private const ushort EastWall = 5;
        private const ushort NorthWallEast = 6;

        /// <summary>A 10 by 8 by 3 metre box with its floor at z = 0.</summary>
        private static CellStruct Box(bool splitNorthWall)
        {
            var cell = new CellStruct { VertexArray = new VertexArray() };
            Vector3[] corners =
            [
                new(0f, 0f, 0f), new(10f, 0f, 0f), new(10f, 8f, 0f), new(0f, 8f, 0f),
                new(0f, 0f, 3f), new(10f, 0f, 3f), new(10f, 8f, 3f), new(0f, 8f, 3f),
                new(5f, 8f, 0f), new(5f, 8f, 3f),
            ];
            for (ushort index = 0; index < corners.Length; index++)
            {
                cell.VertexArray.Vertices[index] = new SWVertex
                {
                    Origin = corners[index],
                    Normal = corners[index].Z == 0f ? Vector3.UnitZ : -Vector3.UnitZ,
                };
            }
            cell.Polygons[Floor] = Polygon(0, 1, 2, 3);
            cell.Polygons[Ceiling] = Polygon(7, 6, 5, 4);
            cell.Polygons[SouthWall] = Polygon(0, 4, 5, 1);
            cell.Polygons[WestWall] = Polygon(0, 3, 7, 4);
            cell.Polygons[EastWall] = Polygon(1, 5, 6, 2);
            if (splitNorthWall)
            {
                cell.Polygons[NorthWall] = Polygon(3, 8, 9, 7);
                cell.Polygons[NorthWallEast] = Polygon(8, 2, 6, 9);
            }
            else
            {
                cell.Polygons[NorthWall] = Polygon(3, 2, 6, 7);
                cell.Portals.Add(EastWall);
            }
            return cell;
        }

        private static Polygon Polygon(params ushort[] vertexIds)
        {
            var polygon = new Polygon { SidesType = CullMode.Clockwise };
            foreach (ushort id in vertexIds)
                polygon.VertexIds.Add((short)id);
            return polygon;
        }
    }
}
