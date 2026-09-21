using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Runtime.Tests.Maps;

/// <summary>
/// Game files holding one shaft three cells tall: a bottom cell with a floor,
/// a middle cell with no floor that opens onto the bottom, and a top cell
/// with no floor that opens only onto the middle. The top cell has no way
/// down except through another floorless cell, so it can be folded into the
/// bottom's band only after the middle has been; and the cells are numbered
/// so that a single folding pass meets the top before the middle.
/// </summary>
internal sealed class ChainedShaftLandblock : IDatObjectSource
{
    public const uint Landblock = 0x03450000u;
    public const uint BottomCell = Landblock | 0x0100u;
    public const uint MiddleCell = Landblock | 0x0101u;
    public const uint TopCell = Landblock | 0x0102u;

    private const ushort EnvironmentId = 0x0345;
    private const ushort BoxWithFloor = 1;
    private const ushort BoxOpenBelow = 2;
    private const ushort BoxOpenBelowWithCeiling = 3;

    private const ushort Floor = 0;
    private const ushort Ceiling = 1;
    private const ushort SouthWall = 2;
    private const ushort NorthWall = 3;
    private const ushort WestWall = 4;
    private const ushort EastWall = 5;

    private readonly Dictionary<uint, DBObj> _objects = new();

    public object Lock { get; } = new();

    public ChainedShaftLandblock()
    {
        Add(new LandBlockInfo { Id = Landblock | 0xFFFEu, NumCells = 3 });
        var environment = new DatEnvironment { Id = 0x0D000000u | EnvironmentId };
        // The bottom box's floor is a metre under its origin; it has no
        // ceiling, the shaft carries on upward.
        environment.Cells[BoxWithFloor] = Box(bottom: -1f, top: 5f, floor: true, ceiling: false, opening: false);
        // The middle box opens downward through a portal polygon where a
        // floor would be, and has no ceiling either.
        environment.Cells[BoxOpenBelow] = Box(bottom: 0f, top: 6f, floor: false, ceiling: false, opening: true);
        // The top box opens downward the same way and is roofed.
        environment.Cells[BoxOpenBelowWithCeiling] = Box(bottom: 0f, top: 6f, floor: false, ceiling: true, opening: true);
        Add(environment);
        // Origins a storey apart: the bottom's floor is in band 0, the
        // middle's origin in band 6, the top's origin in band 12.
        Add(Cell(BottomCell, BoxWithFloor, new Vector3(20f, 30f, 3.5f), opensOnto: [(Ceiling, 0x0101)]));
        Add(Cell(MiddleCell, BoxOpenBelow, new Vector3(20f, 30f, 8.5f), opensOnto: [(Floor, 0x0100), (Ceiling, 0x0102)]));
        Add(Cell(TopCell, BoxOpenBelowWithCeiling, new Vector3(20f, 30f, 14.5f), opensOnto: [(Floor, 0x0101)]));
    }

    private void Add(DBObj value) => _objects[value.Id] = value;

    [return: MaybeNull]
    public T Get<T>(uint fileId) where T : DatReaderWriter.Lib.IO.IDBObj
    {
        if (!Monitor.IsEntered(Lock))
            throw new InvalidOperationException("read outside the lock");
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
        uint id,
        ushort structure,
        Vector3 origin,
        (ushort Polygon, ushort OtherCell)[] opensOnto)
    {
        var cell = new EnvCell
        {
            Id = id,
            EnvironmentId = EnvironmentId,
            CellStructure = structure,
            Position = new Frame { Origin = origin, Orientation = Quaternion.Identity },
        };
        foreach ((ushort polygon, ushort other) in opensOnto)
        {
            cell.CellPortals.Add(new CellPortal
            {
                PolygonId = polygon,
                OtherCellId = other,
                OtherPortalId = 0,
            });
        }
        return cell;
    }

    /// <summary>A 10 by 8 metre box from <paramref name="bottom"/> to <paramref name="top"/>, with the pieces asked for.</summary>
    private static CellStruct Box(float bottom, float top, bool floor, bool ceiling, bool opening)
    {
        var cell = new CellStruct { VertexArray = new VertexArray() };
        Vector3[] corners =
        [
            new(0f, 0f, bottom), new(10f, 0f, bottom), new(10f, 8f, bottom), new(0f, 8f, bottom),
            new(0f, 0f, top), new(10f, 0f, top), new(10f, 8f, top), new(0f, 8f, top),
        ];
        for (ushort index = 0; index < corners.Length; index++)
        {
            cell.VertexArray.Vertices[index] = new SWVertex
            {
                Origin = corners[index],
                Normal = corners[index].Z == bottom ? Vector3.UnitZ : -Vector3.UnitZ,
            };
        }
        if (floor || opening)
        {
            cell.Polygons[Floor] = Polygon(0, 1, 2, 3);
            if (opening)
                cell.Portals.Add(Floor);
        }
        if (ceiling)
            cell.Polygons[Ceiling] = Polygon(7, 6, 5, 4);
        cell.Polygons[SouthWall] = Polygon(0, 4, 5, 1);
        cell.Polygons[WestWall] = Polygon(0, 3, 7, 4);
        cell.Polygons[EastWall] = Polygon(1, 5, 6, 2);
        cell.Polygons[NorthWall] = Polygon(3, 2, 6, 7);
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
