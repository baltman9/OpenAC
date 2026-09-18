using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.Content.Navigation;

/// <summary>
/// One already-prepared landblock participating in a target landblock's
/// navigation build. Entity positions may use a captured runtime origin;
/// <see cref="EntityToAbsoluteOffset"/> translates them into canonical AC
/// world coordinates.
/// </summary>
public sealed record NavigationGeometryLandblockInput(
    LoadedLandblock Landblock,
    TerrainSurface Terrain,
    IReadOnlyList<WorldEntity> HydratedEntities,
    LandblockCollisionBuild Collisions,
    Vector3 EntityToAbsoluteOffset);

/// <summary>
/// Builds navigation input from the same prepared collision assets used by
/// gameplay physics. The target plus its one-landblock halo are emitted as a
/// single world-space triangle soup; tile clipping belongs to the baker.
/// </summary>
public static partial class NavigationGeometryBuilder
{
    private const float LandblockLength = 192f;

    public static NavigationGeometryChunk Build(
        uint canonicalLandblockId,
        IEnumerable<NavigationGeometryLandblockInput> halo,
        CancellationToken cancellationToken = default)
    {
        NavigationLandblockContract.Validate(canonicalLandblockId);
        ArgumentNullException.ThrowIfNull(halo);

        NavigationGeometryLandblockInput[] inputs = halo.ToArray();
        ValidateInputs(canonicalLandblockId, inputs);

        var output = new TriangleSoup();
        foreach (NavigationGeometryLandblockInput input in
            inputs.OrderBy(static input => input.Landblock.LandblockId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddTerrain(input, output, cancellationToken);
            AddStaticCollision(input, output, cancellationToken);
            AddEnvCells(input, output, cancellationToken);
        }

        return new NavigationGeometryChunk(
            canonicalLandblockId,
            output.Vertices,
            output.Triangles);
    }

    private static void ValidateInputs(
        uint canonicalLandblockId,
        IReadOnlyList<NavigationGeometryLandblockInput> inputs)
    {
        int targetX = (int)(canonicalLandblockId >> 24);
        int targetY = (int)((canonicalLandblockId >> 16) & 0xFFu);
        var landblocks = new HashSet<uint>();
        bool containsTarget = false;

        for (int index = 0; index < inputs.Count; index++)
        {
            NavigationGeometryLandblockInput input = inputs[index]
                ?? throw new ArgumentException(
                    $"navigation halo input {index} is null",
                    nameof(inputs));
            ArgumentNullException.ThrowIfNull(input.Landblock);
            ArgumentNullException.ThrowIfNull(input.Terrain);
            ArgumentNullException.ThrowIfNull(input.HydratedEntities);
            ArgumentNullException.ThrowIfNull(input.Collisions);

            uint landblockId = input.Landblock.LandblockId;
            NavigationLandblockContract.Validate(landblockId);
            if (!landblocks.Add(landblockId))
            {
                throw new ArgumentException(
                    $"navigation halo repeats landblock 0x{landblockId:X8}",
                    nameof(inputs));
            }

            int x = (int)(landblockId >> 24);
            int y = (int)((landblockId >> 16) & 0xFFu);
            if (Math.Abs(x - targetX) > 1 || Math.Abs(y - targetY) > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inputs),
                    landblockId,
                    $"landblock 0x{landblockId:X8} lies outside the target halo");
            }

            containsTarget |= landblockId == canonicalLandblockId;
            if (!IsFinite(input.EntityToAbsoluteOffset))
            {
                throw new ArgumentException(
                    $"navigation halo offset {index} is not finite",
                    nameof(inputs));
            }
        }

        if (!containsTarget)
        {
            throw new ArgumentException(
                $"navigation halo does not contain target 0x{canonicalLandblockId:X8}",
                nameof(inputs));
        }
    }

    private static void AddTerrain(
        NavigationGeometryLandblockInput input,
        TriangleSoup output,
        CancellationToken cancellationToken)
    {
        Vector3 origin = CanonicalOrigin(input.Landblock.LandblockId);
        ReadOnlySpan<Vector2> samples =
        [
            new(0.25f, 0.25f),
            new(0.75f, 0.25f),
            new(0.75f, 0.75f),
            new(0.25f, 0.75f),
        ];

        for (int cellX = 0; cellX < TerrainSurface.CellsPerSide; cellX++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int cellY = 0; cellY < TerrainSurface.CellsPerSide; cellY++)
            {
                var emitted = new HashSet<TerrainTriangleVertices>();
                for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
                {
                    Vector2 sample = samples[sampleIndex];
                    TerrainTriangleVertices triangle = input.Terrain
                        .SampleSurfacePolygon(
                            (cellX + sample.X) * TerrainSurface.CellSize,
                            (cellY + sample.Y) * TerrainSurface.CellSize)
                        .Vertices;
                    if (!emitted.Add(triangle))
                        continue;

                    output.AddTriangle(
                        triangle.V0 + origin,
                        triangle.V1 + origin,
                        triangle.V2 + origin,
                        NavigationTriangleSource.Terrain);
                }

                if (emitted.Count != 2)
                {
                    throw new InvalidDataException(
                        $"terrain cell ({cellX},{cellY}) in landblock "
                        + $"0x{input.Landblock.LandblockId:X8} did not resolve to two triangles");
                }
            }
        }
    }

    private static void AddStaticCollision(
        NavigationGeometryLandblockInput input,
        TriangleSoup output,
        CancellationToken cancellationToken)
    {
        foreach (WorldEntity entity in
            input.HydratedEntities.OrderBy(static entity => entity.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(entity);
            Matrix4x4 entityTransform =
                Matrix4x4.CreateFromQuaternion(entity.Rotation)
                * Matrix4x4.CreateTranslation(
                    entity.Position + input.EntityToAbsoluteOffset);
            NavigationTriangleSource polygonSource = entity.IsBuildingShell
                ? NavigationTriangleSource.Building
                : NavigationTriangleSource.StaticPhysics;
            bool hasPhysicsPart = false;

            for (int partIndex = 0;
                partIndex < entity.MeshRefs.Count;
                partIndex++)
            {
                MeshRef part = entity.MeshRefs[partIndex];
                if (!input.Collisions.GfxObjs.TryGetValue(
                        part.GfxObjId,
                        out FlatGfxObjCollisionAsset? asset))
                {
                    throw new InvalidDataException(
                        $"prepared GfxObj collision 0x{part.GfxObjId:X8} is missing "
                        + $"for entity 0x{entity.Id:X8}");
                }

                if (asset.PhysicsBsp.RootIndex < 0)
                    continue;

                hasPhysicsPart = true;
                Matrix4x4 transform = part.PartTransform * entityTransform;
                output.AddPolygonTable(
                    asset.PhysicsBsp.PolygonTable,
                    transform,
                    polygonSource);
            }

            if (hasPhysicsPart)
                continue;

            uint sourceType =
                entity.SourceGfxObjOrSetupId & 0xFF00_0000u;
            if (sourceType != 0x0200_0000u)
                continue;
            if (!input.Collisions.Setups.TryGetValue(
                    entity.SourceGfxObjOrSetupId,
                    out FlatSetupCollision? setup))
            {
                throw new InvalidDataException(
                    $"prepared Setup collision 0x{entity.SourceGfxObjOrSetupId:X8} "
                    + $"is missing for entity 0x{entity.Id:X8}");
            }

            NavigationTriangleSource proxySource = entity.IsBuildingShell
                ? NavigationTriangleSource.Building
                : NavigationTriangleSource.SetupProxy;
            AddSetupProxies(setup, entity.Scale, entityTransform, proxySource, output);
        }
    }

    private static void AddEnvCells(
        NavigationGeometryLandblockInput input,
        TriangleSoup output,
        CancellationToken cancellationToken)
    {
        PhysicsDatBundle dats =
            input.Landblock.PhysicsDats ?? PhysicsDatBundle.Empty;
        Vector3 origin = CanonicalOrigin(input.Landblock.LandblockId);
        foreach (var pair in dats.EnvCells.OrderBy(static pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint envCellId = pair.Key;
            var envCell = pair.Value;
            if (!input.Collisions.CellStructures.TryGetValue(
                    envCellId,
                    out FlatCellStructureCollisionAsset? structure))
            {
                throw new InvalidDataException(
                    $"prepared CellStruct collision 0x{envCellId:X8} is missing");
            }
            if (!input.Collisions.EnvCells.TryGetValue(
                    envCellId,
                    out FlatEnvCellTopology? topology)
                || topology is null)
            {
                throw new InvalidDataException(
                    $"prepared EnvCell topology 0x{envCellId:X8} is missing");
            }

            Vector3 cellOrigin = envCell.Position.Origin + origin;
            Matrix4x4 transform =
                Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation)
                * Matrix4x4.CreateTranslation(cellOrigin);
            output.AddPolygonTable(
                structure.PhysicsBsp.PolygonTable,
                transform,
                NavigationTriangleSource.EnvCell);
        }
    }

    private static Vector3 CanonicalOrigin(uint canonicalLandblockId) =>
        new(
            (canonicalLandblockId >> 24) * LandblockLength,
            ((canonicalLandblockId >> 16) & 0xFFu) * LandblockLength,
            0f);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private sealed class TriangleSoup
    {
        private readonly Dictionary<NavigationVertex, int> _indices = [];
        private readonly List<NavigationVertex> _vertices = [];
        private readonly List<NavigationTriangle> _triangles = [];

        public IReadOnlyList<NavigationVertex> Vertices => _vertices;

        public IReadOnlyList<NavigationTriangle> Triangles => _triangles;

        public void AddPolygonTable(
            FlatPolygonTable table,
            Matrix4x4 transform,
            NavigationTriangleSource source)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (!MatrixFinite(transform))
                throw new InvalidDataException("navigation geometry transform is not finite");

            for (int polygonIndex = 0;
                polygonIndex < table.Polygons.Length;
                polygonIndex++)
            {
                FlatIndexRange range =
                    table.Polygons[polygonIndex].VertexRange;
                if (range.Count < 3)
                    continue;

                Vector3 first = Vector3.Transform(
                    table.Vertices[range.Start],
                    transform);
                Vector3 previous = Vector3.Transform(
                    table.Vertices[range.Start + 1],
                    transform);
                for (int vertexOffset = 2;
                    vertexOffset < range.Count;
                    vertexOffset++)
                {
                    Vector3 current = Vector3.Transform(
                        table.Vertices[range.Start + vertexOffset],
                        transform);
                    AddTriangle(first, previous, current, source);
                    previous = current;
                }
            }
        }

        public void AddTriangle(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            NavigationTriangleSource source)
        {
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
                throw new InvalidDataException("navigation triangle contains a non-finite vertex");
            if (Vector3.Cross(b - a, c - a).LengthSquared() <= 1e-12f)
                return;

            int aIndex = GetVertex(a);
            int bIndex = GetVertex(b);
            int cIndex = GetVertex(c);
            if (aIndex == bIndex || bIndex == cIndex || cIndex == aIndex)
                return;
            _triangles.Add(new NavigationTriangle(aIndex, bIndex, cIndex, source));
        }

        private int GetVertex(Vector3 value)
        {
            var vertex = new NavigationVertex(value.X, value.Y, value.Z);
            if (_indices.TryGetValue(vertex, out int index))
                return index;

            index = _vertices.Count;
            _vertices.Add(vertex);
            _indices.Add(vertex, index);
            return index;
        }

        private static bool MatrixFinite(Matrix4x4 value) =>
            float.IsFinite(value.M11)
            && float.IsFinite(value.M12)
            && float.IsFinite(value.M13)
            && float.IsFinite(value.M14)
            && float.IsFinite(value.M21)
            && float.IsFinite(value.M22)
            && float.IsFinite(value.M23)
            && float.IsFinite(value.M24)
            && float.IsFinite(value.M31)
            && float.IsFinite(value.M32)
            && float.IsFinite(value.M33)
            && float.IsFinite(value.M34)
            && float.IsFinite(value.M41)
            && float.IsFinite(value.M42)
            && float.IsFinite(value.M43)
            && float.IsFinite(value.M44);
    }
}
