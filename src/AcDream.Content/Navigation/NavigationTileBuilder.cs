using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace AcDream.Content.Navigation;

/// <summary>Builds one Detour tile from a canonical landblock geometry halo.</summary>
public static class NavigationTileBuilder
{
    private const float LandblockLength = 192f;

    public static PreparedNavigationBuildParameters DefaultParameters { get; } =
        new(
            CellSize: 0.5f,
            CellHeight: 0.25f,
            AgentHeight: 1.8f,
            AgentRadius: 0.5f,
            AgentMaxClimb: 0.6f,
            AgentMaxSlopeDegrees: 50f,
            RegionMinSize: 8,
            RegionMergeSize: 20,
            EdgeMaxLength: 12f,
            EdgeMaxError: 1.3f,
            VerticesPerPolygon: 6,
            DetailSampleDistance: 3f,
            DetailSampleMaxError: 0.25f,
            TileSizeCells: 384,
            BorderSizeCells: 4,
            PartitionType: NavigationPartitionType.Watershed);

    public static PreparedNavigationTileSet Build(
        NavigationGeometryChunk geometry,
        PreparedNavigationBuildParameters? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        PreparedNavigationBuildParameters buildParameters =
            parameters ?? DefaultParameters;
        buildParameters.Validate();
        ValidateOneTilePerLandblock(buildParameters);
        cancellationToken.ThrowIfCancellationRequested();

        if (geometry.Vertices.Count == 0 || geometry.Triangles.Count == 0)
        {
            return Empty(geometry.CanonicalLandblockId, buildParameters);
        }

        float[] vertices = RemapVertices(geometry.Vertices);
        int[] triangles = RemapTriangles(geometry.Triangles);
        var input = new RcSampleInputGeomProvider(vertices, triangles);
        var config = CreateConfig(buildParameters);

        int landblockX = (int)(geometry.CanonicalLandblockId >> 24);
        int landblockY = (int)((geometry.CanonicalLandblockId >> 16) & 0xFFu);
        float minHeight = vertices[1];
        float maxHeight = minHeight;
        for (int index = 4; index < vertices.Length; index += 3)
        {
            minHeight = MathF.Min(minHeight, vertices[index]);
            maxHeight = MathF.Max(maxHeight, vertices[index]);
        }

        var coreMin = new RcVec3f(
            landblockX * LandblockLength,
            minHeight,
            landblockY * LandblockLength);
        var coreMax = new RcVec3f(
            (landblockX + 1) * LandblockLength,
            maxHeight + buildParameters.CellHeight,
            (landblockY + 1) * LandblockLength);
        var builderConfig = new RcBuilderConfig(config, coreMin, coreMax);
        RcBuilderResult result = new RcBuilder().Build(
            input,
            builderConfig,
            keepInterResults: false);
        cancellationToken.ThrowIfCancellationRequested();

        RcPolyMesh mesh = result.Mesh;
        if (mesh.npolys == 0)
            return Empty(geometry.CanonicalLandblockId, buildParameters);

        Array.Fill(mesh.flags, 1, 0, mesh.npolys);
        DtNavMeshCreateParams createParameters = CreateMeshParameters(
            mesh,
            result.MeshDetail,
            buildParameters,
            landblockX,
            landblockY);
        DtMeshData meshData = DtNavMeshBuilder.CreateNavMeshData(createParameters)
            ?? throw new InvalidDataException(
                $"Detour rejected navigation mesh data for landblock " +
                $"0x{geometry.CanonicalLandblockId:X8}");

        foreach (DtPoly polygon in meshData.polys)
        {
            polygon.flags = 1;
            if (polygon.GetArea() == RcRecast.RC_WALKABLE_AREA)
                polygon.SetArea(0);
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            new DtMeshDataWriter().Write(
                writer,
                meshData,
                RcByteOrder.LITTLE_ENDIAN,
                cCompatibility: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedNavigationTileSet(
            geometry.CanonicalLandblockId,
            buildParameters,
            new[] { new ReadOnlyMemory<byte>(stream.ToArray()) });
    }

    private static PreparedNavigationTileSet Empty(
        uint canonicalLandblockId,
        PreparedNavigationBuildParameters parameters) =>
        new(canonicalLandblockId, parameters, Array.Empty<ReadOnlyMemory<byte>>());

    private static float[] RemapVertices(IReadOnlyList<NavigationVertex> source)
    {
        var output = new float[checked(source.Count * 3)];
        for (int index = 0; index < source.Count; index++)
        {
            NavigationVertex vertex = source[index];
            int offset = index * 3;
            output[offset] = vertex.X;
            output[offset + 1] = vertex.Z;
            output[offset + 2] = vertex.Y;
        }
        return output;
    }

    private static int[] RemapTriangles(IReadOnlyList<NavigationTriangle> source)
    {
        var output = new int[checked(source.Count * 3)];
        for (int index = 0; index < source.Count; index++)
        {
            NavigationTriangle triangle = source[index];
            int offset = index * 3;
            output[offset] = triangle.A;
            output[offset + 1] = triangle.C;
            output[offset + 2] = triangle.B;
        }
        return output;
    }

    private static RcConfig CreateConfig(PreparedNavigationBuildParameters parameters) =>
        new(
            useTiles: true,
            tileSizeX: parameters.TileSizeCells,
            tileSizeZ: parameters.TileSizeCells,
            borderSize: parameters.BorderSizeCells,
            partition: parameters.PartitionType switch
            {
                NavigationPartitionType.Watershed => RcPartition.WATERSHED,
                NavigationPartitionType.Monotone => RcPartition.MONOTONE,
                NavigationPartitionType.Layers => RcPartition.LAYERS,
                _ => throw new ArgumentOutOfRangeException(nameof(parameters)),
            },
            cellSize: parameters.CellSize,
            cellHeight: parameters.CellHeight,
            agentMaxSlope: parameters.AgentMaxSlopeDegrees,
            agentHeight: parameters.AgentHeight,
            agentRadius: parameters.AgentRadius,
            agentMaxClimb: parameters.AgentMaxClimb,
            minRegionArea: parameters.RegionMinSize * parameters.RegionMinSize
                * parameters.CellSize * parameters.CellSize,
            mergeRegionArea: parameters.RegionMergeSize * parameters.RegionMergeSize
                * parameters.CellSize * parameters.CellSize,
            edgeMaxLen: parameters.EdgeMaxLength,
            edgeMaxError: parameters.EdgeMaxError,
            vertsPerPoly: parameters.VerticesPerPolygon,
            detailSampleDist: parameters.DetailSampleDistance / parameters.CellSize,
            detailSampleMaxError: parameters.DetailSampleMaxError / parameters.CellHeight,
            filterLowHangingObstacles: true,
            filterLedgeSpans: true,
            filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(RcRecast.RC_WALKABLE_AREA),
            buildMeshDetail: true);

    private static DtNavMeshCreateParams CreateMeshParameters(
        RcPolyMesh mesh,
        RcPolyMeshDetail? detail,
        PreparedNavigationBuildParameters parameters,
        int landblockX,
        int landblockY) =>
        new()
        {
            verts = mesh.verts,
            vertCount = mesh.nverts,
            polys = mesh.polys,
            polyAreas = mesh.areas,
            polyFlags = mesh.flags,
            polyCount = mesh.npolys,
            nvp = mesh.nvp,
            detailMeshes = detail?.meshes ?? Array.Empty<int>(),
            detailVerts = detail?.verts ?? Array.Empty<float>(),
            detailVertsCount = detail?.nverts ?? 0,
            detailTris = detail?.tris ?? Array.Empty<int>(),
            detailTriCount = detail?.ntris ?? 0,
            offMeshConVerts = Array.Empty<float>(),
            offMeshConRad = Array.Empty<float>(),
            offMeshConFlags = Array.Empty<int>(),
            offMeshConAreas = Array.Empty<int>(),
            offMeshConDir = Array.Empty<int>(),
            offMeshConUserID = Array.Empty<int>(),
            offMeshConCount = 0,
            tileX = landblockX,
            tileZ = landblockY,
            tileLayer = 0,
            bmin = mesh.bmin,
            bmax = mesh.bmax,
            walkableHeight = parameters.AgentHeight,
            walkableRadius = parameters.AgentRadius,
            walkableClimb = parameters.AgentMaxClimb,
            cs = mesh.cs,
            ch = mesh.ch,
            buildBvTree = true,
        };

    private static void ValidateOneTilePerLandblock(
        PreparedNavigationBuildParameters parameters)
    {
        if (MathF.Abs(
                parameters.CellSize * parameters.TileSizeCells
                - LandblockLength) > 0.0001f)
        {
            throw new ArgumentException(
                "navigation baking requires exactly one tile per landblock",
                nameof(parameters));
        }
    }
}
