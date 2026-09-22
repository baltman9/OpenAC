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

    /// <summary>
    /// Where the band may sit, measured against the z-orders root children
    /// really take. Measuring it against the band's own constants is
    /// circular: it says the band is where the band says it is, and stays
    /// green however far the band moves.
    ///
    /// <para>Two things put a z-order on a root child. An imported layout
    /// root takes one computed from its authored level and read order, which
    /// is a real number this reads out of the importer rather than
    /// restating; and anything built in code keeps the element default
    /// unless it sets one, which is what a window does -- registration sets
    /// no z-order at all.</para>
    /// </summary>
    [Fact]
    public void TheBandFitsBetweenTheZOrdersRealRootChildrenTake()
    {
        // A window's outer frame, imported at the authored level everything
        // in a window layout uses, and the same frame built in code.
        int importedWindowRoot = ImportedZOrder(zLevel: 0, readOrder: 0);
        int laterImportedWindowRoot = ImportedZOrder(zLevel: 0, readOrder: 40);
        int codeBuiltWindow = new UiPanel().ZOrder;

        // One authored level further back is where the click-through
        // overlays that mount straight on the root sit, and the band has to
        // be in front of those.
        int oneLevelBack = ImportedZOrder(zLevel: 1, readOrder: 0);

        foreach (int window in new[]
            { importedWindowRoot, laterImportedWindowRoot, codeBuiltWindow })
        {
            Assert.True(
                UiOverlayZOrder.BandCeiling < window,
                $"the band's front {UiOverlayZOrder.BandCeiling} must stay "
                + $"under a real window at {window}");
        }

        Assert.True(
            oneLevelBack < UiOverlayZOrder.BandFloor,
            $"the band's back {UiOverlayZOrder.BandFloor} must stay over the "
            + $"click-through overlays at {oneLevelBack}");
        Assert.InRange(
            UiOverlayZOrder.SharedHostRoot,
            UiOverlayZOrder.BandFloor,
            UiOverlayZOrder.BandCeiling);
    }

    /// <summary>
    /// The host root takes the very back of the band. All of the band's room
    /// is then in front of the host, which is where hosted layers go, and the
    /// host itself stays as far as the band allows from the z-orders real
    /// windows take -- which nothing enforces, so the distance is the only
    /// protection there is.
    ///
    /// Mutation check (2026-09-21): moving the shared host root up to the
    /// band ceiling turned this red and left every other overlay test green.
    /// </summary>
    [Fact]
    public void TheSharedHostRootSitsAtTheVeryBackOfTheBand()
    {
        Assert.Equal(UiOverlayZOrder.BandFloor, UiOverlayZOrder.SharedHostRoot);
        Assert.True(UiOverlayZOrder.BandFloor < UiOverlayZOrder.BandCeiling);
    }

    [Fact]
    public void TheHostIsPaintedBeforeRealWindowsAndAfterADebugOverlay()
    {
        UiRoot root = Root();
        // The z-orders these take are the real ones: the overlay's is what
        // the importer gives content one authored level back, the imported
        // window's is what it gives a window's own root, and the code-built
        // window never sets one -- registering it does not either.
        var debugOverlay = new UiPanel
        {
            Name = "ADebugOverlay",
            ZOrder = ImportedZOrder(zLevel: 1, readOrder: 0),
            ClickThrough = true,
        };
        var importedWindow = new UiPanel
        {
            Name = "AnImportedWindow",
            ZOrder = ImportedZOrder(zLevel: 0, readOrder: 0),
        };
        var codeBuiltWindow = new UiPanel { Name = "ACodeBuiltWindow" };
        root.AddChild(debugOverlay);
        root.AddChild(importedWindow);
        root.AddChild(codeBuiltWindow);
        root.RegisterWindow("imported", importedWindow);
        root.RegisterWindow("code-built", codeBuiltWindow);

        UiOverlayHost host = UiOverlayHost.Mount(root);

        UiElement[] backToFront = root.ChildrenBackToFrontSnapshot();
        int overlayIndex = Array.IndexOf(backToFront, debugOverlay);
        int hostIndex = Array.IndexOf(backToFront, host.Root);
        Assert.True(overlayIndex < hostIndex, "the host draws over the debug overlay");
        Assert.True(
            hostIndex < Array.IndexOf(backToFront, importedWindow),
            "the host draws under an imported window root");
        Assert.True(
            hostIndex < Array.IndexOf(backToFront, codeBuiltWindow),
            "the host draws under a window built in code");

        // A window brought to the front only ever moves further up.
        root.BringToFront(importedWindow);
        Assert.True(host.Root.ZOrder < importedWindow.ZOrder);
    }

    /// <summary>
    /// What the importer gives an element authored at this level and read
    /// order -- asked of the importer itself, so the band is pinned against
    /// the rule that really runs and not against a number copied out of it.
    /// </summary>
    private static int ImportedZOrder(uint zLevel, uint readOrder) =>
        DatWidgetFactory.Create(
            new ElementInfo { Type = 3, ReadOrder = readOrder, ZLevel = zLevel },
            static _ => (0u, 0, 0),
            null)!.ZOrder;

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
