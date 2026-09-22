using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Interaction;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Plugin.Abstractions;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// The batching rule the label work depends on. Sprite quads are collected
/// into runs keyed by texture in emission order, with no sort, so a quad
/// joins the previous run only when its texture matches the one drawn
/// immediately before it. A label is glyph quads against the font's outline
/// atlas and then its fill atlas: drawn one label at a time that is two runs
/// per label; drawn every outline then every fill it is two runs for the
/// whole set. A run is a draw call.
/// </summary>
public sealed class WorldLabelOverlayDrawTests
{
    private const uint ForegroundTex = 1u;
    private const uint BackgroundTex = 2u;
    private const int VerticesPerQuad = 6;

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static FontCharDesc Glyph(char c) => new()
    {
        Unicode = c,
        Width = 8,
        Height = 8,
        OffsetX = 4,
        OffsetY = 4,
    };

    private static UiDatFont Font() => new(
        fgTex: ForegroundTex, fgW: 64, fgH: 64,
        bgTex: BackgroundTex, bgW: 64, bgH: 64,
        lineHeight: 16f, baselineOffset: 12f,
        glyphs: new Dictionary<char, FontCharDesc>
        {
            ['A'] = Glyph('A'),
            ['B'] = Glyph('B'),
        },
        borderX: 4, borderY: 4);

    private static readonly Matrix4x4 View =
        Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ);

    private static readonly Matrix4x4 Projection =
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 4f / 3f, 0.1f, 100f);

    private static readonly Vector2 Viewport = new(800f, 600f);

    /// <summary>
    /// Every object stands somewhere in front of the camera, spread out so
    /// the labels do not all land on one pixel. Ids run from 1.
    /// </summary>
    private static WorldLabelAnchor? Anchor(uint id) => id == 0u
        ? null
        : new WorldLabelAnchor(
            new Vector3((id % 8) - 4f, 8f + id % 5, 0f),
            1.5f,
            WorldLabelAnchorSource.PhysicsCylinder);

    private static (TextRenderer Renderer, IReadOnlyList<(uint Texture, int VertexCount, float Alpha)> Segments)
        DrawOnce(IReadOnlyList<PluginWorldLabel> labels, out WorldLabelOverlayController controller)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(Viewport);
        var ctx = new UiRenderContext(renderer, Viewport);

        var root = new UiRoot { Width = Viewport.X, Height = Viewport.Y };
        UiOverlayHost host = UiOverlayHost.Mount(root);
        controller = WorldLabelOverlayController.Mount(
            host,
            Font(),
            () => labels,
            Anchor,
            () => (View, Projection, Viewport));

        controller.Tick();
        root.Draw(ctx);
        return (renderer, renderer.DebugSpriteSegments);
    }

    private static PluginWorldLabel[] Labels(int count, bool outline = true) =>
        Enumerable.Range(1, count)
            .Select(index => new PluginWorldLabel(
                (uint)index, "AB", Vector4.One, Outline: outline))
            .ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void TheWholeSetCostsTwoDrawCallsHoweverManyLabelsThereAre(int count)
    {
        (_, var segments) = DrawOnce(Labels(count), out WorldLabelOverlayController controller);

        Assert.Equal(count, controller.Element.PlacementCount);
        Assert.Equal(2, segments.Count);
        Assert.Equal(BackgroundTex, segments[0].Texture);
        Assert.Equal(count * 2 * VerticesPerQuad, segments[0].VertexCount);
        Assert.Equal(ForegroundTex, segments[1].Texture);
        Assert.Equal(count * 2 * VerticesPerQuad, segments[1].VertexCount);
    }

    [Fact]
    public void LabelsWithoutAnOutlineCostOneDrawCallForTheSet()
    {
        (_, var segments) = DrawOnce(Labels(20, outline: false), out _);

        var only = Assert.Single(segments);
        Assert.Equal(ForegroundTex, only.Texture);
        Assert.Equal(20 * 2 * VerticesPerQuad, only.VertexCount);
    }

    [Fact]
    public void AMixOfOutlinedAndPlainLabelsStillCostsTwoDrawCalls()
    {
        PluginWorldLabel[] mixed = Labels(30);
        for (int index = 0; index < mixed.Length; index += 2)
            mixed[index] = mixed[index] with { Outline = false };

        (_, var segments) = DrawOnce(mixed, out _);

        Assert.Equal(2, segments.Count);
        Assert.Equal(BackgroundTex, segments[0].Texture);
        Assert.Equal(15 * 2 * VerticesPerQuad, segments[0].VertexCount);
        Assert.Equal(ForegroundTex, segments[1].Texture);
        Assert.Equal(30 * 2 * VerticesPerQuad, segments[1].VertexCount);
    }

    [Fact]
    public void AnEmptySetDrawsNothingAndHidesTheLayer()
    {
        (_, var segments) = DrawOnce([], out WorldLabelOverlayController controller);

        Assert.Empty(segments);
        Assert.Equal(0, controller.Element.PlacementCount);
    }

    [Fact]
    public void WhatIsDrawnChangesOnlyWhenATickPresentsTheNextList()
    {
        // The surface hands out a fresh array whenever a set changes and
        // never writes into one it has handed out; the controller does the
        // same with its placements, so the element always reads a list
        // nobody is writing.
        PluginWorldLabel[] current = Labels(3);
        var root = new UiRoot { Width = Viewport.X, Height = Viewport.Y };
        UiOverlayHost host = UiOverlayHost.Mount(root);
        WorldLabelOverlayController controller = WorldLabelOverlayController.Mount(
            host, Font(), () => current, Anchor, () => (View, Projection, Viewport));

        controller.Tick();
        Assert.Equal(3, controller.Element.PlacementCount);

        current = [];
        Assert.Equal(3, controller.Element.PlacementCount);
        controller.Tick();
        Assert.Equal(0, controller.Element.PlacementCount);

        current = Labels(5);
        controller.Tick();
        Assert.Equal(5, controller.Element.PlacementCount);
    }
}
