using System.Collections.ObjectModel;

namespace AcDream.Content.Navigation;

public enum NavigationTriangleSource : byte
{
    Terrain,
    StaticPhysics,
    SetupProxy,
    Building,
    EnvCell,
}

public readonly record struct NavigationVertex(float X, float Y, float Z)
{
    internal bool IsFinite =>
        float.IsFinite(X)
        && float.IsFinite(Y)
        && float.IsFinite(Z);
}

public readonly record struct NavigationTriangle(
    int A,
    int B,
    int C,
    NavigationTriangleSource Source);

public sealed class NavigationGeometryChunk
{
    private readonly ReadOnlyCollection<NavigationVertex> _vertices;
    private readonly ReadOnlyCollection<NavigationTriangle> _triangles;

    public NavigationGeometryChunk(
        uint canonicalLandblockId,
        IEnumerable<NavigationVertex> vertices,
        IEnumerable<NavigationTriangle> triangles)
    {
        NavigationLandblockContract.Validate(canonicalLandblockId);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangles);

        NavigationVertex[] vertexArray = vertices.ToArray();
        NavigationTriangle[] triangleArray = triangles.ToArray();
        Validate(vertexArray, triangleArray);

        CanonicalLandblockId = canonicalLandblockId;
        _vertices = Array.AsReadOnly(vertexArray);
        _triangles = Array.AsReadOnly(triangleArray);
    }

    public uint CanonicalLandblockId { get; }

    public IReadOnlyList<NavigationVertex> Vertices => _vertices;

    public IReadOnlyList<NavigationTriangle> Triangles => _triangles;

    private static void Validate(
        IReadOnlyList<NavigationVertex> vertices,
        IReadOnlyList<NavigationTriangle> triangles)
    {
        for (int index = 0; index < vertices.Count; index++)
        {
            if (!vertices[index].IsFinite)
            {
                throw new ArgumentException(
                    $"navigation vertex {index} is not finite",
                    nameof(vertices));
            }
        }

        for (int index = 0; index < triangles.Count; index++)
        {
            NavigationTriangle triangle = triangles[index];
            if (!Enum.IsDefined(triangle.Source))
            {
                throw new ArgumentException(
                    $"navigation triangle {index} has an unknown source",
                    nameof(triangles));
            }

            if ((uint)triangle.A >= (uint)vertices.Count
                || (uint)triangle.B >= (uint)vertices.Count
                || (uint)triangle.C >= (uint)vertices.Count)
            {
                throw new ArgumentException(
                    $"navigation triangle {index} references a missing vertex",
                    nameof(triangles));
            }

            if (triangle.A == triangle.B
                || triangle.B == triangle.C
                || triangle.C == triangle.A)
            {
                throw new ArgumentException(
                    $"navigation triangle {index} repeats a vertex index",
                    nameof(triangles));
            }
        }
    }
}

internal static class NavigationLandblockContract
{
    public static void Validate(uint canonicalLandblockId)
    {
        if ((canonicalLandblockId & 0x0000_FFFFu) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalLandblockId),
                canonicalLandblockId,
                "navigation landblock ids must have a zero cell suffix");
        }
    }
}
