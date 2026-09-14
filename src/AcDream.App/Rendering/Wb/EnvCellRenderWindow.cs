using System;
using System.Numerics;

namespace AcDream.App.Rendering.Wb;

/// <summary>
/// The square of landblock-sized world space around the camera that indoor cell
/// geometry is prepared for.
/// </summary>
/// <remarks>
/// A cell's placement comes from the cell data, not from the landblock id it is
/// filed under, and a large dungeon routinely places its cells hundreds of
/// metres outside the 192 m footprint of that landblock. Measuring the window
/// against the grid index of the landblock therefore drops the cells a player is
/// standing in as soon as the dungeon runs more than <c>radius</c> blocks away
/// from its own origin. The window is measured against the geometry's own world
/// bounds instead, which is the same volume the frustum test already uses.
///
/// For a landblock whose cells do stay inside their own footprint this is the
/// identical set: a block occupies <c>[origin, origin + 192]</c>, so overlapping
/// the half-open window is exactly "its block index is within radius of the
/// camera's block index".
/// </remarks>
internal static class EnvCellRenderWindow
{
    internal const float LandblockSize = 192f;

    /// <summary>
    /// The half-open world span, on one axis, of the (2 * radius + 1) landblocks
    /// centred on the block the camera stands in.
    /// </summary>
    internal static (float Min, float Max) Axis(float cameraAxis, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        float cameraBlock = MathF.Floor(cameraAxis / LandblockSize);
        return (
            (cameraBlock - radius) * LandblockSize,
            (cameraBlock + radius + 1) * LandblockSize);
    }

    /// <summary>
    /// Whether world-space <paramref name="bounds"/> reach into the window. Both
    /// the bounds and <paramref name="cameraPosition"/> are in the render frame.
    /// </summary>
    internal static bool Intersects(
        Vector3 cameraPosition,
        int radius,
        in WbBoundingBox bounds)
    {
        (float minX, float maxX) = Axis(cameraPosition.X, radius);
        if (bounds.Max.X <= minX || bounds.Min.X >= maxX)
            return false;

        (float minY, float maxY) = Axis(cameraPosition.Y, radius);
        return bounds.Max.Y > minY && bounds.Min.Y < maxY;
    }
}
