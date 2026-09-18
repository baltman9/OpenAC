using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace AcDream.Runtime.Gameplay;

public enum NavigationPathStatus
{
    Complete,
    InvalidPosition,
    StartOutsideMesh,
    GoalOutsideMesh,
    Unreachable,
    CapacityExceeded,
    Unavailable,
    MissingTiles,
    CorruptTiles,
    Loading,
    SearchLimitReached,
}

public readonly record struct NavigationPathResult(
    NavigationPathStatus Status, IReadOnlyList<Vector3> Corners);

/// <summary>Bounded path queries over a published mesh; positions use world Z-up metres.</summary>
internal sealed class NavigationPathQuery
{
    private const int Capacity = 2048;
    private readonly DtNavMeshQuery _query;
    private readonly DtNavMesh _mesh;
    private readonly DtQueryDefaultFilter _filter = new();
    private readonly long[] _corridor = new long[Capacity];
    private readonly DtStraightPath[] _straight = new DtStraightPath[Capacity];

    public NavigationPathQuery(DtNavMesh mesh)
    {
        _mesh = mesh;
        _query = new DtNavMeshQuery(mesh);
    }

    public IReadOnlyList<(Vector3 Start, Vector3 End)> CaptureEdges(Vector3 center)
    {
        var edges = new List<(Vector3, Vector3)>();
        for (int tileIndex = 0; tileIndex < _mesh.GetMaxTiles(); tileIndex++)
        {
            var data = _mesh.GetTile(tileIndex)?.data;
            if (data?.polys is null) continue;
            foreach (DtPoly polygon in data.polys)
            {
                if (polygon.vertCount < 3 || polygon.flags == 0) continue;
                for (int edge = 0; edge < polygon.vertCount; edge++)
                {
                    int a = polygon.verts[edge] * 3;
                    int b = polygon.verts[(edge + 1) % polygon.vertCount] * 3;
                    var start = new Vector3(data.verts[a], data.verts[a + 2], data.verts[a + 1]);
                    var end = new Vector3(data.verts[b], data.verts[b + 2], data.verts[b + 1]);
                    var midpoint = (start + end) * 0.5f;
                    if (Vector2.DistanceSquared(new(midpoint.X, midpoint.Y), new(center.X, center.Y)) > 10000f)
                        continue;
                    edges.Add((start, end));
                    if (edges.Count >= 12000) return edges;
                }
            }
        }
        return edges;
    }

    public NavigationPathResult Find(Vector3 start, Vector3 goal,
        float horizontalTolerance, float verticalTolerance)
    {
        if (!Finite(start) || !Finite(goal) || !float.IsFinite(horizontalTolerance)
            || !float.IsFinite(verticalTolerance) || horizontalTolerance <= 0f || verticalTolerance <= 0f)
            return Failed(NavigationPathStatus.InvalidPosition);
        var extents = new RcVec3f(horizontalTolerance, verticalTolerance, horizontalTolerance);
        var startStatus = _query.FindNearestPoly(ToMesh(start), extents, _filter,
            out long startRef, out RcVec3f projectedStart, out _);
        if (!startStatus.Succeeded() || startRef == 0)
            return Failed(NavigationPathStatus.StartOutsideMesh);
        var goalStatus = _query.FindNearestPoly(ToMesh(goal), extents, _filter,
            out long goalRef, out RcVec3f projectedGoal, out _);
        if (!goalStatus.Succeeded() || goalRef == 0)
            return Failed(NavigationPathStatus.GoalOutsideMesh);

        var pathStatus = _query.FindPath(startRef, goalRef, projectedStart, projectedGoal,
            _filter, _corridor, out int count, Capacity);
        if (count >= Capacity)
            return Failed(NavigationPathStatus.CapacityExceeded);
        if (!pathStatus.Succeeded() || count == 0 || _corridor[count - 1] != goalRef)
            return Failed(NavigationPathStatus.Unreachable);
        var straightStatus = _query.FindStraightPath(projectedStart, projectedGoal,
            _corridor.AsSpan(0, count), count, _straight, out int corners, Capacity, 0);
        if (corners >= Capacity)
            return Failed(NavigationPathStatus.CapacityExceeded);
        if (!straightStatus.Succeeded() || corners == 0)
            return Failed(NavigationPathStatus.Unreachable);
        // A truncated corridor must never be presented as arrival at the goal.
        RcVec3f last = _straight[corners - 1].pos;
        if (Math.Abs(last.X - projectedGoal.X) > 0.01f
            || Math.Abs(last.Y - projectedGoal.Y) > 0.01f
            || Math.Abs(last.Z - projectedGoal.Z) > 0.01f)
            return Failed(NavigationPathStatus.Unreachable);
        var result = new Vector3[corners];
        for (int index = 0; index < corners; index++)
        {
            RcVec3f point = _straight[index].pos;
            result[index] = new Vector3(point.X, point.Z, point.Y);
        }
        return new NavigationPathResult(NavigationPathStatus.Complete, result);
    }

    private static RcVec3f ToMesh(Vector3 value) => new(value.X, value.Z, value.Y);
    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static NavigationPathResult Failed(NavigationPathStatus status) => new(status, []);
}
