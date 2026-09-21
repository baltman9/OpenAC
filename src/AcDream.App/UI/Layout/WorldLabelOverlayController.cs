using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Interaction;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.UI.Layout;

/// <summary>
/// One label placed on the screen: which label it is, where its text starts,
/// how far its object is from the camera in metres, and how faded it is.
/// </summary>
internal readonly record struct WorldLabelPlacement(
    int LabelIndex,
    float X,
    float Y,
    float Depth,
    float Alpha);

/// <summary>
/// Where each label goes on the screen this frame. Pure: the same labels,
/// anchors and camera give the same placements, which is what the tests
/// hold it to.
/// </summary>
internal static class WorldLabelLayout
{
    /// <summary>
    /// The last fifth of a label's range is the fade: at four fifths of the
    /// range it is solid, at the range it is gone.
    /// </summary>
    internal const float FadeFraction = 0.2f;

    private static readonly Comparison<WorldLabelPlacement> FarToNear =
        static (left, right) => right.Depth.CompareTo(left.Depth);

    /// <summary>
    /// Fills <paramref name="output"/> with this frame's placements, farthest
    /// first, so that when two labels overlap the nearer object's is drawn
    /// last and wins.
    /// </summary>
    /// <param name="labels">Every label every plugin has showing.</param>
    /// <param name="anchor">
    /// The object's base and height by server id, or null when the client
    /// does not hold the object.
    /// </param>
    /// <param name="view">The camera's view matrix.</param>
    /// <param name="projection">The camera's projection matrix.</param>
    /// <param name="viewport">The size of the screen the world is drawn into.</param>
    /// <param name="lineHeight">The font's line height in pixels.</param>
    /// <param name="measure">The width of a string in this font, in pixels.</param>
    /// <param name="output">Cleared, then filled; never reallocated.</param>
    internal static void Place(
        IReadOnlyList<PluginWorldLabel> labels,
        Func<uint, WorldLabelAnchor?> anchor,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector2 viewport,
        float lineHeight,
        Func<string, float> measure,
        List<WorldLabelPlacement> output)
    {
        output.Clear();
        for (int index = 0; index < labels.Count; index++)
        {
            PluginWorldLabel label = labels[index];
            if (anchor(label.ObjectId) is not { } at)
                continue;

            // The label hangs from the top of the object: its base, up by
            // its height, up again by whatever the plugin asked for.
            Vector3 head = at.BasePosition
                + new Vector3(0f, 0f, at.Height + label.HeightOffset);
            if (!ScreenProjection.TryProjectSphereToScreenRect(
                    head,
                    0f,
                    view,
                    projection,
                    viewport,
                    out Vector2 minimum,
                    out Vector2 maximum,
                    out float depth,
                    minSidePixels: 0f)
                || depth > label.MaxRange)
            {
                continue;
            }

            float width = measure(label.Text);
            float x = (minimum.X + maximum.X) * 0.5f - width * 0.5f;
            // Line 0 sits with its bottom on the head; each further line is
            // one line height higher, which on the screen is a smaller Y.
            float y = (minimum.Y + maximum.Y) * 0.5f - lineHeight * (label.Line + 1);
            if (x + width < 0f
                || x > viewport.X
                || y + lineHeight < 0f
                || y > viewport.Y)
            {
                continue;
            }

            output.Add(new WorldLabelPlacement(
                index, x, y, depth, Fade(depth, label.MaxRange)));
        }

        output.Sort(FarToNear);
    }

    /// <summary>How solid a label is at this distance: 1 until the fade starts, 0 at its range.</summary>
    internal static float Fade(float depth, float maxRange)
    {
        float fadeStart = maxRange * (1f - FadeFraction);
        if (depth <= fadeStart)
            return 1f;
        float fadeLength = maxRange - fadeStart;
        if (fadeLength <= 0f)
            return 0f;
        return Math.Clamp((maxRange - depth) / fadeLength, 0f, 1f);
    }
}

/// <summary>
/// The one element that draws every label. There is deliberately not an
/// element per label: sprite quads are batched into runs by texture in
/// emission order, and a label is glyph quads against a font's outline
/// atlas and then its fill atlas, so drawing labels one at a time costs two
/// draw calls per label. Drawing every outline and then every fill costs
/// two for the whole set. That ordering is the whole point of this type.
///
/// <para>Text is drawn at a constant screen size. Glyphs are never scaled
/// with distance: the font draw snaps every glyph to a whole pixel precisely
/// so text does not shimmer as its anchor drifts, and a scale would undo
/// that.</para>
/// </summary>
internal sealed class WorldLabelLayerElement : UiElement
{
    private readonly UiDatFont _font;
    private IReadOnlyList<PluginWorldLabel> _labels = Array.Empty<PluginWorldLabel>();
    private IReadOnlyList<WorldLabelPlacement> _placements = Array.Empty<WorldLabelPlacement>();

    internal WorldLabelLayerElement(UiDatFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        ClickThrough = true;
        Anchors = AnchorEdges.None;
    }

    /// <summary>
    /// What to draw from now on. The placements list handed in is read until
    /// the next call and never written by this element, so the owner can
    /// fill a different list meanwhile.
    /// </summary>
    internal void Present(
        IReadOnlyList<PluginWorldLabel> labels,
        IReadOnlyList<WorldLabelPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(placements);
        _labels = labels;
        _placements = placements;
    }

    /// <summary>How many placements the element is currently drawing.</summary>
    internal int PlacementCount => _placements.Count;

    protected override void OnDraw(UiRenderContext ctx)
    {
        IReadOnlyList<PluginWorldLabel> labels = _labels;
        IReadOnlyList<WorldLabelPlacement> placements = _placements;

        // Pass 1: every outline, one run against the outline atlas.
        for (int index = 0; index < placements.Count; index++)
        {
            WorldLabelPlacement placement = placements[index];
            PluginWorldLabel label = labels[placement.LabelIndex];
            if (!label.Outline)
                continue;
            ctx.PushAlpha(placement.Alpha);
            ctx.DrawStringDatPass(
                _font,
                label.Text,
                placement.X,
                placement.Y,
                UiRenderContext.DefaultOutlineColor,
                isOutlinePass: true);
            ctx.PopAlpha();
        }

        // Pass 2: every fill, one run against the fill atlas.
        for (int index = 0; index < placements.Count; index++)
        {
            WorldLabelPlacement placement = placements[index];
            PluginWorldLabel label = labels[placement.LabelIndex];
            ctx.PushAlpha(placement.Alpha);
            ctx.DrawStringDatPass(
                _font,
                label.Text,
                placement.X,
                placement.Y,
                label.Color,
                isOutlinePass: false);
            ctx.PopAlpha();
        }
    }
}

/// <summary>
/// Hangs plugin labels over world objects on the shared overlay band.
///
/// <para>Each frame it projects every label through the same world-to-screen
/// call the target indicator uses, drops the ones past their range, fades the
/// ones in the last fifth of it, sorts far to near, stacks a label's lines
/// upward, and hands the result to one drawing element. Two placement lists
/// alternate: the element reads one while the next frame is worked out in
/// the other, so the list being drawn is never the list being written. The
/// element itself is allocated once and reused; nothing is created or
/// destroyed per frame or per label.</para>
///
/// <para>There is no occlusion. A label shows through a wall, because the
/// interface is drawn after the world with no depth to test against.
/// Retaining depth is a renderer change in its own right and was left out of
/// this version on purpose.</para>
/// </summary>
internal sealed class WorldLabelOverlayController
{
    private readonly UiOverlayHost _host;
    private readonly UiOverlayLayer _layer;
    private readonly WorldLabelLayerElement _element;
    private readonly UiDatFont _font;
    private readonly Func<IReadOnlyList<PluginWorldLabel>> _labels;
    private readonly Func<uint, WorldLabelAnchor?> _anchor;
    private readonly Func<(Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport)> _camera;
    private List<WorldLabelPlacement> _front = [];
    private List<WorldLabelPlacement> _back = [];

    private WorldLabelOverlayController(
        UiOverlayHost host,
        UiOverlayLayer layer,
        WorldLabelLayerElement element,
        UiDatFont font,
        Func<IReadOnlyList<PluginWorldLabel>> labels,
        Func<uint, WorldLabelAnchor?> anchor,
        Func<(Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport)> camera)
    {
        _host = host;
        _layer = layer;
        _element = element;
        _font = font;
        _labels = labels;
        _anchor = anchor;
        _camera = camera;
    }

    /// <summary>The element doing the drawing, for the tests that count its runs.</summary>
    internal WorldLabelLayerElement Element => _element;

    internal static WorldLabelOverlayController Mount(
        UiOverlayHost host,
        UiDatFont font,
        Func<IReadOnlyList<PluginWorldLabel>> labels,
        Func<uint, WorldLabelAnchor?> anchor,
        Func<(Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport)> camera)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(camera);
        UiOverlayLayer layer = host.AddLayer("PluginWorldLabelOverlay");
        var element = new WorldLabelLayerElement(font)
        {
            Name = "PluginWorldLabels",
            Left = 0f,
            Top = 0f,
            Width = layer.Width,
            Height = layer.Height,
        };
        layer.AddChild(element);
        return new WorldLabelOverlayController(
            host, layer, element, font, labels, anchor, camera);
    }

    internal void Tick()
    {
        IReadOnlyList<PluginWorldLabel> labels = _labels();
        var camera = _camera();
        if (labels.Count == 0
            || camera.Viewport.X <= 0f
            || camera.Viewport.Y <= 0f)
        {
            _back.Clear();
            Present(labels);
            return;
        }

        _host.SetViewport(camera.Viewport);
        _element.Width = camera.Viewport.X;
        _element.Height = camera.Viewport.Y;
        WorldLabelLayout.Place(
            labels,
            _anchor,
            camera.View,
            camera.Projection,
            camera.Viewport,
            _font.LineHeight,
            _font.MeasureWidth,
            _back);
        Present(labels);
    }

    /// <summary>
    /// Swaps the freshly written list in for drawing and keeps the old one
    /// to write the next frame into.
    /// </summary>
    private void Present(IReadOnlyList<PluginWorldLabel> labels)
    {
        (_front, _back) = (_back, _front);
        _element.Present(labels, _front);
        _layer.Visible = _front.Count > 0;
    }
}
