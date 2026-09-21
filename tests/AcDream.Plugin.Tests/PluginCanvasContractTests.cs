// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Tests.Fixtures;

namespace AcDream.Plugin.Tests;

/// <summary>
/// The canvas contract is additive with inert defaults: a host that never
/// heard of canvases accepts one, keeps the state the plugin sets, and
/// never calls the paint callback.
/// </summary>
public sealed class PluginCanvasContractTests
{
    private sealed class BareRegistry : IUiRegistry
    {
        public void AddMarkupPanel(string markupPath, object binding)
        {
        }
    }

    private sealed class BareScopedRegistry : IScopedUiRegistry
    {
        public void AddMarkupPanel(string markupPath, object binding)
        {
        }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;
    }

    private static PluginCanvasDescriptor Descriptor() =>
        new("hud", 200, 100)
        {
            Anchor = PluginCanvasAnchor.BottomRight,
            Offset = new PluginPoint(-10, -20),
            StartVisible = false,
        };

    [Fact]
    public void ADescriptorDefaultsToTheTopLeftShownAtOnce()
    {
        var descriptor = new PluginCanvasDescriptor("map", 64, 64);

        Assert.Equal(PluginCanvasAnchor.TopLeft, descriptor.Anchor);
        Assert.Equal(default, descriptor.Offset);
        Assert.True(descriptor.StartVisible);
    }

    [Fact]
    public void ARegistryThatNeverHeardOfCanvasesAcceptsOneAndNeverPaints()
    {
        IUiRegistry registry = new BareRegistry();
        IScopedUiRegistry scoped = new BareScopedRegistry();
        int paints = 0;

        IPluginCanvas canvas = registry.RegisterCanvas(Descriptor(), _ => paints++);
        IPluginCanvas scopedCanvas = scoped.RegisterCanvas(
            new PluginUiOwner("p", "P"), Descriptor(), _ => paints++);
        canvas.Invalidate();
        scopedCanvas.Invalidate();

        Assert.IsType<NoOpPluginCanvas>(canvas);
        Assert.IsType<NoOpPluginCanvas>(scopedCanvas);
        Assert.Equal(0, paints);
    }

    [Fact]
    public void TheInertCanvasAnswersWithTheDescriptorAndKeepsWhatThePluginSets()
    {
        IPluginCanvas canvas = new NoOpPluginCanvas(Descriptor());

        Assert.Equal("hud", canvas.CanvasId);
        Assert.Equal((200, 100), (canvas.Width, canvas.Height));
        Assert.False(canvas.IsAvailable);
        Assert.False(canvas.IsVisible);
        Assert.Equal(PluginCanvasAnchor.BottomRight, canvas.Anchor);
        Assert.Equal(new PluginPoint(-10, -20), canvas.Offset);

        canvas.IsVisible = true;
        canvas.Anchor = PluginCanvasAnchor.Center;
        canvas.Offset = new PluginPoint(1, 2);
        canvas.Invalidate();

        Assert.True(canvas.IsVisible);
        Assert.Equal(PluginCanvasAnchor.Center, canvas.Anchor);
        Assert.Equal(new PluginPoint(1, 2), canvas.Offset);
        Assert.False(((NoOpPluginCanvas)canvas).IsDisposed);
        canvas.Dispose();
        Assert.True(((NoOpPluginCanvas)canvas).IsDisposed);
    }

    [Fact]
    public void TheFakeHostAcceptsACanvasInertly()
    {
        var host = new FakePluginHost();
        int paints = 0;

        IPluginCanvas canvas = host.Ui.RegisterCanvas(new PluginCanvasDescriptor("map", 8, 8), _ => paints++);
        canvas.Invalidate();

        Assert.False(canvas.IsAvailable);
        Assert.Equal(0, paints);
    }

    [Fact]
    public void WhiteAndTransparentAreWhatTheyClaim()
    {
        Assert.Equal(new PluginColor(255, 255, 255, 255), PluginColor.White);
        Assert.Equal(new PluginColor(0, 0, 0, 0), PluginColor.Transparent);
    }
}
