using System;
using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class TransformedQuadClipperTests
{
    /// <summary>
    /// The clipper's buffers hold eight corners: the four a quad starts with,
    /// plus the one each of the four clip edges can add. A wider polygon can
    /// therefore grow past them while it is being clipped and write off the
    /// end of the buffer, so the entry point refuses one rather than trusting
    /// its caller to have counted. The only caller today passes four; the
    /// entry point takes anything from three upwards, which is where the gap
    /// was.
    ///
    /// Mutation check (2026-09-21): with the guard taken out, the eight-corner
    /// row threw IndexOutOfRangeException from inside the first clip pass --
    /// one corner outside the left edge turns eight corners into nine. The
    /// five-corner row returned 5 instead of throwing: a convex pentagon
    /// cannot be cut by all four edges, so it does not reach the worst case,
    /// but it is over the limit the buffers can carry and the entry point has
    /// no way to know the caller's shape is convex.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void APolygonWiderThanTheBuffersCanCarryIsRefused(int corners)
    {
        UiQuadVertex[] polygon = RingWithOneCornerOutsideTheLeftEdge(corners);
        var destination = new UiQuadVertex[TransformedQuadClipper.MaxClippedVertices];

        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => TransformedQuadClipper.Clip(
                0f, 0f, 10f, 10f, polygon, destination));

        Assert.Equal("polygon", thrown.ParamName);
    }

    /// <summary>
    /// The shape the clipper exists for still goes through: four corners,
    /// cut by all four edges, is exactly the worst case the buffers are
    /// sized for.
    /// </summary>
    [Fact]
    public void AQuadCutByEveryEdgeStillFillsTheBufferAndNoMore()
    {
        // A diamond around the clip rectangle: one corner beyond each edge,
        // so every pass adds one and the result is the full eight.
        UiQuadVertex[] diamond =
        [
            Corner(-5f, 5f),
            Corner(5f, -5f),
            Corner(15f, 5f),
            Corner(5f, 15f),
        ];
        var destination = new UiQuadVertex[TransformedQuadClipper.MaxClippedVertices];

        int count = TransformedQuadClipper.Clip(
            0f, 0f, 10f, 10f, diamond, destination);

        Assert.Equal(TransformedQuadClipper.MaxClippedVertices, count);
        Assert.Equal(
            TransformedQuadClipper.MaxInputVertices,
            diamond.Length);
    }

    /// <summary>
    /// A convex ring around the middle of the clip rectangle with one corner
    /// pushed out past its left edge, which is the growing case: one corner
    /// outside a boundary is replaced by two crossings, so each pass can add
    /// one to the count.
    /// </summary>
    private static UiQuadVertex[] RingWithOneCornerOutsideTheLeftEdge(int corners)
    {
        var ring = new UiQuadVertex[corners];
        for (int index = 0; index < corners; index++)
        {
            // Corner zero is the leftmost one, so pushing it straight out
            // past the left edge keeps the ring convex.
            float angle = MathF.PI + (MathF.Tau * index / corners);
            ring[index] = Corner(
                5f + 4f * MathF.Cos(angle),
                5f + 4f * MathF.Sin(angle));
        }
        ring[0] = Corner(-5f, 5f);
        return ring;
    }

    private static UiQuadVertex Corner(float x, float y) =>
        new(new Vector2(x, y), Vector2.Zero);
}
