using System.Numerics;
using AcDream.Runtime.Maps;

namespace AcDream.Runtime.Tests.Maps;

/// <summary>
/// The floorplan builder over <see cref="TwoRoomLandblock"/>: floors are the
/// up-facing polygons, walls the vertical ones with the doorway left out,
/// and a wall authored in two pieces comes out as one line.
/// </summary>
public sealed class DungeonFloorplanBuilderTests
{
    private const uint Landblock = TwoRoomLandblock.Landblock;

    [Fact]
    public void TwoRoomsBecomeTwoLayersOfFloorsAndWalls()
    {
        var dats = new TwoRoomLandblock();
        var builder = new DungeonFloorplanBuilder(dats, dats.Lock);

        DungeonFloorplan? plan = builder.TryBuild(TwoRoomLandblock.TurnedRoom);

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
            [TwoRoomLandblock.TurnedRoom, TwoRoomLandblock.UpstairsRoom],
            plan.Cells.Select(static cell => cell.CellId).ToArray());
        Assert.Equal(0f, plan.Cells[0].LayerZ);
        Assert.Equal(6f, plan.Cells[1].LayerZ);
        AssertVector(new Vector3(16f, 35f, 2f), plan.Cells[0].Center);

        AssertVector(new Vector3(12f, 30f, 0.5f), plan.Bounds.Min);
        AssertVector(new Vector3(50f, 40f, 10.2f), plan.Bounds.Max);

        Assert.Equal(2, plan.Counts.Floors);
        Assert.Equal(2, plan.Counts.Ceilings);
        Assert.Equal(1, plan.Counts.Portals);
        Assert.Equal(8, plan.Counts.Walls);
        Assert.Equal(7, plan.Counts.WallsAfterMerge);
    }

    [Fact]
    public void ThePlanAndTheGeometryAreBuiltOnceAndReadUnderTheLock()
    {
        var dats = new TwoRoomLandblock();
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
        var dats = new TwoRoomLandblock();
        var builder = new DungeonFloorplanBuilder(dats, dats.Lock);

        Assert.Null(builder.TryBuild(0x45670000u));
        Assert.Null(builder.TryBuild(TwoRoomLandblock.EmptyLandblock));
    }

    [Fact]
    public void ATallRoomsUpperCellFoldsIntoTheBandOfTheCellBelow()
    {
        var dats = new StackedRoomLandblock();
        var builder = new DungeonFloorplanBuilder(dats, dats.Lock);

        DungeonFloorplan? plan = builder.TryBuild(StackedRoomLandblock.Landblock);

        Assert.NotNull(plan);
        // The lower cell's origin is at z = 3.5, a storey up, but its floor
        // is at z = 2.5; the upper cell's origin is at 8.5 and it has no
        // floor; the lonely cell is at 15 and has no floor and no doorway.
        Assert.Equal([0f, 18f], plan.Layers.Select(static layer => layer.Z).ToArray());

        DungeonFloorplanLayer ground = plan.Layers[0];
        Assert.Single(ground.Floors);
        // Eight wall polygons, four per cell, one on top of the other: four lines.
        Assert.Equal(4, ground.Walls.Length);
        Assert.Contains(ground.Walls, wall =>
            Matches(wall, new Vector2(20f, 30f), new Vector2(30f, 30f)));

        DungeonFloorplanLayer lonely = plan.Layers[1];
        Assert.Empty(lonely.Floors);
        Assert.Equal(4, lonely.Walls.Length);

        Assert.Equal(
            [0f, 0f, 18f],
            plan.Cells.Select(static cell => cell.LayerZ).ToArray());
        Assert.Equal(1, plan.Counts.Floors);
        Assert.Equal(2, plan.Counts.Ceilings);
        Assert.Equal(1, plan.Counts.Portals);
        Assert.Equal(12, plan.Counts.Walls);
        Assert.Equal(8, plan.Counts.WallsAfterMerge);
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
}
