using System.Numerics;
using AcDream.App.Interaction;
using AcDream.App.UI.Layout;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// Where a label lands on the screen, and which labels land at all: the
/// projection of the object's head, the range cull on view-space depth, the
/// fade over the last fifth, the far-to-near order, and the stacking of a
/// second line above the first.
/// </summary>
public sealed class WorldLabelOverlayLayoutTests
{
    private const float LineHeight = 16f;
    private static readonly Vector2 Viewport = new(800f, 600f);

    // The camera stands at the origin looking along +Y with +Z up, so an
    // object at (0, d, 0) is d metres away, straight ahead.
    private static readonly Matrix4x4 View =
        Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ);

    // A 90 degree vertical field of view: M22 is 1, so a point 2 m up at
    // 10 m away sits at 0.2 of the half-height above centre.
    private static readonly Matrix4x4 Projection =
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 4f / 3f, 0.1f, 100f);

    private static float Measure(string text) => text.Length * 10f;

    private static PluginWorldLabel Label(
        uint id, string text = "name", int line = 0, float range = 60f, float offset = 0f) =>
        new(id, text, Vector4.One, HeightOffset: offset, Line: line, MaxRange: range);

    private static List<WorldLabelPlacement> Place(
        IReadOnlyList<PluginWorldLabel> labels,
        Func<uint, WorldLabelAnchor?> anchor)
    {
        var output = new List<WorldLabelPlacement>();
        WorldLabelLayout.Place(
            labels, anchor, View, Projection, Viewport, LineHeight, Measure, output);
        return output;
    }

    private static WorldLabelAnchor? StandingAt(float distance, float height = 2f) =>
        new(new Vector3(0f, distance, 0f), height, WorldLabelAnchorSource.PhysicsCylinder);

    [Fact]
    public void TheLabelIsCentredOverTheObjectsHeadWithItsBottomOnTheHead()
    {
        List<WorldLabelPlacement> placed = Place(
            [Label(1u, "name")],
            _ => StandingAt(10f, height: 2f));

        WorldLabelPlacement only = Assert.Single(placed);
        // Head at 2 m up, 10 m away: ndc y = 0.2, screen y = (1 - 0.6) * 600.
        Assert.Equal(400f - 20f, only.X, 3);
        Assert.Equal(240f - LineHeight, only.Y, 3);
        Assert.Equal(10f, only.Depth, 3);
        Assert.Equal(1f, only.Alpha);
    }

    [Fact]
    public void TheHeightOffsetRaisesTheLabelInMetresNotPixels()
    {
        WorldLabelPlacement plain = Assert.Single(Place(
            [Label(1u)], _ => StandingAt(10f, height: 2f)));
        WorldLabelPlacement raised = Assert.Single(Place(
            [Label(1u, offset: 1f)], _ => StandingAt(10f, height: 2f)));

        // 3 m up at 10 m: ndc y = 0.3, screen y = (1 - 0.65) * 600 = 210.
        Assert.Equal(240f - LineHeight, plain.Y, 3);
        Assert.Equal(210f - LineHeight, raised.Y, 3);
    }

    [Fact]
    public void ALabelPastItsRangeAnObjectNotHeldAndAnObjectBehindTheCameraAreNotPlaced()
    {
        Assert.Empty(Place([Label(1u, range: 9.99f)], _ => StandingAt(10f)));
        Assert.Empty(Place([Label(1u)], _ => null));
        Assert.Empty(Place([Label(1u)], _ => StandingAt(-10f)));
    }

    [Fact]
    public void TheRangeIsMeasuredAsViewSpaceDepthInMetres()
    {
        // Exactly at the range is still placed, one centimetre past is not.
        Assert.Single(Place([Label(1u, range: 10f)], _ => StandingAt(10f)));
        Assert.Empty(Place([Label(1u, range: 10f)], _ => StandingAt(10.01f)));
    }

    [Theory]
    [InlineData(5f, 10f, 1f)]
    [InlineData(8f, 10f, 1f)]
    [InlineData(9f, 10f, 0.5f)]
    [InlineData(9.5f, 10f, 0.25f)]
    [InlineData(10f, 10f, 0f)]
    public void TheFadeCoversTheLastFifthOfTheRange(float depth, float range, float expected)
    {
        Assert.Equal(expected, WorldLabelLayout.Fade(depth, range), 4);

        WorldLabelPlacement placed = Assert.Single(Place(
            [Label(1u, range: range)], _ => StandingAt(depth)));
        Assert.Equal(expected, placed.Alpha, 4);
    }

    [Fact]
    public void PlacementsComeFarToNearSoTheNearestIsDrawnLastAndWins()
    {
        WorldLabelAnchor? At(uint id) => id switch
        {
            1u => StandingAt(5f),
            2u => StandingAt(20f),
            3u => StandingAt(10f),
            _ => null,
        };

        List<WorldLabelPlacement> placed = Place([Label(1u), Label(2u), Label(3u)], At);

        Assert.Equal([1, 2, 0], placed.Select(static p => p.LabelIndex).ToArray());
        Assert.Equal([20f, 10f, 5f], placed.Select(static p => p.Depth).ToArray());
    }

    [Fact]
    public void ASecondLineStacksOneLineHeightAboveTheFirst()
    {
        List<WorldLabelPlacement> placed = Place(
            [Label(1u, "name", line: 0), Label(1u, "12 m", line: 1)],
            _ => StandingAt(10f));

        Assert.Equal(2, placed.Count);
        WorldLabelPlacement first = placed.Single(static p => p.LabelIndex == 0);
        WorldLabelPlacement second = placed.Single(static p => p.LabelIndex == 1);
        Assert.Equal(first.Y - LineHeight, second.Y, 3);
        Assert.Equal(first.Depth, second.Depth);
    }

    [Fact]
    public void TheOutputListIsReusedNotReplaced()
    {
        var output = new List<WorldLabelPlacement>();
        WorldLabelLayout.Place(
            [Label(1u)], _ => StandingAt(10f), View, Projection, Viewport,
            LineHeight, Measure, output);
        Assert.Single(output);

        WorldLabelLayout.Place(
            [], _ => StandingAt(10f), View, Projection, Viewport,
            LineHeight, Measure, output);
        Assert.Empty(output);
    }
}
