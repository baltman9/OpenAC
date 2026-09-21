using System.Numerics;

namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A wall seen from above: one straight line in landblock-local metres. See
/// <see cref="PluginDungeonFloorplan"/> for the frame.
/// </summary>
/// <param name="Start">One end of the wall.</param>
/// <param name="End">The other end.</param>
public readonly record struct PluginDungeonWall(Vector2 Start, Vector2 End);

/// <summary>
/// One cell of a floorplan: where it is and which layer it was drawn in.
/// </summary>
/// <param name="CellId">The full cell id, landblock in the high half.</param>
/// <param name="Center">The middle of the cell's geometry, in landblock-local metres.</param>
/// <param name="LayerZ">The <see cref="PluginDungeonLayer.Z"/> of the layer the cell is drawn in.</param>
public readonly record struct PluginDungeonCell(uint CellId, Vector3 Center, float LayerZ);

/// <summary>
/// One storey of a floorplan: the cells whose origin falls in one six-metre
/// band of height, flattened together.
/// </summary>
/// <param name="Z">
/// The lowest height the band covers, in metres; bands are six metres tall
/// and shifted down three, so a cell at height z is in the band
/// floor((z + 3) / 6) * 6.
/// </param>
/// <param name="Floors">
/// The floor area as closed polygons, each vertex in landblock-local metres,
/// in the order the data gives them. The polygons overlap wherever two cells
/// meet; fill each one and the room comes out whole.
/// </param>
/// <param name="Walls">The walls as lines, collinear runs already joined.</param>
public sealed record PluginDungeonLayer(
    float Z,
    IReadOnlyList<IReadOnlyList<Vector2>> Floors,
    IReadOnlyList<PluginDungeonWall> Walls);

/// <summary>
/// One landblock's indoor cells seen from above, worked out from the cell
/// geometry the game data carries: level faces are floor, standing faces
/// are walls, and the doorways between cells are left open. Everything is
/// in the landblock's own frame and in metres: x grows east and y grows
/// north from the landblock's south-west corner, the frame the game's own
/// cell positions use. The plan is built once and never changed, so it is
/// safe to keep and to read from any thread.
/// </summary>
/// <param name="LandblockId">The landblock the plan is of, with a zero low half.</param>
/// <param name="Layers">The storeys, lowest first.</param>
/// <param name="Cells">Every cell with geometry, in cell id order.</param>
/// <param name="BoundsMin">The lowest corner of the box around every cell's geometry, in landblock-local metres.</param>
/// <param name="BoundsMax">The highest corner of that box.</param>
public sealed record PluginDungeonFloorplan(
    uint LandblockId,
    IReadOnlyList<PluginDungeonLayer> Layers,
    IReadOnlyList<PluginDungeonCell> Cells,
    Vector3 BoundsMin,
    Vector3 BoundsMax)
{
    /// <summary>
    /// The plan of nothing: what comes back for a landblock the host does
    /// not know, a landblock with no indoor cells, or a host that cannot
    /// read the game data.
    /// </summary>
    public static PluginDungeonFloorplan Empty { get; } =
        new(0u, Array.Empty<PluginDungeonLayer>(), Array.Empty<PluginDungeonCell>(), Vector3.Zero, Vector3.Zero);

    /// <summary>True when there is nothing to draw.</summary>
    public bool IsEmpty => Layers.Count == 0;

    /// <summary>
    /// A position in the plan's frame: metres east and north of the
    /// south-west corner of the position's own landblock, and metres up.
    /// Compare its landblock (<see cref="PluginNavigationPosition.CellId"/>
    /// with a zero low half) with <see cref="LandblockId"/> before drawing
    /// it on this plan.
    /// </summary>
    /// <param name="position">A position as the navigation surface reports it.</param>
    public static Vector3 ToLandblockLocal(in PluginNavigationPosition position)
    {
        uint blockX = (position.CellId >> 24) & 0xFFu;
        uint blockY = (position.CellId >> 16) & 0xFFu;
        return new Vector3(
            (float)((position.EastWest * 240d) + 84d - (((double)blockX - 127d) * 192d)),
            (float)((position.NorthSouth * 240d) + 84d - (((double)blockY - 127d) * 192d)),
            (float)(position.Elevation * 240d));
    }
}

/// <summary>
/// The shape of the dungeon the character is in, for a plugin drawing a map
/// of it. The plan is data, not a picture: the plugin draws it however it
/// likes, on whatever it has to draw with.
/// </summary>
public interface IDungeonMapAutomation
{
    /// <summary>
    /// Whether <paramref name="cellId"/> is an indoor cell that sees nothing
    /// outside, as a dungeon's cells are; false for outdoor cells, for the
    /// interiors of buildings that open onto the landscape, for cells the
    /// data lacks, and on a host that cannot read the game data, which is
    /// what the default implementation always reports.
    /// </summary>
    /// <param name="cellId">The full cell id, landblock in the high half.</param>
    bool IsSealedDungeon(uint cellId) => false;

    /// <summary>
    /// The landblock the character stands in, with a zero low half, or zero
    /// when the character has no position yet, which is also what the
    /// default implementation always reports.
    /// </summary>
    uint CurrentLandblockId => 0u;

    /// <summary>
    /// The floorplan of a landblock's indoor cells, built from the game data
    /// the first time it is asked for and the same plan every time after.
    /// Any cell id in the landblock will do. Reading the data can take a few
    /// milliseconds on a large dungeon the first time; the plan is worth
    /// keeping. <see cref="PluginDungeonFloorplan.Empty"/> comes back for a
    /// landblock the data does not have, for one with no indoor cells, and
    /// on a host that cannot read the game data, which is what the default
    /// implementation always returns.
    /// </summary>
    /// <param name="landblockId">The landblock, or any cell in it.</param>
    PluginDungeonFloorplan CaptureFloorplan(uint landblockId) => PluginDungeonFloorplan.Empty;
}
