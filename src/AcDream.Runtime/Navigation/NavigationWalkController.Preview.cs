using System.Numerics;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Navigation;

internal sealed partial class NavigationWalkController
{
    internal Task<PluginNavigationPlan> PreviewPathAsync(uint objectId, float arrivalMeters)
    {
        if (!_goals.TryLocate(objectId, out Vector3 goal))
            return PreviewResult(PluginNavigationPlanStatus.NoRoute, "the client has no position for the object");
        float radius = _goals.TryGetSurfaces(objectId, out NavSurfaces surfaces)
            ? RadiusOf(surfaces, goal) : 0f;
        return PreviewPathAsync(goal, objectId, arrivalMeters, onGoalFloor: false, radius);
    }

    internal Task<PluginNavigationPlan> PreviewPathAsync(uint cellId, Vector3 local, float arrivalMeters)
    {
        if (!_goals.TryLocatePlace(cellId, local, out Vector3 goal))
            return PreviewResult(PluginNavigationPlanStatus.NoRoute, "the client cannot place the destination");
        return PreviewPathAsync(goal, 0u, arrivalMeters, onGoalFloor: true, 0f);
    }

    private Task<PluginNavigationPlan> PreviewPathAsync(
        Vector3 goal, uint objectId, float arrivalMeters, bool onGoalFloor, float goalRadius)
    {
        if (!_body.TrySample(out NavigationWalkBodySample sample) || sample.InPortalSpace || sample.Airborne
            || !_goals.TryGlobalOf(sample.Position, out Vector3 globalStart))
            return PreviewResult(PluginNavigationPlanStatus.Unavailable, "the character is not standing in the world");

        bool dungeon = TryMeasureDungeon(sample.CellId, out uint dungeonId, out Vector2 minimum, out Vector2 maximum);
        bool region = dungeon
            ? TryChooseSquare(
                Vector2.Min(minimum, Vector2.Min(Flat(sample.Position), Flat(goal))),
                Vector2.Max(maximum, Vector2.Max(Flat(sample.Position), Flat(goal))),
                MaximumDungeonRegion, out float originX, out float originY, out float size)
            : TryChooseRegion(sample.Position, goal, out originX, out originY, out size);
        if (!region)
            return PreviewResult(PluginNavigationPlanStatus.NoRoute, "the destination is outside one planning region");

        NavGeometry? geometry = dungeon
            ? NavGeometry.CaptureDungeon(_physics, dungeonId, originX, originY, size, _goals.StandsStill)
            : NavGeometry.Capture(_physics, originX, originY, size, _goals.StandsStill);
        if (geometry is null)
            return PreviewResult(PluginNavigationPlanStatus.NoRoute, "no collision is loaded around the character");

        // Cell surfaces are immutable once loaded. Keep their identifiers with the
        // captured geometry so the worker can label indoor path points by floor.
        var cells = new List<CellSurface>();
        foreach (uint landblockId in geometry.LandblockIds)
        {
            if (_physics.TryGetLandblockCollision(landblockId, out _, out IReadOnlyList<CellSurface> found, out _))
                cells.AddRange(found);
        }
        NavAvoidance[] crowd = Crowd(sample, objectId);
        NavBody body = sample.Body;
        Vector3 start = sample.Position;
        NavLeapAbility? leaps = sample.Leaps;
        float half = size * 0.5f;
        var centre = new Vector3(originX + half, originY + half, 0f);
        float reach = half * MathF.Sqrt(2f);
        NavAvoidance[] portals = _goals.FindPortals(centre, reach, objectId, goal)
            .Select(spot => spot with { Radius = spot.Radius + body.Radius }).ToArray();
        NavAvoidance[] obstacles = _goals.FindObstacles(centre, reach, objectId, geometry.ObjectIds)
            .Select(spot => spot with { Radius = spot.Radius + body.Radius }).ToArray();
        return Task.Run(() =>
        {
            try
            {
                NavGrid grid = NavGrid.Build(geometry, body);
                NavRoute route = AroundObstacles(goal, portals, obstacles,
                    spots => NavRouter.Find(grid, start, goal, PlanningRadius(arrivalMeters),
                        spots, leaps, crowd, onGoalFloor, goalRadius));
                if (route.Outcome != NavRouteOutcome.Routed || !route.EndsInSight)
                    return new PluginNavigationPlan(PluginNavigationPlanStatus.NoRoute, [], route.Reason);
                PluginNavigationPosition[] path = route.Legs
                    .Select(point => PreviewPosition(point, start, globalStart, sample.CellId, cells, dungeon))
                    .ToArray();
                return new PluginNavigationPlan(PluginNavigationPlanStatus.Routed, path, route.Reason)
                {
                    LengthMeters = route.Length,
                };
            }
            catch (Exception error)
            {
                return new PluginNavigationPlan(PluginNavigationPlanStatus.Failed, [],
                    $"the search failed: {error.Message}");
            }
        });
    }

    private static PluginNavigationPosition PreviewPosition(
        Vector3 point, Vector3 start, Vector3 globalStart, uint startCell,
        IReadOnlyList<CellSurface> cells, bool sealedDungeon)
    {
        Vector3 global = globalStart + point - start;
        uint cellId = 0u;
        float nearest = float.PositiveInfinity;
        foreach (CellSurface cell in cells)
        {
            float? floor = cell.SampleFloorZ(point.X, point.Y);
            if (floor is not { } z || MathF.Abs(z - point.Z) >= nearest)
                continue;
            nearest = MathF.Abs(z - point.Z);
            cellId = cell.CellId;
        }
        if (cellId == 0u && sealedDungeon)
            cellId = startCell;
        if (cellId == 0u)
        {
            int bx = Math.Clamp((int)MathF.Floor(global.X / 192f), 0, 255);
            int by = Math.Clamp((int)MathF.Floor(global.Y / 192f), 0, 255);
            cellId = TerrainSurface.ComputeOutdoorCellId((uint)((bx << 24) | (by << 16)),
                global.X - bx * 192f, global.Y - by * 192f);
        }
        return RuntimeNavigationProjection.GlobalPosition(cellId, global);
    }

    private static Task<PluginNavigationPlan> PreviewResult(PluginNavigationPlanStatus status, string reason) =>
        Task.FromResult(new PluginNavigationPlan(status, [], reason));
}
