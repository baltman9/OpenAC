using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.World;
using AcDream.App.Rendering.Gpu;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Rendering;

internal sealed class PluginWorldLineRenderer(
    PluginWorldLineStore store,
    DebugLineRenderer lines,
    IWorldFrameCameraSource camera,
    LiveWorldOriginState origin,
    AcDream.Core.Physics.PhysicsEngine physics)
{
    private long _nextDiagnostic;

    public void Render(IGpuPassEncoder encoder, int width, int height)
    {
        if (!origin.IsKnown || width <= 0 || height <= 0) return;
        WorldCameraFrame frame = camera.Resolve();
        long now = Environment.TickCount64;
        bool diagnose = now >= _nextDiagnostic;
        if (diagnose) _nextDiagnostic = now + 5000;
        float nearestDistance = float.MaxValue;
        Vector3 nearestPoint = default;
        bool nearestFollow = false;
        int drawn = 0;
        int submitted = 0;
        lines.Begin();
        foreach (PluginWorldLine line in store.Lines)
        {
            submitted++;
            Vector3 start = Project(line.Start);
            Vector3 end = Project(line.End);
            if (!float.IsFinite(start.X + start.Y + start.Z + end.X + end.Y + end.Z))
                continue;
            float distance = Vector3.DistanceSquared(start, frame.Position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestPoint = start;
                nearestFollow = line.FollowTerrain;
            }
            if (Vector3.DistanceSquared(start, frame.Position) > 250f * 250f
                && Vector3.DistanceSquared(end, frame.Position) > 250f * 250f)
                continue;
            drawn++;
            var color = new Vector3(
                (line.ColorRgb >> 16) & 255, (line.ColorRgb >> 8) & 255, line.ColorRgb & 255) / 255f;
            float thickness = float.IsFinite(line.WidthMeters)
                ? Math.Clamp(line.WidthMeters, 0.01f, 2f) : 0.25f;
            int steps = line.FollowTerrain
                ? Math.Clamp((int)MathF.Ceiling(Vector2.Distance(new(start.X, start.Y), new(end.X, end.Y)) / 0.75f), 1, 1024)
                : 1;
            Vector3 previous = Ground(start, line.FollowTerrain);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 next = Ground(Vector3.Lerp(start, end, (float)i / steps), line.FollowTerrain);
                DrawBar(previous, next, color, thickness);
                previous = next;
            }
        }
        lines.FlushWorld(encoder, frame.ViewProjection, width, height);
        if (diagnose)
            Console.WriteLine($"[nav-lines] submitted={submitted} drawn={drawn} origin={origin.CenterX:X2}{origin.CenterY:X2} camera={frame.Position} nearest={nearestPoint} distance={MathF.Sqrt(nearestDistance):F2} follow={nearestFollow} terrainZ={(submitted == 0 ? null : physics.SampleTerrainZ(nearestPoint.X, nearestPoint.Y))}");
    }

    private Vector3 Ground(Vector3 point, bool follow)
    {
        if (follow && physics.SampleTerrainZ(point.X, point.Y) is { } z)
            // Outdoor routes can cross raised geometry above the heightmap.
            // Preserve their recorded height; terrain only lifts a buried line.
            point.Z = MathF.Max(point.Z, z + 0.05f);
        return point;
    }

    private void DrawBar(Vector3 start, Vector3 end, Vector3 color, float thickness)
    {
        Vector3 direction = end - start;
        if (direction.LengthSquared() < 0.000001f) return;
        direction = Vector3.Normalize(direction);
        Vector3 side = Vector3.Cross(direction, Vector3.UnitZ);
        if (side.LengthSquared() < 0.000001f) side = Vector3.UnitX;
        side = Vector3.Normalize(side) * (thickness * 0.5f);
        Vector3 up = Vector3.Normalize(Vector3.Cross(side, direction)) * (thickness * 0.5f);
        Span<Vector3> vertices = stackalloc Vector3[8]
        {
            start - side - up, start + side - up, start + side + up, start - side + up,
            end - side - up, end + side - up, end + side + up, end - side + up,
        };
        Quad(vertices[0], vertices[1], vertices[2], vertices[3], color);
        Quad(vertices[4], vertices[5], vertices[6], vertices[7], color);
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            Quad(vertices[i], vertices[next], vertices[next + 4], vertices[i + 4], color);
        }
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color)
    {
        lines.AddTriangle(a, b, c, color);
        lines.AddTriangle(a, c, d, color);
    }

    private Vector3 Project(PluginNavigationPosition p) => new(
        (float)(p.EastWest * 240d + (127 - origin.CenterX) * 192d + 84d),
        (float)(p.NorthSouth * 240d + (127 - origin.CenterY) * 192d + 84d),
        (float)(p.Elevation * 240d));
}
