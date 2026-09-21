using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Maps;

/// <summary>
/// Builds a <see cref="DungeonFloorplan"/> for a landblock from the cell
/// geometry the game data carries: each indoor cell's structure, placed by
/// the cell's own position and turn, with every polygon flattened onto the
/// ground plane. A level polygon that faces up is floor; a standing polygon
/// is a wall, seen edge-on as a line; the doorways between cells, which the
/// data lists as portal polygons, are left open.
///
/// <para>The game files are read with the caller's lock held and only for as
/// long as the read takes; the flattening runs afterwards, off the lock. What
/// a structure flattens to is kept once per structure, since hundreds of
/// cells share a few dozen, and a finished plan is kept per landblock, since
/// the data never changes underneath it.</para>
/// </summary>
internal sealed class DungeonFloorplanBuilder
{
    /// <summary>
    /// How far a polygon's normal must lean upward, as the cosine of its
    /// angle from straight up, to count as level: 0.7 is about 45 degrees,
    /// so a stair ramp is floor and a steep chute is not.
    /// </summary>
    private const float LevelMinimumUp = 0.7f;

    /// <summary>
    /// The most a polygon's normal may lean up or down, as a cosine, to
    /// count as standing: 0.3 is about 17 degrees off vertical.
    /// </summary>
    private const float StandingMaximumUp = 0.3f;

    /// <summary>Cells are bucketed into bands this tall, in metres.</summary>
    private const float LayerHeight = 6f;

    /// <summary>
    /// Bands are shifted down by this much so a cell whose origin sits a
    /// little below a whole band still lands in that band.
    /// </summary>
    private const float LayerOffset = 3f;

    /// <summary>Two wall lines within this angle, in degrees, may be one line.</summary>
    private const float MergeAngleDegrees = 1f;

    /// <summary>Two wall lines this far apart sideways, in metres, may be one line.</summary>
    private const float MergeOffsetMetres = 0.05f;

    /// <summary>Two collinear spans with a gap this small, in metres, are joined.</summary>
    private const float MergeGapMetres = 0.05f;

    /// <summary>A wall shorter than this, in metres, is a sliver and dropped.</summary>
    private const float MinimumWallMetres = 0.01f;

    private readonly IDatObjectSource _dats;
    private readonly object _datLock;
    private readonly object _cacheGate = new();
    private readonly Dictionary<uint, CellGeometry?> _geometryByStructure = new();
    private readonly Dictionary<uint, DungeonFloorplan?> _plansByLandblock = new();

    /// <param name="dats">The game files.</param>
    /// <param name="datLock">The lock every read of <paramref name="dats"/> is made under.</param>
    public DungeonFloorplanBuilder(IDatObjectSource dats, object datLock)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
    }

    /// <summary>How many cell structures have been flattened and kept.</summary>
    public int CachedGeometryCount
    {
        get { lock (_cacheGate) return _geometryByStructure.Count; }
    }

    /// <summary>
    /// The plan for the landblock of <paramref name="landblockId"/> (any cell
    /// id in it will do), or null when the landblock is not in the data or
    /// has no indoor cells. The same plan comes back on every later call.
    /// </summary>
    public DungeonFloorplan? TryBuild(uint landblockId)
    {
        uint landblock = landblockId & 0xFFFF0000u;
        lock (_cacheGate)
        {
            if (_plansByLandblock.TryGetValue(landblock, out DungeonFloorplan? cached))
                return cached;
        }

        DungeonFloorplan? plan = Build(landblock);
        lock (_cacheGate)
        {
            // Two callers may race to the same landblock; both plans are
            // the same data, so the first one kept wins and the other is let go.
            if (_plansByLandblock.TryGetValue(landblock, out DungeonFloorplan? kept))
                return kept;
            _plansByLandblock[landblock] = plan;
            return plan;
        }
    }

    private DungeonFloorplan? Build(uint landblock)
    {
        List<PlacedCell> placed = ReadCells(landblock);
        if (placed.Count == 0)
            return null;

        var counts = new DungeonFloorplanCounts();
        var bounds = DungeonFloorplanBounds.Empty;
        var cells = ImmutableArray.CreateBuilder<DungeonFloorplanCell>(placed.Count);
        var layers = new SortedDictionary<float, LayerBuilder>();

        foreach (PlacedCell cell in placed)
        {
            float layerZ = LayerKey(cell.Origin.Z);
            if (!layers.TryGetValue(layerZ, out LayerBuilder? layer))
                layers[layerZ] = layer = new LayerBuilder();

            Matrix4x4 transform = Matrix4x4.CreateFromQuaternion(cell.Orientation)
                * Matrix4x4.CreateTranslation(cell.Origin);
            var cellBounds = DungeonFloorplanBounds.Empty;

            foreach (PolygonGeometry polygon in cell.Geometry.Polygons)
            {
                counts.Polygons++;
                var world = new Vector3[polygon.Vertices.Length];
                for (int index = 0; index < world.Length; index++)
                {
                    world[index] = Vector3.Transform(polygon.Vertices[index], transform);
                    cellBounds = cellBounds.Including(world[index]);
                }

                if (polygon.IsPortal)
                {
                    counts.Portals++;
                    continue;
                }

                Vector3 facing = Vector3.Transform(polygon.WindingNormal, cell.Orientation);
                Vector3 shading = Vector3.Transform(polygon.MeanVertexNormal, cell.Orientation);
                float up = MathF.Abs(facing.Z);
                if (up >= LevelMinimumUp)
                {
                    // The authored vertex normals say which way a level
                    // face is lit, which is which way the room is; the
                    // winding is the fallback when they say nothing.
                    float facesUp = MathF.Abs(shading.Z) > 0.001f ? shading.Z : facing.Z;
                    if (facesUp > 0f)
                    {
                        counts.Floors++;
                        layer.Floors.Add(ProjectFloor(world));
                    }
                    else
                    {
                        counts.Ceilings++;
                    }
                }
                else if (up <= StandingMaximumUp)
                {
                    if (TryProjectWall(world, facing, out WallSpan span))
                    {
                        counts.Walls++;
                        layer.Walls.Add(span);
                    }
                }
                else
                {
                    counts.Slopes++;
                }
            }

            if (!cellBounds.IsEmpty)
            {
                bounds = bounds.Including(cellBounds.Min).Including(cellBounds.Max);
                cells.Add(new DungeonFloorplanCell(
                    cell.CellId,
                    (cellBounds.Min + cellBounds.Max) * 0.5f,
                    layerZ));
            }
        }

        var built = ImmutableArray.CreateBuilder<DungeonFloorplanLayer>(layers.Count);
        foreach ((float z, LayerBuilder layer) in layers)
        {
            ImmutableArray<DungeonFloorplanWall> walls = MergeWalls(layer.Walls);
            counts.WallsAfterMerge += walls.Length;
            built.Add(new DungeonFloorplanLayer(z, layer.Floors.ToImmutable(), walls));
        }

        return new DungeonFloorplan(
            landblock,
            built.MoveToImmutable(),
            cells.ToImmutable(),
            bounds,
            counts);
    }

    /// <summary>The band a cell at height <paramref name="z"/> is drawn in.</summary>
    internal static float LayerKey(float z) =>
        MathF.Floor((z + LayerOffset) / LayerHeight) * LayerHeight;

    /// <summary>
    /// Reads the landblock's cells with the lock held, and nothing more:
    /// every flattening happens after the lock is released. A structure
    /// seen before is not read again.
    /// </summary>
    private List<PlacedCell> ReadCells(uint landblock)
    {
        var placed = new List<PlacedCell>();
        var pendingStructures = new List<(uint Key, CellStruct Structure)>();
        var pendingCells = new List<(uint CellId, EnvCell Cell, uint Key)>();

        lock (_datLock)
        {
            LandBlockInfo? info = _dats.Get<LandBlockInfo>(landblock | 0xFFFEu);
            if (info is null || info.NumCells == 0)
                return placed;

            uint firstCell = landblock | 0x0100u;
            for (uint offset = 0; offset < info.NumCells; offset++)
            {
                uint cellId = firstCell + offset;
                EnvCell? envCell = _dats.Get<EnvCell>(cellId);
                if (envCell is null || envCell.EnvironmentId == 0 || envCell.Position is null)
                    continue;

                uint key = StructureKey(envCell.EnvironmentId, envCell.CellStructure);
                bool known;
                lock (_cacheGate)
                    known = _geometryByStructure.ContainsKey(key)
                        || pendingStructures.Exists(pending => pending.Key == key);
                if (!known)
                {
                    var environment = _dats.Get<DatReaderWriter.DBObjs.Environment>(
                        0x0D000000u | envCell.EnvironmentId);
                    if (environment is not null
                        && environment.Cells.TryGetValue(envCell.CellStructure, out CellStruct? structure))
                    {
                        pendingStructures.Add((key, structure));
                    }
                    else
                    {
                        pendingStructures.Add((key, null!));
                    }
                }
                pendingCells.Add((cellId, envCell, key));
            }
        }

        foreach ((uint key, CellStruct structure) in pendingStructures)
        {
            CellGeometry? geometry = structure is null ? null : CellGeometry.From(structure);
            lock (_cacheGate)
                _geometryByStructure.TryAdd(key, geometry);
        }

        foreach ((uint cellId, EnvCell envCell, uint key) in pendingCells)
        {
            CellGeometry? geometry;
            lock (_cacheGate)
                _geometryByStructure.TryGetValue(key, out geometry);
            if (geometry is null)
                continue;

            // A doorway is a polygon the structure lists as a portal, or one
            // this cell's own portal list points at; both are openings.
            var openings = new HashSet<ushort>(geometry.PortalPolygonIds);
            foreach (CellPortal portal in envCell.CellPortals)
                openings.Add(portal.PolygonId);
            CellGeometry withOpenings = openings.SetEquals(geometry.PortalPolygonIds)
                ? geometry
                : geometry.WithOpenings(openings);

            placed.Add(new PlacedCell(
                cellId,
                envCell.Position.Origin,
                envCell.Position.Orientation,
                withOpenings));
        }
        return placed;
    }

    private static uint StructureKey(ushort environmentId, ushort cellStructure) =>
        ((uint)environmentId << 16) | cellStructure;

    private static ImmutableArray<Vector2> ProjectFloor(Vector3[] world)
    {
        var flat = ImmutableArray.CreateBuilder<Vector2>(world.Length);
        foreach (Vector3 vertex in world)
            flat.Add(new Vector2(vertex.X, vertex.Y));
        return flat.MoveToImmutable();
    }

    /// <summary>
    /// A standing polygon seen from above is a line: the polygon's plane
    /// meets the ground along the direction across its normal, and the
    /// vertices reach some way along it either side of their middle.
    /// </summary>
    private static bool TryProjectWall(Vector3[] world, Vector3 facing, out WallSpan span)
    {
        span = default;
        var across = new Vector2(facing.X, facing.Y);
        float acrossLength = across.Length();
        if (acrossLength < 1e-6f)
            return false;
        across /= acrossLength;
        Vector2 along = Canonical(new Vector2(-across.Y, across.X));
        // With the direction made canonical the sideways normal is fixed
        // too, so two walls facing opposite ways along one line share a key.
        Vector2 sideways = new(-along.Y, along.X);

        float offset = 0f;
        float minimum = float.MaxValue;
        float maximum = float.MinValue;
        foreach (Vector3 vertex in world)
        {
            var flat = new Vector2(vertex.X, vertex.Y);
            offset += Vector2.Dot(flat, sideways);
            float t = Vector2.Dot(flat, along);
            minimum = MathF.Min(minimum, t);
            maximum = MathF.Max(maximum, t);
        }
        offset /= world.Length;
        if (maximum - minimum < MinimumWallMetres)
            return false;

        span = new WallSpan(along, offset, minimum, maximum);
        return true;
    }

    /// <summary>
    /// A direction and its opposite are the same line; pick the one pointing
    /// right, or up when it points straight up or down.
    /// </summary>
    private static Vector2 Canonical(Vector2 direction) =>
        direction.X > 1e-6f || (MathF.Abs(direction.X) <= 1e-6f && direction.Y > 0f)
            ? direction
            : -direction;

    /// <summary>
    /// Joins spans that lie on the same line and touch or overlap. Every
    /// polygon a wall is authored in becomes one line per straight run.
    /// </summary>
    private static ImmutableArray<DungeonFloorplanWall> MergeWalls(List<WallSpan> spans)
    {
        if (spans.Count == 0)
            return ImmutableArray<DungeonFloorplanWall>.Empty;

        var groups = new Dictionary<(int Angle, int Offset), List<WallSpan>>();
        foreach (WallSpan span in spans)
        {
            float degrees = MathF.Atan2(span.Along.Y, span.Along.X) * (180f / MathF.PI);
            if (degrees < 0f)
                degrees += 180f;
            int angleKey = (int)MathF.Round(degrees / MergeAngleDegrees) % (int)(180f / MergeAngleDegrees);
            int offsetKey = (int)MathF.Round(span.Offset / MergeOffsetMetres);
            if (!groups.TryGetValue((angleKey, offsetKey), out List<WallSpan>? group))
                groups[(angleKey, offsetKey)] = group = new List<WallSpan>();
            group.Add(span);
        }

        var walls = ImmutableArray.CreateBuilder<DungeonFloorplanWall>();
        foreach (List<WallSpan> group in groups.Values)
        {
            group.Sort(static (a, b) => a.Minimum.CompareTo(b.Minimum));
            Vector2 along = group[0].Along;
            float offset = 0f;
            foreach (WallSpan span in group)
                offset += span.Offset;
            offset /= group.Count;
            Vector2 sideways = new(-along.Y, along.X);
            Vector2 origin = sideways * offset;

            float runStart = group[0].Minimum;
            float runEnd = group[0].Maximum;
            for (int index = 1; index < group.Count; index++)
            {
                WallSpan span = group[index];
                if (span.Minimum <= runEnd + MergeGapMetres)
                {
                    runEnd = MathF.Max(runEnd, span.Maximum);
                    continue;
                }
                walls.Add(new DungeonFloorplanWall(
                    origin + along * runStart, origin + along * runEnd));
                runStart = span.Minimum;
                runEnd = span.Maximum;
            }
            walls.Add(new DungeonFloorplanWall(
                origin + along * runStart, origin + along * runEnd));
        }
        return walls.ToImmutable();
    }

    private sealed class LayerBuilder
    {
        public ImmutableArray<ImmutableArray<Vector2>>.Builder Floors { get; } =
            ImmutableArray.CreateBuilder<ImmutableArray<Vector2>>();

        public List<WallSpan> Walls { get; } = new();
    }

    /// <summary>A wall line before merging: its direction, its sideways offset from the origin, and its extent along the direction.</summary>
    private readonly record struct WallSpan(Vector2 Along, float Offset, float Minimum, float Maximum);

    private readonly record struct PlacedCell(
        uint CellId,
        Vector3 Origin,
        Quaternion Orientation,
        CellGeometry Geometry);

    /// <summary>One polygon of a structure, in the structure's own frame.</summary>
    private readonly record struct PolygonGeometry(
        ushort PolygonId,
        Vector3[] Vertices,
        Vector3 WindingNormal,
        Vector3 MeanVertexNormal,
        bool IsPortal);

    /// <summary>A cell structure's polygons, resolved to points and normals once.</summary>
    private sealed class CellGeometry
    {
        private CellGeometry(PolygonGeometry[] polygons, ImmutableHashSet<ushort> portalPolygonIds)
        {
            Polygons = polygons;
            PortalPolygonIds = portalPolygonIds;
        }

        public PolygonGeometry[] Polygons { get; }

        public ImmutableHashSet<ushort> PortalPolygonIds { get; }

        public static CellGeometry From(CellStruct structure)
        {
            var portals = ImmutableHashSet.CreateRange(structure.Portals);
            var polygons = new List<PolygonGeometry>(structure.Polygons.Count);
            Dictionary<ushort, SWVertex> vertices = structure.VertexArray.Vertices;
            foreach ((ushort polygonId, Polygon polygon) in structure.Polygons)
            {
                if (polygon.VertexIds.Count < 3)
                    continue;
                var points = new Vector3[polygon.VertexIds.Count];
                var shading = Vector3.Zero;
                bool complete = true;
                for (int index = 0; index < points.Length; index++)
                {
                    if (!vertices.TryGetValue((ushort)polygon.VertexIds[index], out SWVertex? vertex))
                    {
                        complete = false;
                        break;
                    }
                    points[index] = vertex.Origin;
                    shading += vertex.Normal;
                }
                if (!complete)
                    continue;

                Vector3 winding = WindingNormal(points);
                if (winding == Vector3.Zero)
                    continue;
                float shadingLength = shading.Length();
                polygons.Add(new PolygonGeometry(
                    polygonId,
                    points,
                    winding,
                    shadingLength > 1e-6f ? shading / shadingLength : Vector3.Zero,
                    portals.Contains(polygonId)));
            }
            return new CellGeometry(polygons.ToArray(), portals);
        }

        public CellGeometry WithOpenings(HashSet<ushort> openings)
        {
            var polygons = new PolygonGeometry[Polygons.Length];
            for (int index = 0; index < polygons.Length; index++)
            {
                PolygonGeometry polygon = Polygons[index];
                polygons[index] = polygon with { IsPortal = openings.Contains(polygon.PolygonId) };
            }
            return new CellGeometry(polygons, openings.ToImmutableHashSet());
        }

        /// <summary>
        /// The polygon's normal from its vertex order, summed over every
        /// edge so a bent or many-sided polygon still gives its overall
        /// facing.
        /// </summary>
        private static Vector3 WindingNormal(Vector3[] points)
        {
            var normal = Vector3.Zero;
            for (int index = 0; index < points.Length; index++)
            {
                Vector3 current = points[index];
                Vector3 next = points[(index + 1) % points.Length];
                normal.X += (current.Y - next.Y) * (current.Z + next.Z);
                normal.Y += (current.Z - next.Z) * (current.X + next.X);
                normal.Z += (current.X - next.X) * (current.Y + next.Y);
            }
            float length = normal.Length();
            return length < 1e-9f ? Vector3.Zero : normal / length;
        }
    }
}
