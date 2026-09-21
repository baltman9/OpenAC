using System.Collections.Immutable;
using System.Numerics;

namespace AcDream.Runtime.Maps;

/// <summary>
/// One landblock's indoor cells flattened onto the ground plane: per height
/// layer, the floor area and the wall lines, in the landblock's own frame
/// and in metres. Built once from the cell geometry the game data carries
/// and never changed afterwards, so it can be handed across threads and
/// kept by whoever asked for it.
/// </summary>
/// <param name="LandblockId">The landblock, with a zero low half.</param>
/// <param name="Layers">The height layers, lowest first.</param>
/// <param name="Cells">Every cell with geometry, in cell id order.</param>
/// <param name="Bounds">The smallest box around every cell's geometry.</param>
/// <param name="Counts">How the polygons were sorted, for whoever judges the plan.</param>
internal sealed record DungeonFloorplan(
    uint LandblockId,
    ImmutableArray<DungeonFloorplanLayer> Layers,
    ImmutableArray<DungeonFloorplanCell> Cells,
    DungeonFloorplanBounds Bounds,
    DungeonFloorplanCounts Counts);

/// <summary>
/// What became of every polygon in a plan: how many were floor, ceiling,
/// wall, doorway, or too steep to be either, and how many wall lines were
/// left once collinear runs were joined.
/// </summary>
internal sealed class DungeonFloorplanCounts
{
    public int Polygons { get; set; }
    public int Floors { get; set; }
    public int Ceilings { get; set; }
    public int Walls { get; set; }
    public int Portals { get; set; }
    public int Slopes { get; set; }
    public int WallsAfterMerge { get; set; }

    public override string ToString() =>
        $"polygons={Polygons} floors={Floors} ceilings={Ceilings} walls={Walls}"
        + $" merged={WallsAfterMerge} doorways={Portals} slopes={Slopes}";
}

/// <summary>
/// One six-metre band of height, drawn together: every floor polygon at
/// that height, and the walls of every cell whose floor is there. A
/// dungeon's storeys sit six metres apart often enough that the band reads
/// as a storey. A tall room's upper cell, which has walls but no floor, is
/// drawn with the cell it opens onto below rather than as an empty outline
/// a band up.
/// </summary>
/// <param name="Z">The band's key: the lowest height it covers, in metres.</param>
/// <param name="Floors">
/// Floor area as closed polygons, each vertex in landblock-local metres.
/// The polygons overlap wherever two cells meet; they are meant to be filled,
/// not stitched.
/// </param>
/// <param name="Walls">Wall lines, collinear runs merged.</param>
internal sealed record DungeonFloorplanLayer(
    float Z,
    ImmutableArray<ImmutableArray<Vector2>> Floors,
    ImmutableArray<DungeonFloorplanWall> Walls);

/// <summary>A wall seen from above: one straight line, in landblock-local metres.</summary>
internal readonly record struct DungeonFloorplanWall(Vector2 Start, Vector2 End)
{
    /// <summary>The wall's length in metres.</summary>
    public float Length => Vector2.Distance(Start, End);
}

/// <summary>
/// Where one cell sits in the plan.
/// </summary>
/// <param name="CellId">The full cell id, landblock in the high half.</param>
/// <param name="Center">The middle of the cell's geometry, in landblock-local metres.</param>
/// <param name="LayerZ">The key of the layer the cell was drawn in.</param>
internal readonly record struct DungeonFloorplanCell(
    uint CellId,
    Vector3 Center,
    float LayerZ);

/// <summary>The box around a plan, in landblock-local metres.</summary>
internal readonly record struct DungeonFloorplanBounds(Vector3 Min, Vector3 Max)
{
    /// <summary>The bounds of nothing, from which any point extends.</summary>
    public static DungeonFloorplanBounds Empty { get; } =
        new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

    /// <summary>True when nothing has been added.</summary>
    public bool IsEmpty => Min.X > Max.X;

    /// <summary>These bounds grown to include a point.</summary>
    public DungeonFloorplanBounds Including(Vector3 point) =>
        new(Vector3.Min(Min, point), Vector3.Max(Max, point));
}
