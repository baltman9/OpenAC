using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Runtime.Tests.Maps;

/// <summary>
/// Game files holding one tall room authored the way the real ones are: a
/// lower cell with a floor and walls, and on top of it an upper cell with
/// walls and a ceiling but no floor, whose doorway list names the cell it
/// opens onto below. The lower cell's origin sits a storey above its floor,
/// so a floor bucketed by its origin lands in the wrong band. A third cell
/// off on its own has walls and no floor and no doorway at all.
/// </summary>
internal sealed class StackedRoomLandblock : IDatObjectSource
{
    public const uint Landblock = 0x02340000u;
    public const uint LowerRoom = Landblock | 0x0100u;
    public const uint UpperRoom = Landblock | 0x0101u;
    public const uint LonelyRoom = Landblock | 0x0102u;

    private const ushort EnvironmentId = 0x0234;
    private const ushort BoxWithFloor = 1;
    private const ushort BoxWithOpening = 2;
    private const ushort BoxWithNeither = 3;

    private const ushort Floor = 0;
    private const ushort Ceiling = 1;
    private const ushort SouthWall = 2;
    private const ushort NorthWall = 3;
    private const ushort WestWall = 4;
    private const ushort EastWall = 5;

    private readonly Dictionary<uint, DBObj> _objects = new();

    public object Lock { get; } = new();

    public StackedRoomLandblock()
    {
        Add(new LandBlockInfo { Id = Landblock | 0xFFFEu, NumCells = 3 });
        var environment = new DatEnvironment { Id = 0x0D000000u | EnvironmentId };
        // The lower box's floor is a metre under its origin and it has no
        // ceiling: the room carries on upward into the cell above.
        environment.Cells[BoxWithFloor] = Box(bottom: -1f, top: 5f, floor: true, ceiling: false, opening: false);
        // The upper box opens downward through a portal polygon where a
        // floor would be.
        environment.Cells[BoxWithOpening] = Box(bottom: 0f, top: 6f, floor: false, ceiling: true, opening: true);
        environment.Cells[BoxWithNeither] = Box(bottom: 0f, top: 3f, floor: false, ceiling: true, opening: false);
        Add(environment);
        Add(Cell(LowerRoom, BoxWithFloor, new Vector3(20f, 30f, 3.5f), opensOnto: 0x0101));
        Add(Cell(UpperRoom, BoxWithOpening, new Vector3(20f, 30f, 8.5f), opensOnto: 0x0100));
        Add(Cell(LonelyRoom, BoxWithNeither, new Vector3(60f, 30f, 15f), opensOnto: null));
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

    private static EnvCell Cell(uint id, ushort structure, Vector3 origin, ushort? opensOnto)
    {
        var cell = new EnvCell
        {
            Id = id,
            EnvironmentId = EnvironmentId,
            CellStructure = structure,
            Position = new Frame { Origin = origin, Orientation = Quaternion.Identity },
        };
        if (opensOnto is { } other)
        {
            // The lower cell's doorway is its open top; the upper cell's is
            // its open bottom. Which polygon each is does not matter here,
            // only which cell it leads to.
            cell.CellPortals.Add(new CellPortal
            {
                PolygonId = structure == BoxWithOpening ? Floor : Ceiling,
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
