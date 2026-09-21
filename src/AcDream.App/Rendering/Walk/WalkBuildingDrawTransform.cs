using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.App.Rendering.Walk;

internal static class WalkBuildingDrawTransform
{
    internal static Matrix4x4 Resolve(
        Matrix4x4 placement, Vector3 sortCenter, Vector3 viewer, uint mode)
    {
        if (mode is < 2 or > 5)
            return placement;
        if (!Matrix4x4.Decompose(placement, out Vector3 scale,
                out Quaternion orientation, out Vector3 translation))
            return placement;

        Vector3 heading = Vector3.Transform(sortCenter, placement) - viewer;
        float lengthSquared = heading.LengthSquared();
        if (!float.IsFinite(lengthSquared))
            return placement;
        heading = lengthSquared < PhysicsGlobals.EpsilonSq
            ? Vector3.UnitZ
            : heading / MathF.Sqrt(lengthSquared);

        if (mode == 2)
        {
            orientation = RetailFrameMath.SetVectorHeading(orientation, heading);
            return Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(orientation)
                * Matrix4x4.CreateTranslation(translation);
        }

        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(orientation);
        Span<Vector3> axes = stackalloc Vector3[3]
        {
            new(rotation.M11, rotation.M12, rotation.M13),
            new(rotation.M21, rotation.M22, rotation.M23),
            new(rotation.M31, rotation.M32, rotation.M33),
        };
        int fixedAxis = (int)mode - 3;
        int facingAxis = (fixedAxis + 2) % 3;
        int remainingAxis = (facingAxis + 2) % 3;
        Vector3 axis = axes[fixedAxis];
        Vector3 facing = heading - axis * Vector3.Dot(axis, heading);
        float projectedLengthSquared = facing.LengthSquared();
        if (projectedLengthSquared < PhysicsGlobals.EpsilonSq)
            facing = axes[facingAxis];
        else
            facing /= MathF.Sqrt(projectedLengthSquared);
        if (MathF.Abs(Vector3.Dot(axis, facing)) > PhysicsGlobals.EPSILON * 5f)
            return placement;

        axes[facingAxis] = facing;
        axes[remainingAxis] = Vector3.Cross(facing, axis);
        rotation = new Matrix4x4(
            axes[0].X, axes[0].Y, axes[0].Z, 0f,
            axes[1].X, axes[1].Y, axes[1].Z, 0f,
            axes[2].X, axes[2].Y, axes[2].Z, 0f,
            0f, 0f, 0f, 1f);
        return Matrix4x4.CreateScale(scale) * rotation
            * Matrix4x4.CreateTranslation(translation);
    }
}
