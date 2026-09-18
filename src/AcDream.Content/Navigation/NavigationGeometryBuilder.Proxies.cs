using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Content.Navigation;

public static partial class NavigationGeometryBuilder
{
    private const int ProxySides = 16;
    private const int SphereLatitudeBands = 8;

    private static void AddSetupProxies(
        FlatSetupCollision setup,
        float entityScale,
        Matrix4x4 entityTransform,
        NavigationTriangleSource source,
        TriangleSoup output)
    {
        float scale = entityScale > 0f ? entityScale : 1f;
        for (int index = 0; index < setup.Cylinders.Length; index++)
        {
            FlatCollisionCylinder cylinder = setup.Cylinders[index];
            float radius = cylinder.Radius * scale;
            float baseHeight = cylinder.Height > 0f
                ? cylinder.Height
                : cylinder.Radius * 4f;
            float height = baseHeight * scale;
            if (radius <= 0f || height <= 0f)
                continue;

            AddCylinder(
                cylinder.Origin * scale,
                radius,
                height,
                entityTransform,
                source,
                output);
        }

        if (setup.Cylinders.Length != 0)
            return;

        for (int index = 0; index < setup.Spheres.Length; index++)
        {
            FlatCollisionSphere sphere = setup.Spheres[index];
            float radius = sphere.Radius * scale;
            if (radius <= 0f)
                continue;

            AddSphere(
                sphere.Origin * scale,
                radius,
                entityTransform,
                source,
                output);
        }
    }

    private static void AddCylinder(
        Vector3 localOrigin,
        float radius,
        float height,
        Matrix4x4 transform,
        NavigationTriangleSource source,
        TriangleSoup output)
    {
        Vector3 bottomCenter = Vector3.Transform(localOrigin, transform);
        Vector3 topCenter = Vector3.Transform(
            localOrigin + new Vector3(0f, 0f, height),
            transform);
        var bottom = new Vector3[ProxySides];
        var top = new Vector3[ProxySides];
        for (int side = 0; side < ProxySides; side++)
        {
            float angle = MathF.Tau * side / ProxySides;
            var radial = new Vector3(
                MathF.Cos(angle) * radius,
                MathF.Sin(angle) * radius,
                0f);
            bottom[side] = Vector3.Transform(localOrigin + radial, transform);
            top[side] = Vector3.Transform(
                localOrigin + radial + new Vector3(0f, 0f, height),
                transform);
        }

        for (int side = 0; side < ProxySides; side++)
        {
            int next = (side + 1) % ProxySides;
            output.AddTriangle(bottomCenter, bottom[next], bottom[side], source);
            output.AddTriangle(topCenter, top[side], top[next], source);
            output.AddTriangle(bottom[side], bottom[next], top[next], source);
            output.AddTriangle(bottom[side], top[next], top[side], source);
        }
    }

    private static void AddSphere(
        Vector3 localOrigin,
        float radius,
        Matrix4x4 transform,
        NavigationTriangleSource source,
        TriangleSoup output)
    {
        Vector3 bottomPole = Vector3.Transform(
            localOrigin - new Vector3(0f, 0f, radius),
            transform);
        Vector3 topPole = Vector3.Transform(
            localOrigin + new Vector3(0f, 0f, radius),
            transform);
        var rings = new Vector3[SphereLatitudeBands - 1, ProxySides];
        for (int latitude = 1;
            latitude < SphereLatitudeBands;
            latitude++)
        {
            float verticalAngle =
                -MathF.PI / 2f + MathF.PI * latitude / SphereLatitudeBands;
            float ringRadius = MathF.Cos(verticalAngle) * radius;
            float z = MathF.Sin(verticalAngle) * radius;
            for (int side = 0; side < ProxySides; side++)
            {
                float angle = MathF.Tau * side / ProxySides;
                Vector3 local = localOrigin + new Vector3(
                    MathF.Cos(angle) * ringRadius,
                    MathF.Sin(angle) * ringRadius,
                    z);
                rings[latitude - 1, side] = Vector3.Transform(local, transform);
            }
        }

        for (int side = 0; side < ProxySides; side++)
        {
            int next = (side + 1) % ProxySides;
            output.AddTriangle(
                bottomPole,
                rings[0, next],
                rings[0, side],
                source);

            for (int ring = 0; ring < SphereLatitudeBands - 2; ring++)
            {
                Vector3 lower = rings[ring, side];
                Vector3 lowerNext = rings[ring, next];
                Vector3 upper = rings[ring + 1, side];
                Vector3 upperNext = rings[ring + 1, next];
                output.AddTriangle(lower, lowerNext, upperNext, source);
                output.AddTriangle(lower, upperNext, upper, source);
            }

            output.AddTriangle(
                topPole,
                rings[SphereLatitudeBands - 2, side],
                rings[SphereLatitudeBands - 2, next],
                source);
        }
    }
}
