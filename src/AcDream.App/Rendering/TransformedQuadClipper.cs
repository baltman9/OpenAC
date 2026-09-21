using System;
using System.Numerics;

namespace AcDream.App.Rendering;

/// <summary>
/// One corner of a textured shape in screen pixels, carrying the texture
/// coordinate that belongs at that corner. A quad that has been rotated or
/// scaled can no longer be described by an (x, y, w, h) rectangle plus a
/// (u0, v0, u1, v1) sub-image, so its corners travel as four of these.
/// </summary>
internal struct UiQuadVertex
{
    public Vector2 Position;
    public Vector2 Uv;

    public UiQuadVertex(Vector2 position, Vector2 uv)
    {
        Position = position;
        Uv = uv;
    }

    public UiQuadVertex(float x, float y, float u, float v)
        : this(new Vector2(x, y), new Vector2(u, v))
    {
    }
}

/// <summary>
/// Trims a convex polygon down to the part inside an axis-aligned clip
/// rectangle, carrying the texture coordinates along with it.
///
/// <para>The axis-aligned case has its own clipper (<see cref="QuadClipper"/>)
/// that just moves the rectangle's edges and rescales the two UV ranges. That
/// shortcut stops working the moment a quad is rotated: the four corners no
/// longer line up with the clip rectangle's edges, and cutting one corner off
/// turns a four-sided shape into a five-sided one. So this walks the polygon
/// once per clip edge and keeps the part on the inside -- the classic
/// Sutherland-Hodgman method. Each pass can add at most one corner (the one
/// place where the outline crosses that edge and comes back), so four passes
/// take a quad from four corners to at most eight.</para>
///
/// <para>That bound is why the entry point refuses a wider shape than a
/// quad: the buffers are sized for eight, and the count has to fit at every
/// pass, not only at the end.</para>
/// </summary>
internal static class TransformedQuadClipper
{
    /// <summary>The four sides a shape is cut against.</summary>
    private const int ClipEdges = 4;

    /// <summary>
    /// The largest number of corners a clipped quad can have: four to start
    /// with, plus one for each of the four clip edges it crosses.
    /// </summary>
    public const int MaxClippedVertices = 8;

    /// <summary>
    /// The most corners an input shape may have. Each pass can add one, so a
    /// shape wider than this can outgrow the buffers part-way through being
    /// clipped -- and a shape that is not convex can outgrow them faster
    /// still.
    /// </summary>
    public const int MaxInputVertices = MaxClippedVertices - ClipEdges;

    /// <summary>
    /// Writes the part of <paramref name="polygon"/> inside the clip rectangle
    /// into <paramref name="destination"/> and returns how many corners that
    /// took. Zero means the shape fell entirely outside and draws nothing.
    /// <paramref name="destination"/> must hold <see cref="MaxClippedVertices"/>
    /// and <paramref name="polygon"/> at most <see cref="MaxInputVertices"/>.
    /// </summary>
    public static int Clip(
        float clipLeft,
        float clipTop,
        float clipRight,
        float clipBottom,
        ReadOnlySpan<UiQuadVertex> polygon,
        Span<UiQuadVertex> destination)
    {
        if (destination.Length < MaxClippedVertices)
            throw new ArgumentException(
                $"A clipped quad needs room for {MaxClippedVertices} corners.",
                nameof(destination));
        // Guarding only the destination is not enough: the buffers have to
        // hold what the shape grows to part-way through, not just what comes
        // out at the end.
        if (polygon.Length > MaxInputVertices)
            throw new ArgumentException(
                $"A shape of more than {MaxInputVertices} corners can grow "
                + $"past {MaxClippedVertices} while it is being clipped.",
                nameof(polygon));
        if (polygon.Length < 3 || clipRight <= clipLeft || clipBottom <= clipTop)
            return 0;

        Span<UiQuadVertex> scratch = stackalloc UiQuadVertex[MaxClippedVertices];
        polygon.CopyTo(destination);
        int count = polygon.Length;

        count = ClipAgainstEdge(ClipEdge.Left, clipLeft, destination, count, scratch);
        if (count == 0) return 0;
        count = ClipAgainstEdge(ClipEdge.Top, clipTop, scratch, count, destination);
        if (count == 0) return 0;
        count = ClipAgainstEdge(ClipEdge.Right, clipRight, destination, count, scratch);
        if (count == 0) return 0;
        count = ClipAgainstEdge(ClipEdge.Bottom, clipBottom, scratch, count, destination);
        return count;
    }

    private enum ClipEdge
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    /// <summary>
    /// Keeps the part of the outline on the inside of one clip edge. Walks the
    /// corners in order; whenever consecutive corners sit on opposite sides of
    /// the edge, the crossing point is emitted in between.
    /// </summary>
    private static int ClipAgainstEdge(
        ClipEdge edge,
        float boundary,
        ReadOnlySpan<UiQuadVertex> input,
        int count,
        Span<UiQuadVertex> output)
    {
        int written = 0;
        for (int index = 0; index < count; index++)
        {
            UiQuadVertex current = input[index];
            UiQuadVertex previous = input[(index + count - 1) % count];
            float currentDistance = SignedDistance(edge, boundary, current.Position);
            float previousDistance = SignedDistance(edge, boundary, previous.Position);
            bool currentInside = currentDistance >= 0f;
            bool previousInside = previousDistance >= 0f;

            if (currentInside)
            {
                if (!previousInside)
                    output[written++] = Crossing(previous, current, previousDistance, currentDistance);
                output[written++] = current;
            }
            else if (previousInside)
            {
                output[written++] = Crossing(previous, current, previousDistance, currentDistance);
            }
        }

        return written;
    }

    /// <summary>
    /// How far a point sits on the inside of one clip edge, in pixels.
    /// Negative means outside.
    /// </summary>
    private static float SignedDistance(ClipEdge edge, float boundary, Vector2 position)
        => edge switch
        {
            ClipEdge.Left => position.X - boundary,
            ClipEdge.Top => position.Y - boundary,
            ClipEdge.Right => boundary - position.X,
            _ => boundary - position.Y,
        };

    private static UiQuadVertex Crossing(
        in UiQuadVertex from, in UiQuadVertex to, float fromDistance, float toDistance)
    {
        float span = fromDistance - toDistance;
        // The two ends straddle the edge, so the span cannot be zero; the guard
        // is for the degenerate input where both distances are exactly zero.
        float t = MathF.Abs(span) > float.Epsilon ? fromDistance / span : 0f;
        return new UiQuadVertex(
            Vector2.Lerp(from.Position, to.Position, t),
            Vector2.Lerp(from.Uv, to.Uv, t));
    }
}
