using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

/// <summary>
/// The immediate-mode 2D surface could only ever emit upright rectangles.
/// These pin the two shapes that are not rectangles -- a thick segment and a
/// blit that has been scaled and turned about a pivot -- and pin that neither
/// of them breaks the per-texture batching the upright path relies on.
/// </summary>
public sealed class UiRenderContextPrimitiveTests
{
    private const uint TextureA = 7u;
    private const uint TextureB = 9u;
    private static readonly Vector4 Opaque = new(1f, 1f, 1f, 1f);

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (TextRenderer Renderer, UiRenderContext Context) Build()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        return (renderer, new UiRenderContext(renderer, new Vector2(800f, 600f)));
    }

    /// <summary>Every (x, y) written into one batched run, in emission order.</summary>
    private static List<Vector2> Positions(TextRenderer renderer, int segment = 0)
    {
        IReadOnlyList<float> verts = renderer.DebugSpriteSegmentVerts[segment].Verts;
        var positions = new List<Vector2>(verts.Count / TextRenderer.FloatsPerVertex);
        for (int i = 0; i < verts.Count; i += TextRenderer.FloatsPerVertex)
            positions.Add(new Vector2(verts[i], verts[i + 1]));
        return positions;
    }

    private static List<Vector2> Uvs(TextRenderer renderer, int segment = 0)
    {
        IReadOnlyList<float> verts = renderer.DebugSpriteSegmentVerts[segment].Verts;
        var uvs = new List<Vector2>(verts.Count / TextRenderer.FloatsPerVertex);
        for (int i = 0; i < verts.Count; i += TextRenderer.FloatsPerVertex)
            uvs.Add(new Vector2(verts[i + 2], verts[i + 3]));
        return uvs;
    }

    private static Vector4 FirstColor(TextRenderer renderer, int segment = 0)
    {
        IReadOnlyList<float> verts = renderer.DebugSpriteSegmentVerts[segment].Verts;
        return new Vector4(verts[4], verts[5], verts[6], verts[7]);
    }

    private static void AssertHasCorner(IEnumerable<Vector2> positions, float x, float y)
        => Assert.True(
            positions.Any(p => MathF.Abs(p.X - x) < 0.001f && MathF.Abs(p.Y - y) < 0.001f),
            $"expected a corner at ({x}, {y}); got "
            + string.Join(", ", positions.Select(p => $"({p.X}, {p.Y})")));

    // ── the segment ─────────────────────────────────────────────────────

    [Fact]
    public void AHorizontalSegmentCoversHalfItsWidthOnEachSide()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.DrawLine(10f, 20f, 110f, 20f, Opaque, thickness: 4f);

        List<Vector2> positions = Positions(renderer);
        Assert.Equal(6, positions.Count); // one quad, two triangles
        Assert.Equal(10f, positions.Min(p => p.X));
        Assert.Equal(110f, positions.Max(p => p.X));
        Assert.Equal(18f, positions.Min(p => p.Y));
        Assert.Equal(22f, positions.Max(p => p.Y));
    }

    [Fact]
    public void ADiagonalSegmentIsPerpendicularlyThick()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        // 45 degrees down-right; half of a 2-pixel width is 1 pixel measured
        // perpendicular to the segment, which is 1/sqrt(2) on each axis.
        context.DrawLine(0f, 0f, 100f, 100f, Opaque, thickness: 2f);

        const float offset = 0.70710678f;
        List<Vector2> positions = Positions(renderer);
        AssertHasCorner(positions, -offset, offset);
        AssertHasCorner(positions, offset, -offset);
        AssertHasCorner(positions, 100f - offset, 100f + offset);
        AssertHasCorner(positions, 100f + offset, 100f - offset);
    }

    [Fact]
    public void ASegmentMovesWithTheTransformStackAndDimsWithTheAlphaStack()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.PushTransform(30f, 40f);
        context.PushAlpha(0.5f);
        context.DrawLine(0f, 0f, 10f, 0f, Opaque, thickness: 2f);

        List<Vector2> positions = Positions(renderer);
        Assert.Equal(30f, positions.Min(p => p.X));
        Assert.Equal(40f, positions.Max(p => p.X));
        Assert.Equal(39f, positions.Min(p => p.Y));
        Assert.Equal(41f, positions.Max(p => p.Y));
        Assert.Equal(0.5f, FirstColor(renderer).W, 5);
    }

    [Fact]
    public void ASegmentWithNoLengthOrNoWidthDrawsNothing()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.DrawLine(5f, 5f, 5f, 5f, Opaque, thickness: 3f);
        context.DrawLine(0f, 0f, 50f, 0f, Opaque, thickness: 0f);

        Assert.Empty(renderer.DebugSpriteSegmentVerts);
    }

    [Fact]
    public void ASegmentIsTrimmedToTheEnclosingClip()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.PushClip(0f, 0f, 50f, 600f);
        context.DrawLine(0f, 100f, 200f, 100f, Opaque, thickness: 4f);

        List<Vector2> positions = Positions(renderer);
        Assert.NotEmpty(positions);
        Assert.Equal(50f, positions.Max(p => p.X));
    }

    // ── the transformed blit ────────────────────────────────────────────

    [Fact]
    public void AnUnturnedUnscaledBlitLandsOnThePlainRectangle()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.DrawSpriteTransformed(
            TextureA, 100f, 100f, 40f, 20f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0f, scale: Vector2.One, pivot: new Vector2(20f, 10f));

        List<Vector2> positions = Positions(renderer);
        Assert.Equal(6, positions.Count);
        AssertHasCorner(positions, 100f, 100f);
        AssertHasCorner(positions, 140f, 100f);
        AssertHasCorner(positions, 140f, 120f);
        AssertHasCorner(positions, 100f, 120f);
    }

    [Fact]
    public void AQuarterTurnAboutTheMiddleSwapsTheBlitsWidthAndHeight()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        // A 40x20 rectangle at (100, 100), turned a quarter turn clockwise
        // about its own middle (120, 110), becomes 20 wide and 40 tall.
        context.DrawSpriteTransformed(
            TextureA, 100f, 100f, 40f, 20f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: MathF.PI / 2f,
            scale: Vector2.One,
            pivot: new Vector2(20f, 10f));

        List<Vector2> positions = Positions(renderer);
        AssertHasCorner(positions, 130f, 90f);
        AssertHasCorner(positions, 130f, 130f);
        AssertHasCorner(positions, 110f, 130f);
        AssertHasCorner(positions, 110f, 90f);
    }

    [Fact]
    public void ScaleGrowsTheBlitAwayFromItsPivotOnly()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        // Pivot on the top-left corner, so that corner stays put and the
        // opposite one moves out to twice the width and three times the height.
        context.DrawSpriteTransformed(
            TextureA, 10f, 10f, 40f, 20f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0f,
            scale: new Vector2(2f, 3f),
            pivot: Vector2.Zero);

        List<Vector2> positions = Positions(renderer);
        AssertHasCorner(positions, 10f, 10f);
        AssertHasCorner(positions, 90f, 70f);
    }

    [Fact]
    public void ATurnedBlitIsTrimmedToTheEnclosingClipAndKeepsItsTextureCoordinates()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        // Half the sub-image is cut away on the right, so no surviving corner
        // may reach past the clip edge, and no surviving u may exceed the half
        // of the sub-image that stayed.
        context.PushClip(0f, 0f, 50f, 100f);
        context.DrawSpriteTransformed(
            TextureA, 0f, 0f, 100f, 100f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0f, scale: Vector2.One, pivot: Vector2.Zero);

        List<Vector2> positions = Positions(renderer);
        Assert.NotEmpty(positions);
        Assert.Equal(50f, positions.Max(p => p.X), 3);
        Assert.Equal(0.5f, Uvs(renderer).Max(uv => uv.X), 3);
        Assert.Equal(0f, Uvs(renderer).Min(uv => uv.X), 3);
    }

    [Fact]
    public void ACornerCutOffTheClipTurnsTheQuadIntoFiveCorners()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        // Turned 45 degrees about its middle and nudged so exactly one corner
        // pokes out past the left clip edge: cutting that corner replaces it
        // with two, and a five-cornered fan is three triangles, so nine verts.
        context.PushClip(0f, 0f, 800f, 600f);
        context.DrawSpriteTransformed(
            TextureA, 0f, 100f, 40f, 40f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: MathF.PI / 4f,
            scale: Vector2.One,
            pivot: new Vector2(20f, 20f));

        List<Vector2> positions = Positions(renderer);
        Assert.Equal(9, positions.Count);
        Assert.True(positions.All(p => p.X >= -0.001f));
    }

    [Fact]
    public void ABlitEntirelyOutsideTheClipDrawsNothing()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.PushClip(0f, 0f, 50f, 50f);
        context.DrawSpriteTransformed(
            TextureA, 200f, 200f, 40f, 40f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0.3f, scale: Vector2.One, pivot: Vector2.Zero);

        Assert.Empty(renderer.DebugSpriteSegmentVerts);
    }

    [Fact]
    public void ATurnedBlitMovesWithTheTransformStack()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.PushTransform(200f, 300f);
        context.DrawSpriteTransformed(
            TextureA, 0f, 0f, 10f, 10f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0f, scale: Vector2.One, pivot: Vector2.Zero);

        AssertHasCorner(Positions(renderer), 200f, 300f);
        AssertHasCorner(Positions(renderer), 210f, 310f);
    }

    // ── batching ────────────────────────────────────────────────────────

    [Fact]
    public void ASegmentJoinsTheRunOfUntexturedFillsAroundIt()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.DrawFill(0f, 0f, 10f, 10f, Opaque);
        context.DrawLine(0f, 0f, 10f, 10f, Opaque, thickness: 2f);
        context.DrawFill(20f, 20f, 10f, 10f, Opaque);

        Assert.Single(renderer.DebugSpriteSegmentVerts);
        Assert.Equal(18, Positions(renderer).Count); // 6 + 6 + 6
    }

    [Fact]
    public void ATurnedBlitJoinsTheRunOfUprightBlitsOfTheSameTexture()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.DrawSprite(TextureA, 0f, 0f, 10f, 10f, 0f, 0f, 1f, 1f, Opaque);
        context.DrawSpriteTransformed(
            TextureA, 20f, 0f, 10f, 10f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0.5f, scale: Vector2.One, pivot: Vector2.Zero);
        context.DrawSprite(TextureA, 40f, 0f, 10f, 10f, 0f, 0f, 1f, 1f, Opaque);

        Assert.Single(renderer.DebugSpriteSegmentVerts);
        Assert.Equal(TextureA, renderer.DebugSpriteSegmentVerts[0].Texture);
        Assert.Equal(18, Positions(renderer).Count);
    }

    /// <summary>
    /// The batching rule is run-length by texture in emission order, not a
    /// sort: going A, B, A costs three draw calls even though only two
    /// textures are involved. Anything drawing many small pieces has to emit
    /// them grouped by texture or pay per switch.
    /// </summary>
    [Fact]
    public void AlternatingTexturesCostOneRunPerSwitch()
    {
        (TextRenderer renderer, UiRenderContext context) = Build();

        context.DrawSpriteTransformed(
            TextureA, 0f, 0f, 10f, 10f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0.5f, scale: Vector2.One, pivot: Vector2.Zero);
        context.DrawSpriteTransformed(
            TextureB, 20f, 0f, 10f, 10f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0.5f, scale: Vector2.One, pivot: Vector2.Zero);
        context.DrawSpriteTransformed(
            TextureA, 40f, 0f, 10f, 10f, 0f, 0f, 1f, 1f, Opaque,
            rotationRadians: 0.5f, scale: Vector2.One, pivot: Vector2.Zero);

        Assert.Equal(3, renderer.DebugSpriteSegmentVerts.Count);
        Assert.Equal(TextureA, renderer.DebugSpriteSegmentVerts[0].Texture);
        Assert.Equal(TextureB, renderer.DebugSpriteSegmentVerts[1].Texture);
        Assert.Equal(TextureA, renderer.DebugSpriteSegmentVerts[2].Texture);
    }
}
