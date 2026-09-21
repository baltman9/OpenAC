using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// The four rules a full-screen non-interactive overlay has to follow, baked
/// into one host so no future overlay has to rediscover them: anchors off,
/// rectangle equal to the viewport, click-through all the way down, and a
/// z-order above the debug overlays but under every real window.
/// </summary>
public sealed class UiOverlayHostTests
{
    private static UiRoot Root() => new() { Width = 800f, Height = 600f };

    [Fact]
    public void TheBandSitsAboveTheDebugOverlaysAndUnderEveryWindow()
    {
        Assert.True(
            UiOverlayZOrder.DebugOverlayCeiling < UiOverlayZOrder.BandFloor,
            "the band must start above the overlays that mount on the root");
        Assert.True(UiOverlayZOrder.BandFloor <= UiOverlayZOrder.SharedHostRoot);
        Assert.True(UiOverlayZOrder.SharedHostRoot <= UiOverlayZOrder.BandCeiling);
        Assert.True(
            UiOverlayZOrder.BandCeiling < UiOverlayZOrder.WindowFloor,
            "nothing in the band may reach a real window's z-order");
    }

    [Fact]
    public void TheHostIsPaintedBeforeAWindowAndAfterADebugOverlay()
    {
        UiRoot root = Root();
        var debugOverlay = new UiPanel
        {
            Name = "ADebugOverlay",
            ZOrder = UiOverlayZOrder.DebugOverlayCeiling,
        };
        var window = new UiPanel { Name = "AWindow", ZOrder = UiOverlayZOrder.WindowFloor };
        root.AddChild(debugOverlay);
        root.AddChild(window);

        UiOverlayHost host = UiOverlayHost.Mount(root);

        UiElement[] backToFront = root.ChildrenBackToFrontSnapshot();
        int overlayIndex = Array.IndexOf(backToFront, debugOverlay);
        int hostIndex = Array.IndexOf(backToFront, host.Root);
        int windowIndex = Array.IndexOf(backToFront, window);
        Assert.True(overlayIndex < hostIndex, "the host draws over the debug overlay");
        Assert.True(hostIndex < windowIndex, "the host draws under the window");
    }

    [Fact]
    public void TheRootTakesItsRectangleFromTheInterfaceRootAtMount()
    {
        UiRoot root = Root();

        UiOverlayHost host = UiOverlayHost.Mount(root);

        // Children are clipped to their parent by default, so a host left at
        // zero size would silently show nothing until someone set a viewport.
        Assert.Equal(0f, host.Root.Left);
        Assert.Equal(0f, host.Root.Top);
        Assert.Equal(800f, host.Root.Width);
        Assert.Equal(600f, host.Root.Height);
        Assert.Equal(AnchorEdges.None, host.Root.Anchors);
        Assert.True(host.Root.ClickThrough);
    }

    [Fact]
    public void ANewViewportReachesTheRootAndEveryLayerIncludingOnesAddedLater()
    {
        UiOverlayHost host = UiOverlayHost.Mount(Root());
        UiOverlayLayer first = host.AddLayer("First");

        host.SetViewport(new Vector2(1920f, 1080f));
        UiOverlayLayer second = host.AddLayer("Second");

        foreach (UiElement element in new UiElement[] { host.Root, first, second })
        {
            Assert.Equal(0f, element.Left);
            Assert.Equal(0f, element.Top);
            Assert.Equal(1920f, element.Width);
            Assert.Equal(1080f, element.Height);
        }
    }

    [Fact]
    public void ALayerStartsHiddenClickThroughAnchorlessAndWithoutChrome()
    {
        UiOverlayHost host = UiOverlayHost.Mount(Root());

        UiOverlayLayer layer = host.AddLayer("Layer");

        Assert.False(layer.Visible);
        Assert.True(layer.ClickThrough);
        Assert.Equal(AnchorEdges.None, layer.Anchors);
        Assert.Equal(Vector4.Zero, layer.BackgroundColor);
        Assert.Equal(Vector4.Zero, layer.BorderColor);
        Assert.Same(host.Root, layer.Parent);
    }

    [Fact]
    public void AddingASubtreeToALayerMakesEveryElementInItClickThrough()
    {
        UiOverlayHost host = UiOverlayHost.Mount(Root());
        UiOverlayLayer layer = host.AddLayer("Layer");
        var branch = new UiPanel { Name = "Branch" };
        var leaf = new UiPanel { Name = "Leaf" };
        branch.AddChild(leaf);
        Assert.False(branch.ClickThrough);
        Assert.False(leaf.ClickThrough);

        // Click-through has no inheritance: a transparent parent with an
        // ordinary child still swallows clicks over the child's rectangle.
        layer.AddChild(branch);

        Assert.True(branch.ClickThrough);
        Assert.True(leaf.ClickThrough);
    }

    [Fact]
    public void TwoLayersDoNotShareVisibilityOrSiblingOrder()
    {
        UiOverlayHost host = UiOverlayHost.Mount(Root());

        UiOverlayLayer first = host.AddLayer("First");
        UiOverlayLayer second = host.AddLayer("Second");
        first.Visible = true;

        Assert.True(first.Visible);
        Assert.False(second.Visible);
        Assert.Equal(
            ["First", "Second"],
            host.Root.Children.Select(child => child.Name ?? string.Empty).ToArray());
    }
}
