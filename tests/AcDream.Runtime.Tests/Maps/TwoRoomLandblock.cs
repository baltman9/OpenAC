using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Runtime.Tests.Maps;

/// <summary>
/// Game files holding one landblock of two box rooms: one turned a quarter
/// turn with a doorway in its east wall, the other a storey higher with its
/// north wall authored in two pieces. A third cell has no geometry and sees
/// outside; a second landblock says it has no cells. Every read has to
/// happen with the lock held; one that does not is counted.
/// </summary>
internal sealed class TwoRoomLandblock : IDatObjectSource
{
    public const uint Landblock = 0x01230000u;
    public const uint TurnedRoom = Landblock | 0x0100u;
    public const uint UpstairsRoom = Landblock | 0x0101u;
    public const uint CellSeeingOutside = Landblock | 0x0102u;
    public const uint EmptyLandblock = 0x89AB0000u;

    private const ushort EnvironmentId = 0x0123;
    private const ushort BoxWithDoor = 1;
    private const ushort BoxWithSplitWall = 2;

    private const ushort Floor = 0;
    private const ushort Ceiling = 1;
    private const ushort SouthWall = 2;
    private const ushort NorthWall = 3;
    private const ushort WestWall = 4;
    private const ushort EastWall = 5;
    private const ushort NorthWallEast = 6;

    private readonly Dictionary<uint, DBObj> _objects = new();

    public object Lock { get; } = new();
    public int Reads { get; private set; }
    public int ReadsOutsideTheLock { get; private set; }

    public TwoRoomLandblock()
    {
        Add(new LandBlockInfo { Id = Landblock | 0xFFFEu, NumCells = 3 });
        Add(new LandBlockInfo { Id = EmptyLandblock | 0xFFFEu, NumCells = 0 });
        var environment = new DatEnvironment { Id = 0x0D000000u | EnvironmentId };
        environment.Cells[BoxWithDoor] = Box(splitNorthWall: false);
        environment.Cells[BoxWithSplitWall] = Box(splitNorthWall: true);
        Add(environment);
        Add(Cell(
            TurnedRoom,
            BoxWithDoor,
            new Vector3(20f, 30f, 0.5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            doorway: true));
        Add(Cell(
            UpstairsRoom,
            BoxWithSplitWall,
            new Vector3(40f, 30f, 7.2f),
            Quaternion.Identity,
            doorway: false));
        Add(new EnvCell
        {
            Id = CellSeeingOutside,
            EnvironmentId = 0,
            Flags = EnvCellFlags.SeenOutside,
        });
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
